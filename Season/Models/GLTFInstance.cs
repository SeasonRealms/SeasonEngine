// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
// Disambiguate from Microsoft.Maui.Controls.Image: in this file, Image always refers to a glTF image entity.
using Image = SharpGLTF.Schema2.Image;

namespace Season.Models;

/// <summary>
/// Shared utility set for "splittable multi-mesh GLB assets -> InstancedMesh3D GPU instancing".
/// This extracts the common contract used by pipelines such as Mountains and Rocks.
/// Each node with a Mesh inside the asset is treated as an independently instantiable variant template.
/// This class provides five atomic capabilities for reuse by similar multi-mesh combination panels:
/// loading, enumerating mesh nodes, baking centered template geometry node by node,
/// locating material-channel images, and decoding embedded textures in memory without writing to disk.
///
/// Standard usage, where work starts on a background thread and control creation is finalized on the UI thread:
/// 1) <see cref="LoadGlbAsync"/>: read the byte stream through StorageService and parse with SharpGLTF, skipping validation.
/// 2) <see cref="GetMeshNodes"/>: enumerate nodes with Mesh in stable Mesh.LogicalIndex order.
///    Callers can still sort again if needed, for example Mountains sorts by name.
/// 3) <see cref="BakeMeshNode"/>: bake the node-chain RH world transform into the engine LH convention
///    and center it by AABB, where the template anchor is the geometric center of the box.
///    When the asset has no TANGENT attribute, set generateTangents=true and let <see cref="GenerateTangents"/> rebuild the TBN basis.
/// 4) <see cref="FindChannelImage"/> plus <see cref="ExtractEmbeddedImageAsync"/>:
///    locate material-channel images and decode embedded textures in memory without writing them to disk,
///    with reference deduplication so shared image sources are decoded only once.
///
/// All members perform CPU-side parsing, transforms, and image decoding only, so they are safe on background threads.
/// InstancedMesh3D creation and AddControl are UI-resource operations and must be finalized by the caller on the main thread,
/// matching the same contract used by Mountains and Rocks.
/// </summary>
public static class GLTFInstance
{
    /// <summary>
    /// Reads GLB bytes from local storage and parses them into a SharpGLTF model.
    /// ValidationMode.Skip tolerates non-fatal validation issues in third-party assets,
    /// matching the original Mountains and Rocks behavior.
    /// </summary>
    public static async Task<ModelRoot> LoadGlbAsync(string path)
    {
        var glbBytes = await StorageService.LoadBytesAsync(path);

        using var stream = new MemoryStream(glbBytes);

        return ModelRoot.ReadGLB(stream, new ReadSettings() { Validation = ValidationMode.Skip });
    }

    /// <summary>
    /// Enumerates all nodes with Mesh in the scene, which are the independently instantiable variant templates.
    /// Results are sorted by Mesh.LogicalIndex, meaning the original mesh-array order inside the asset,
    /// which is stable and aligned with exporter semantics.
    /// Node-name sorting is not valid because in Sketchfab-style assets the lexical order of Object_N names
    /// does not match the asset order.
    /// </summary>
    public static List<Node> GetMeshNodes(ModelRoot model)
        => model.LogicalNodes
            .Where(n => n.Mesh != null)
            .OrderBy(n => n.Mesh.LogicalIndex)
            .ToList();

    /// <summary>
    /// Bakes a variant node into instanced template geometry centered at the origin.
    /// GLTFTools.LoadMeshPrimitive provides local vertices in the engine convention, with Z already mirrored from RH to LH.
    /// The method then restores RH, applies the full RH world matrix along the node chain,
    /// including Sketchfab correction matrices and node translation,
    /// mirrors Z a second time to obtain LH world coordinates, and finally subtracts the AABB center.
    /// The geometric center of the local bounding box therefore becomes the origin,
    /// which is exactly the template anchor, so instance position maps directly to the template box center
    /// and the rotation pivot is naturally centered.
    /// The winding inversion is intentionally preserved: RH CCW becomes outward CW after Z mirroring,
    /// matching the engine rule that outward-facing CW is front-facing, consistent with the Model pipeline.
    /// </summary>
    /// <param name="node">Variant node with Mesh. Uses <c>Primitives[0]</c>.</param>
    /// <param name="generateTangents">
    /// Set to true when the asset does not include a TANGENT attribute.
    /// Normal mapping requires a valid TBN basis, so tangents are generated at the end of baking
    /// using the Lengyel method. See <see cref="GenerateTangents"/>.
    /// Set to false when the asset already provides TANGENT, in which case only tangent W,
    /// the bitangent sign, is flipped according to handedness so TBN parity stays consistent.
    /// </param>
    public static (Vertex[] Vertices, ushort[] Indices) BakeMeshNode(Node node, bool generateTangents = false)
    {
        var primitive = node.Mesh.Primitives[0];

        var (vertices, indices) = GLTFTools.LoadMeshPrimitive(primitive);

        var world = node.WorldMatrix;   // RH world transform including the full parent chain.

        // Composite linear transform L = S * world, where S is the Z mirror and det(S) = -1.
        // When det(L) < 0, handedness flips, so tangent W, the bitangent sign, must also flip
        // to preserve TBN parity.
        bool flipTangentW = Determinant3x3(world) > 0f;

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);

        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];

            // LH local -> restore RH -> apply RH world matrix -> mirror Z again -> LH world.
            var posRh = new Vector3(v.Position.X, v.Position.Y, -v.Position.Z);
            var posWorldRh = Vector3.Transform(posRh, world);
            v.Position = new Vector3(posWorldRh.X, posWorldRh.Y, -posWorldRh.Z);

            var nRh = new Vector3(v.Normal.X, v.Normal.Y, -v.Normal.Z);
            var nWorldRh = Vector3.TransformNormal(nRh, world);
            v.Normal = Vector3.Normalize(new Vector3(nWorldRh.X, nWorldRh.Y, -nWorldRh.Z));

            var tRh = new Vector3(v.Tangent.X, v.Tangent.Y, -v.Tangent.Z);
            var tWorldRh = Vector3.TransformNormal(tRh, world);
            v.Tangent = new Vector4(tWorldRh.X, tWorldRh.Y, -tWorldRh.Z, flipTangentW ? -v.Tangent.W : v.Tangent.W);

            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);

            vertices[i] = v;
        }

        var center = (min + max) * 0.5f;

        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            v.Position -= center;
            vertices[i] = v;
        }

        var ushortIndices = new ushort[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            ushortIndices[i] = (ushort)indices[i];

        var baked = vertices.ToArray();

        // When the asset has no TANGENT and LoadMeshPrimitive provides zero tangents,
        // rebuild them from UV gradients.
        // Generation happens in final LH space, so no extra handedness correction is needed
        // and W stays at +1, matching the shader contract.
        if (generateTangents)
            GenerateTangents(baked, ushortIndices);

        return (baked, ushortIndices);
    }

    /// <summary>
    /// Generates tangents using the Lengyel method.
    /// For each triangle, it solves tangent direction from position deltas and UV deltas
    /// and accumulates the result per vertex.
    /// A final Gram-Schmidt step orthogonalizes against the normal.
    /// Degenerate UVs, where accumulation collapses to zero, fall back to any direction orthogonal to the normal.
    /// W stays at +1, matching the shader contract where bitangent = cross(N, T.xyz) * T.w.
    /// </summary>
    public static void GenerateTangents(Vertex[] vertices, ushort[] indices)
    {
        var accumulated = new Vector3[vertices.Length];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];

            var p0 = vertices[i0].Position;
            var p1 = vertices[i1].Position;
            var p2 = vertices[i2].Position;
            var uv0 = vertices[i0].TexCoord;
            var uv1 = vertices[i1].TexCoord;
            var uv2 = vertices[i2].TexCoord;

            var e1 = p1 - p0;
            var e2 = p2 - p0;
            var d1 = uv1 - uv0;
            var d2 = uv2 - uv0;

            float denom = d1.X * d2.Y - d2.X * d1.Y;
            float r = MathF.Abs(denom) > 1e-8f ? 1f / denom : 0f;

            var tangent = new Vector3(
                (d2.Y * e1.X - d1.Y * e2.X) * r,
                (d2.Y * e1.Y - d1.Y * e2.Y) * r,
                (d2.Y * e1.Z - d1.Y * e2.Z) * r);

            accumulated[i0] += tangent;
            accumulated[i1] += tangent;
            accumulated[i2] += tangent;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var n = v.Normal;
            var t = accumulated[i] - n * Vector3.Dot(n, accumulated[i]);   // Gram-Schmidt orthogonalization.

            if (t.LengthSquared() < 1e-10f)
            {
                // Degenerate fallback: pick any axis not parallel to the normal
                // and derive an orthogonal direction through a cross product.
                var axis = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                t = Vector3.Cross(n, axis);
            }

            t = Vector3.Normalize(t);
            v.Tangent = new Vector4(t.X, t.Y, t.Z, 1f);
            vertices[i] = v;
        }
    }

    /// <summary>Locates the primary texture image for a specific material channel. Returns null if material, channel, or texture is missing.</summary>
    public static Image? FindChannelImage(Material? material, KnownChannel channel)
        => material?.FindChannel(channel.ToString()) is MaterialChannel ch
            ? ch.Texture?.PrimaryImage
            : null;

    /// <summary>
    /// Decodes an embedded GLB image, stored as a PNG or JPEG compressed byte stream,
    /// into an in-memory RGBA8 pixel buffer and wraps it as a Surface texture override source,
    /// allowing direct GPU upload without writing a file to disk.
    /// This works on all backends, including Web where no local filesystem exists,
    /// matching the same replacement pattern used by procedural Ground and Beach textures.
    /// Returns default when the image is missing so the slot remains disabled.
    /// </summary>
    /// <param name="savedImages">
    /// Optional reference-deduplication table mapping Image to decoded pixel source.
    /// When multiple slots share the same image source, such as the common glTF pattern
    /// where AO and MR reuse one image, decoding happens only once and the pixel buffer is reused.
    /// No deduplication is performed when null.
    /// </param>
    /// <remarks>
    /// Pixels are decoded through ImageUtils, which always yields RGBA8 under the shared contract
    /// used by embedded images in the Model loader, and then normalized into NativeImageData.
    /// NativeImageData owns its byte array and Dispose has no side effects.
    /// The same returned value can therefore be safely passed to multiple Surface.TextureOverride consumers.
    /// Every backend copies pixels immediately during Load, see the per-backend EnsureSurfaceTexture path,
    /// so usages do not interfere with each other.
    /// Each consuming control still registers its own GPU texture under a composed name,
    /// and platform dictionaries do not deduplicate across controls.
    /// </remarks>
    public static async Task<TextureUpdateSource> ExtractEmbeddedImageAsync(Image? image, IDictionary<Image, TextureUpdateSource>? savedImages = null)
    {
        if (image == null || image.Content.IsEmpty)
            return default;

        if (savedImages != null && savedImages.TryGetValue(image, out var cached))
            return cached;

        // Image.Content is a MemoryImage, and Content.Open() exposes a view over the PNG or JPEG compressed byte stream.
        using var stream = image.Content.Open();
        using var decoded = await ImageUtils.GetImageFromStreamAsync(stream, null);
        var pixels = new NativeImageData(decoded.Width, decoded.Height, decoded.PixelSpan.ToArray());

        var source = TextureUpdateSource.FromImage(pixels);

        if (savedImages != null)
            savedImages[image] = source;

        return source;
    }

    /// <summary>Determinant of the upper-left 3x3 linear part. Row-vector or column-vector convention does not affect the sign, and it is used only for handedness tests.</summary>
    static float Determinant3x3(Matrix4x4 m)
    {
        return m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
             - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
             + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
    }
}
