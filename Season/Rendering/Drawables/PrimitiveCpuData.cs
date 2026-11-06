// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering.Drawables;

/// <summary>
/// The CPU side of a primitive: geometry, the material description, and the small bookkeeping every backend keeps
/// beside its GPU handles. Written once so DirectX, Vulkan and Metal cannot drift in what a primitive knows about
/// itself, and so a fix to that knowledge does not have to be made three times.
///
/// The split is deliberate. Everything here is backend-agnostic — <typeparamref name="TMat"/> is a backend material
/// block satisfying <see cref="IMaterialFields"/>, <typeparamref name="TTex"/> is a backend texture handle — while
/// everything the backend owns (buffers, views, descriptor sets, mapped pointers) stays in the derived
/// <c>PrimitiveData</c>. The two remain a single object, so every existing call site keeps working unchanged and the
/// GC still sees one allocation per primitive.
///
/// There are no virtual members here. The only instantiations are the three backends' concrete classes, so the
/// interface calls stay constrained and nothing needs generic virtual dispatch, which keeps the AOT targets (iOS,
/// Mac Catalyst, Android) free of an interpreter fallback.
/// </summary>
internal abstract class PrimitiveCpuData<TMat, TTex>
    where TMat : unmanaged, IMaterialFields
    where TTex : class
{
    // ---- Geometry ----

    /// <summary>Index into the host's instance stream; -1 for a primitive that is not instanced.</summary>
    public int InstanceStreamIndex = -1;

    public List<Vertex> Vertices = null!;

    public Vertex[]? BaseVertices;

    public List<GLTFMorphTarget>? MorphTargets;

    public GltfNodeBase? OwnerNode;

    public uint LastAppliedWeightsVersion;

    public uint[] Indices = null!;

    public bool Use32BitIndices;

    public bool DoubleSided;

    /// <summary>2-2 contract rule 7: GTAO exemption, synced from <c>Mesh3D.ExcludeFromAo</c>. The draw buckets use
    /// it to select the NoDepth PSO.</summary>
    public bool AoExempt;

    public Vector3 LocalBoundsCenter;

    /// <summary>1-3: half extents of the primitive-local AABB, paired with <see cref="LocalBoundsCenter"/> for
    /// primitive-level and instance-level culling.</summary>
    public Vector3 LocalBoundsExtents;

    /// <summary>2-3 contract rule 6: CPU shadow copy of the previous frame's world matrix, stored without
    /// transpose. The matrix buffers are N-buffered upload resources, so previous frames must never be read back
    /// from them; history has to come from this field. All-zero means no history yet, the first-frame case, and the
    /// shader falls back to the current world matrix.</summary>
    public System.Numerics.Matrix4x4 PrevWorldMatrix;

    // ---- Material ----

    // PBR textures, one field per slot. The slot-to-field mapping lives in Texture() below, so the switch exists once
    // instead of three times.
    public TTex BaseColorTexture = null!;

    public TTex NormalTexture = null!;

    public TTex MetallicRoughnessTexture = null!;

    public TTex OcclusionTexture = null!;

    public TTex EmissiveTexture = null!;

    /// <summary>The backend material constant block for this primitive. The field is named after its type because
    /// every backend already spells it that way, so all existing <c>primitive.MaterialParams</c> accesses keep
    /// resolving unchanged after the move.</summary>
    public TMat MaterialParams;

    /// <summary>Original glTF material BaseColor.W (alpha), multiplied with Model.Alpha to produce the final
    /// opacity multiplier.</summary>
    public float OriginalBaseColorAlpha = 1.0f;

    /// <summary>Original Surface BaseColor frozen at load time, used as the multiplicative baseline for
    /// SyncColorTint. RGB is tinted while W stays untouched.</summary>
    public Vector4 OriginalBaseColor = Vector4.One;

    /// <summary>glTF material name of the part this primitive draws; null when the asset has no material. This is
    /// the primary key <c>Model.SetPartColor</c> resolves against.</summary>
    public string? PartName;

    /// <summary>Name of the node that owns this primitive, the fallback key for <c>Model.SetPartColor</c>.</summary>
    public string? PartNodeName;

    /// <summary>BaseColor.rgb as loaded (after the MaterialColor tint, before any part override). It is what
    /// SyncPartColors restores when a part override is cleared, so an absolute override never needs to re-derive the
    /// authored value.</summary>
    public Vector3 PartBaseColor = Vector3.One;

    /// <summary>Original glTF material AlphaCutoff. When Model.Alpha is less than 1, this is scaled proportionally
    /// by alpha so a MASK material is not clipped away entirely by <c>clip(alpha - alphaCutoff)</c>.</summary>
    public float OriginalAlphaCutoff = 0.5f;

    /// <summary>Whether the material is transparent, determined from the glTF material's AlphaMode: BLEND is
    /// transparent and uses the Transparent PSO, MASK uses the Opaque PSO plus shader discard, and OPAQUE uses the
    /// Opaque PSO.</summary>
    public bool IsTransparent;

    // ---- Texture slots ----

    /// <summary>
    /// Turns a PBR slot into the field that holds it. Returning by reference means both directions go through this
    /// one switch: <c>var t = primitive.Texture(slot)</c> reads, <c>primitive.Texture(slot) = tex</c> writes (the
    /// Vulkan call site also needs the write to happen before its descriptor sets are rebuilt).
    ///
    /// An unknown slot falls back to the base-color field, which is what the three per-backend
    /// <c>GetTextureBySlot</c> switch expressions did before this move.
    /// </summary>
    public ref TTex Texture(TextureSlot slot)
    {
        switch (slot)
        {
            case TextureSlot.Normal: return ref NormalTexture;
            case TextureSlot.MetallicRoughness: return ref MetallicRoughnessTexture;
            case TextureSlot.Occlusion: return ref OcclusionTexture;
            case TextureSlot.Emissive: return ref EmissiveTexture;
            default: return ref BaseColorTexture;
        }
    }
}
