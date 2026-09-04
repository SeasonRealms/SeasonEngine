// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// GLB/glTF model control.
/// World transforms follow the unified positioning model: (PosX, PosY, PosZ) is the anchor
/// at the geometric center of the raw bounding box, Width/Height/Depth are per-axis scales,
/// and Rotation is around Y with the anchor as pivot, see <see cref="Mesh3DBase"/>.
/// When position needs to be expressed relative to the model-local origin, convert with
/// <see cref="Mesh3DBase.AnchorWorldOffset"/>.
/// Name is normalized to Control.Name, and platform dictionaries use (Name, ID) as the key.
/// </summary>
public class Model : Mesh3DBase
{
    public System.Numerics.Vector4? MaterialColor;

    public float Time { get; set; }

    public bool Unlit { get; set; } = false;

    public bool Positive { get; set; } = true;

    public System.Numerics.Vector3 Size;

    public float OriginalScale { get; set; } = 1f;

    /// <summary>
    /// Effective uniform scale, preserving the historical meaning of
    /// "representative axis of per-axis scaling x OriginalScale".
    /// After default dimensions have been settled, the representative axis of ComputedScale becomes
    /// the effective scale value, used by consumers that expect uniform scale such as KHR punctual-light range.
    /// Falls back to OriginalScale before bounds have been established.
    /// </summary>
    public float RealScale
    {
        get
        {
            var local = LocalSize;
            var scale = ComputedScale;
            if (local.X > 1e-6f) return scale.X;
            if (local.Y > 1e-6f) return scale.Y;
            if (local.Z > 1e-6f) return scale.Z;
            return OriginalScale;
        }
    }

    float _rotation;
    public float Rotation
    {
        get => _rotation;
        set
        {
            if (_rotation != value)
            {
                _rotation = value;
                Changed = true;
            }
        }
    }

    /// <summary>Rotation injection point: around the world Y axis, with the anchor as pivot. See <see cref="Mesh3DBase.BuildWorldMatrix"/>.</summary>
    protected override System.Numerics.Matrix4x4 GetRotationMatrix() => System.Numerics.Matrix4x4.CreateRotationY(Rotation);

    /// <summary>Default size-settling factor equals OriginalScale, so default size = local size x OriginalScale, preserving the normalized loaded appearance.</summary>
    protected override float DefaultSizeFactor => OriginalScale;

    /// <summary>
    /// 1-2: Punctual lights imported from glTF KHR_lights_punctual.
    /// They remain in model-local space, with position, direction, and range all stored before userTransform is applied.
    /// They are parsed and filled by GltfAsset.Load. Intensity is stored in original candela units,
    /// and scaling is applied per frame in <see cref="AppendWorldLights"/>.
    /// </summary>
    public List<GpuLight> ImportedPunctualLights { get; internal set; } = new();

    /// <summary>
    /// 1-2: Runtime intensity scale for punctual lights imported by this model.
    /// 0 turns them all off, 1 keeps original brightness, and intermediate values dim them.
    /// It is multiplied by the global RenderQuality.KhrLightIntensityScale knob and applied every frame
    /// in <see cref="AppendWorldLights"/>. When it is 0, light appending is skipped entirely,
    /// so no punctual slots are consumed in SceneLightParams.
    /// </summary>
    public float LightIntensityScale { get; set; } = 1f;

    /// <summary>Transparent-sort reference point: the unified positioning model uses the anchor's world position.</summary>
    public override System.Numerics.Vector3 TransparentSortPosition => new System.Numerics.Vector3(PosX, PosY, PosZ);
    public override bool EnableTransparentSort => Alpha < 1f;

    protected override bool HasContent => !Name.IsNullOrWhiteSpace();

    /// <summary>
    /// Cross-platform animation data source.
    /// GltfAsset is a purely managed entity in the glTF parsing domain, holding animation clips and player state
    /// independently of any graphics backend. Platform wrappers inject it during Load for direct-load paths,
    /// or during CreateInstance for shared-template instancing paths.
    /// The control and the backend's per-frame sampling share the same asset instance, so animation queries and switches
    /// are performed directly by the control instead of being routed back through IGraphics.
    /// Null before loading and after disposal.
    /// </summary>
    internal Season.Models.GltfAsset Asset { get; set; }

    // Runtime material overrides. These are property-driven and consumed internally by UpdateModel, then reset to null.

    /// <summary>Override the BaseColor texture. Supports either a file path or pixel data through implicit conversion. Automatically reset to null after consumption.</summary>
    public TextureUpdateSource BaseColorOverride { get; set; }

    /// <summary>Override the Normal texture. Supports either a file path or pixel data through implicit conversion. Automatically reset to null after consumption.</summary>
    public TextureUpdateSource NormalOverride { get; set; }

    /// <summary>Override the MetallicRoughness texture. Supports either a file path or pixel data through implicit conversion. Automatically reset to null after consumption.</summary>
    public TextureUpdateSource MetallicRoughnessOverride { get; set; }

    /// <summary>Override the Occlusion texture. Supports either a file path or pixel data through implicit conversion. Automatically reset to null after consumption.</summary>
    public TextureUpdateSource OcclusionOverride { get; set; }

    /// <summary>Override the Emissive texture. Supports either a file path or pixel data through implicit conversion. Automatically reset to null after consumption.</summary>
    public TextureUpdateSource EmissiveTextureOverride { get; set; }

    /// <summary>Override the Metallic factor in the 0-1 range. Automatically reset to null after consumption.</summary>
    public float? MetallicOverride { get; set; }

    /// <summary>Override the Roughness factor in the 0-1 range. Automatically reset to null after consumption.</summary>
    public float? RoughnessOverride { get; set; }

    /// <summary>Override the Emissive color as RGB intensity. Automatically reset to null after consumption.</summary>
    public Vector4? EmissiveFactorOverride { get; set; }

    public override async Task<bool> Load()
    {
        await Graphics.Instance.LoadModel(this);

        return true;
    }

    public bool SetModel(string name, bool forceReload = false)
    {
        bool sameName = _name == name;

        if (!forceReload && sameName)
        {
            return false;
        }

        _name = name;
        Ready = false;
        Changed = true;

        DeviceServices.BaseApp?.RequestLoad(this);

        return true;
    }

    public bool Update(float time, string? name = null, float? alpha = null)
    {
        var result = base.Update(time, alpha: alpha);

        if (name is null)
        {

        }
        else
        {
            SetModel(name);
        }

        if (Ready && HasContent)
        {
            Graphics.Instance.UpdateModel(this, time);
        }

        return result;
    }

    public string? SwitchToNextAnimation()
    {
        if (Name.IsNullOrWhiteSpace() || !Ready || Asset == null)
            return null;

        return Asset.PlayNextAnimation();
    }

    /// <summary>List of animation names contained in the model, from glTF `animations[].name`. Available after loading; returns an empty list when there are no animations.</summary>
    public IReadOnlyList<string> GetAnimationNames()
    {
        if (Name.IsNullOrWhiteSpace() || !Ready || Asset == null)
            return Array.Empty<string>();

        return Asset.GetAnimationNames();
    }

    /// <summary>List of animation metadata contained in the model, including name and duration. See <see cref="Season.Models.ModelAnimationInfo"/>. Returns an empty list when there are no animations.</summary>
    public IReadOnlyList<Season.Models.ModelAnimationInfo> GetAnimations()
    {
        if (Name.IsNullOrWhiteSpace() || !Ready || Asset == null)
            return Array.Empty<Season.Models.ModelAnimationInfo>();

        return Asset.GetAnimations();
    }

    /// <summary>Name of the animation currently playing. Returns null when not loaded or when there is no animation.</summary>
    public string? GetCurrentAnimationName()
    {
        if (Name.IsNullOrWhiteSpace() || !Ready || Asset == null)
            return null;

        return Asset.GetCurrentAnimationName();
    }

    /// <summary>Switches playback to the animation with the given name and returns the name actually activated. Returns null when not loaded or when the name does not exist.</summary>
    public string? PlayAnimation(string animationName)
    {
        if (Name.IsNullOrWhiteSpace() || !Ready || Asset == null)
            return null;

        Asset.PlayAnimation(animationName);
        return Asset.GetCurrentAnimationName();
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            Graphics.Instance.DrawModel(this);

            result = true;
        }

        return result;
    }

    /// <summary>
    /// Surface-accurate picking at v2 mesh granularity.
    /// After broad-phase culling against LocalBounds, which includes 1.5x conservative expansion for animation,
    /// this performs per-node PickMesh ray-triangle narrow-phase testing.
    /// Skinned hit candidates are skinned on the fly using the current-frame bones, reusing the same bone palette
    /// and node world matrices as rendering, so results match screen output bit for bit.
    /// Morph primitives are approximated with base geometry, which is the intended v1 boundary because deformation
    /// does not affect the primary picking use case.
    /// When Asset has not been injected or there is no PickMesh data at all, the base class falls back to OBB.
    /// If data exists but narrow phase misses, that is a real miss: empty model space is no longer selected by mistake,
    /// and overlapping objects resolve by nearest surface, meaning whichever is closest to the screen wins.
    /// </summary>
    public override bool TryPickSurface(Vector3 rayOrigin, Vector3 rayDirection, out float distance)
    {
        if (Asset == null)
            return base.TryPickSurface(rayOrigin, rayDirection, out distance);

        var userTransform = BuildWorldMatrix();
        if (!TryPickBroadPhase(rayOrigin, rayDirection, userTransform))
        {
            distance = float.MaxValue;
            return false;
        }

        bool hasData = false;
        bool hit = false;
        float bestDistance = float.MaxValue;

        foreach (var node in Asset.gltfNodes)
        {
            var meshes = node.PickMeshes;
            if (meshes.Count == 0)
                continue;

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh.Positions.Length < 3 || mesh.Indices.Length < 3)
                    continue;

                hasData = true;

                if (mesh.IsSkinned)
                {
                    int paletteOffset = Asset.GetSkinPaletteOffset(node.Skin);
                    var bones = Asset._animationPlayer.GetBoneMatricesArray();
                    if (paletteOffset < 0 || bones.Length == 0)
                        continue;

                    // Skinned mesh space is node-local, with the bones already carrying inverseMeshWorld semantics.
                    // The world matrix shares the same source as rendering: node.WorldTransform multiplied by the user transform.
                    var skinWorld = node.WorldTransform * userTransform;
                    var scratch = ArrayPool<Vector3>.Shared.Rent(mesh.Positions.Length);
                    try
                    {
                        Picking.SkinPositions(mesh.Positions, mesh.Joints, mesh.Weights, bones, paletteOffset,
                            scratch.AsSpan(0, mesh.Positions.Length));

                        if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, skinWorld,
                                scratch.AsSpan(0, mesh.Positions.Length), mesh.Indices, out var d)
                            && d < bestDistance)
                        {
                            bestDistance = d;
                            hit = true;
                        }
                    }
                    finally
                    {
                        ArrayPool<Vector3>.Shared.Return(scratch);
                    }
                }
                else
                {
                    var world = node.WorldTransform * userTransform;
                    if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, world, mesh.Positions, mesh.Indices, out var d)
                        && d < bestDistance)
                    {
                        bestDistance = d;
                        hit = true;
                    }
                }
            }
        }

        if (!hasData)
            return base.TryPickSurface(rayOrigin, rayDirection, out distance);

        if (!hit)
        {
            distance = float.MaxValue;
            return false;
        }

        distance = bestDistance;
        return true;
    }

    protected override void DrawShadowCore() => Graphics.Instance.DrawModelShadow(this);

    public override void Dispose()
    {
        base.Dispose();
        Graphics.Instance.DisposeModel(this);
        Asset = null;
    }

    /// <summary>
    /// User transform matrix from model space to world space.
    /// Since Phase 3 this has converged to the unified positioning entry point
    /// <see cref="Mesh3DBase.BuildWorldMatrix"/>, with the anchor as pivot.
    /// Model rendering paths across all backends, inherited GetWorldBounds, and AppendWorldLights
    /// all go through this entry point as a single source of truth.
    /// </summary>
    public System.Numerics.Matrix4x4 GetUserTransform() => BuildWorldMatrix();

    /// <summary>
    /// 1-2: Transforms the punctual lights imported by this model from local space to world space
    /// and appends them to the <paramref name="scene"/> Lights array.
    /// Position is transformed by userTransform. Range is multiplied by RealScale for uniform scaling.
    /// Intensity is multiplied by RenderQuality.KhrLightIntensityScale as a live runtime knob.
    /// Spot directions are transformed with the normal transform of userTransform and normalized.
    /// Lights beyond MaxLights are discarded.
    /// </summary>
    public void AppendWorldLights(ref SceneLightParams scene)
    {
        if (ImportedPunctualLights == null || ImportedPunctualLights.Count == 0)
            return;

        if (LightIntensityScale <= 0f)
            return;

        var userTransform = GetUserTransform();
        float scale = RealScale;
        float intensityScale = RenderQuality.Current.KhrLightIntensityScale * LightIntensityScale;

        int count = scene.LightCount;
        foreach (var src in ImportedPunctualLights)
        {
            if (count >= SceneLightParams.MaxLights)
                break;

            var localPos = new System.Numerics.Vector3(src.PosRange.X, src.PosRange.Y, src.PosRange.Z);
            var worldPos = System.Numerics.Vector3.Transform(localPos, userTransform);

            var light = src;
            light.PosRange = new System.Numerics.Vector4(worldPos.X, worldPos.Y, worldPos.Z, src.PosRange.W * scale);
            light.ColorIntensity = new System.Numerics.Vector4(
                src.ColorIntensity.X, src.ColorIntensity.Y, src.ColorIntensity.Z, src.ColorIntensity.W * intensityScale);

            if (src.DirType.W == GpuLight.TypeSpot)
            {
                var localDir = new System.Numerics.Vector3(src.DirType.X, src.DirType.Y, src.DirType.Z);
                var worldDir = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(localDir, userTransform));
                light.DirType = new System.Numerics.Vector4(worldDir.X, worldDir.Y, worldDir.Z, GpuLight.TypeSpot);
            }

            scene.Lights[count] = light;
            count++;
        }

        scene.LightCount = count;
    }
}
