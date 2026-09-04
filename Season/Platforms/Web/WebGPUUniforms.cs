// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// Uniform buffer layout definition. Precisely aligned with the WGSL <c>Uniforms</c> struct (GLSL std140 layout).
/// Both vec4 and mat4 are aligned to 16 bytes, for a total size of 108 floats = 432 bytes.
/// </summary>
public static class WebGPUUniformLayout
{
    // Matrix section (16 floats per matrix, stored in transposed row-major order).
    /// <summary>mat4x4f world</summary>
    public const int World = 0;
    /// <summary>mat4x4f view</summary>
    public const int View = 16;
    /// <summary>mat4x4f projection</summary>
    public const int Projection = 32;

    // 2-3 history section (contract clause 6): the 9 retired vec4 slots from 1-2
    // (old cameraPos + 4 light positions + 4 light colors; lighting now comes from the shared UBO at binding(10))
    // are repurposed here for previous-frame data. Those 36 floats exactly fill the retired area.
    // 48*4=192 / 64*4=256 / 80*4=320 all satisfy the 16-byte alignment requirement for mat4x4f.
    // The full 432-byte layout, JS-side STRIDE=108, and pipeline selection offsets (float[94] / int[98]) all stay unchanged.
    // All zeros means "not written" (MotionVectors disabled or previous data not ready), and the shader degrades gracefully based on the sentinel (clause 9).
    /// <summary>mat4x4f prevWorld (previous-frame world matrix, using the same transposed row-major convention as World;
    /// M44 == 0 means no history, so the shader falls back to the current-frame world matrix)</summary>
    public const int PrevWorld = 48;

    /// <summary>mat4x4f prevViewProjection (previous-frame non-jittered View x Projection; an all-zero block means no history,
    /// so the shader outputs zero velocity)</summary>
    public const int PrevViewProjection = 64;

    /// <summary>vec4f prevMorphWeights (previous-frame morph weights; consumed by Step C and kept at 0 in Step A)</summary>
    public const int PrevMorphWeights = 80;

    // Material parameters (each item is a vec4f).
    /// <summary>vec4f baseColor</summary>
    public const int BaseColor = 84;
    /// <summary>vec4f emissive（xyz=color, w=ao）</summary>
    public const int Emissive = 88;
    /// <summary>vec4f material（x=metallic, y=roughness, z=alpha, w=alphaCutoff）</summary>
    public const int Material = 92;

    // Flags (vec4&lt;i32&gt;, written into float slots using int bit patterns).
    /// <summary>vec4&lt;i32&gt; flags (x = previous-data sentinel bitfield for 2-3 Step C, see <see cref="WebGPUPrevDataFlags"/>;
    /// reuses the retired 1-2 lightCount slot, while punctual light count now reads from uLights.params0.x;
    /// y = renderMode, z = alphaMode, w = textureFlags). JS pipeline selection depends on int[98] (= z), so this offset cannot move.</summary>
    public const int Flags = 96;

    // Morph weights (non-instanced path, aligned with native MaterialParams.MorphWeights).
    /// <summary>vec4f morphWeights (up to 4 morph target weights)</summary>
    public const int MorphWeights = 100;

    // HDR parameters (1-4 Step B -> retired in 1-2).
    /// <summary>vec4f hdrParams (retired reserved slot: exposure now reads from uLights.params0.y while keeping the single injection point from contract 7;
    /// in Phase 4 it is repurposed as the per-draw outline color carrier for the OutlineMask pass, see <see cref="WebGPUUniformWriter.SetOutlineMaskColor"/>)</summary>
    public const int HdrParams = 104;

    /// <summary>Total float count in the uniform buffer.</summary>
    public const int TotalFloats = 108;
    /// <summary>Total byte size of the uniform buffer.</summary>
    public const int TotalBytes = TotalFloats * 4;
}

/// <summary>
/// 2-3 Step C: previous-data validity sentinel bitfield stored in <c>flags.x</c>. It matches the three native
/// MaterialParams uints HasPrevBones@112 / HasPrevInstanceWorld@116 / HasPrevMorph@120, but this backend packs them into
/// the retired 1-2 reserved <c>flags.x</c> slot because the 432-byte UBO is already fully occupied by 108 floats.
/// The layout and STRIDE remain unchanged. Semantics are identical across all backends: bit = 0 means that previous data path
/// is not ready or not applicable, so the VS falls back to the current-frame source data for that path and produces zero velocity there.
/// On the first frame, the all-zero state naturally matches the behavior before Step C. <see cref="WebGPUUniformWriter.SetFlags"/>
/// always writes this field, so scratch-buffer reuse across draws cannot leak sentinels from a previous object
/// (sites that never pass a value, such as text or Sprite2D/3D, always keep it at 0).
/// </summary>
public static class WebGPUPrevDataFlags
{
    /// <summary>Bit 0: previous bone palette (binding 13) is valid because the JS-side shadow copy has been ready for two consecutive frames.</summary>
    public const int PrevBones = 1;

    /// <summary>Bit 1: the first 4 vec4 values in the previous instance byte stream (binding 14), which store per-instance previous-frame world matrices, are valid.</summary>
    public const int PrevInstanceWorld = 2;

    /// <summary>Bit 2: previous morph weights are valid (non-instanced path reads <see cref="WebGPUUniformLayout.PrevMorphWeights"/>,
    /// instanced path reads the 5th vec4 from the previous instance byte stream).</summary>
    public const int PrevMorph = 4;
}

/// <summary>
/// Bitmask for uniform <c>flags.w</c> (textureFlags).
/// Aligned with the <c>HasTexture(flagMask)</c> checks in the <see cref="WebGPUPipeline"/> WGSL code.
/// </summary>
public static class WebGPUTextureFlags
{
    /// <summary>Binding slot 4: metallicRoughness texture.</summary>
    public const int MetallicRoughness = 1;
    /// <summary>Binding slot 3: normal texture.</summary>
    public const int Normal = 2;
    /// <summary>Binding slot 5: occlusion texture.</summary>
    public const int Occlusion = 4;
    /// <summary>Binding slot 6: emissive texture.</summary>
    public const int Emissive = 8;

    // Vertex processing path flags (share flags.w with texture flags).
    /// <summary>Bit 4 (16): enable GPU instancing (uses the mat4x4 world from the instance stream).</summary>
    public const int Instanced = 16;
    /// <summary>Bit 5 (32): enable skeletal skinning (reads binding 7 uBones).</summary>
    public const int Skinned = 32;
    /// <summary>Bit 6 (64): enable morph targets (reads binding 8/9 uMorphMeta / uMorphValues).</summary>
    public const int Morph = 64;

    // Pipeline routing flags (also share flags.w with texture flags; consumed by JS _selectPipelineMode, ignored by WGSL).
    /// <summary>Bit 7 (128): 2-2 contract clause 7 GTAO exemption (mirrors Mesh3D.ExcludeFromAo).
    /// The Scene pass disables depth writes through the Nd pipeline variant, so SceneDepth keeps the clear value and the GTAO sky/empty branch exempts it.
    /// WGSL checks only the existing bits (HasTexture / instancing / skinned / morph), so this new bit is harmless to the shader.</summary>
    public const int NoDepthWrite = 128;
}

/// <summary>
/// Enum for uniform <c>flags.y</c> (renderMode).
/// </summary>
public enum WebGPURenderMode
{
    /// <summary>Unlit rendering.</summary>
    Unlit = 0,
    /// <summary>PBR lighting.</summary>
    Lit = 1,
    /// <summary>MSDF text rendering.</summary>
    TextMsdf = 2,
    /// <summary>2-5 procedural sky (ignores vertex UVs, reconstructs Sky-View LUT UVs from the world-space view direction,
    /// consumes sky/cloud fields from SceneLights, and composites celestial discs, stars, and clouds).</summary>
    ProceduralSky = 3,
}

/// <summary>
/// Enum for uniform <c>flags.z</c> (alphaMode).
/// </summary>
public enum WebGPUAlphaMode
{
    /// <summary>Opaque.</summary>
    Opaque = 0,
    /// <summary>Alpha test (Mask).</summary>
    Mask = 1,
    /// <summary>Alpha blending (Blend).</summary>
    Blend = 2,
}

/// <summary>
/// Helper for uniform buffer reads and writes. Provides type-safe float[] indexed access
/// instead of hardcoded patterns such as <c>uniformData[84] = ...</c>.
/// </summary>
public ref struct WebGPUUniformWriter
{
    readonly Span<float> _data;

    public WebGPUUniformWriter(Span<float> data)
    {
        _data = data;
    }

    /// <summary>Writes a mat4x4 using the same transposed row-major convention used to match WGSL and CopyMatrixTransposed.</summary>
    public void SetWorld(Matrix4x4 m) => CopyTransposed(m, WebGPUUniformLayout.World);
    public void SetView(Matrix4x4 m) => CopyTransposed(m, WebGPUUniformLayout.View);
    public void SetProjection(Matrix4x4 m) => CopyTransposed(m, WebGPUUniformLayout.Projection);

    void CopyTransposed(Matrix4x4 m, int offset)
    {
        _data[offset + 0] = m.M11; _data[offset + 1] = m.M12; _data[offset + 2] = m.M13; _data[offset + 3] = m.M14;
        _data[offset + 4] = m.M21; _data[offset + 5] = m.M22; _data[offset + 6] = m.M23; _data[offset + 7] = m.M24;
        _data[offset + 8] = m.M31; _data[offset + 9] = m.M32; _data[offset + 10] = m.M33; _data[offset + 11] = m.M34;
        _data[offset + 12] = m.M41; _data[offset + 13] = m.M42; _data[offset + 14] = m.M43; _data[offset + 15] = m.M44;
    }

    // Contract 8 in 1-2: SetCameraPos / SetLightPosition / SetLightColor / SetHdrExposure were removed.
    // Lighting and exposure now come from the shared UBO at binding(10) (Graphics.UpdateCamera3D uploads SceneLightParams as a whole each frame),
    // and those slots are reused as the previous-data section starting in 2-3 (see WebGPUUniformLayout.PrevWorld).

    /// <summary>2-3 contract clause 6: writes the previous-frame world matrix using the same transpose convention as SetWorld.
    /// If not called, the slot stays all-zero (each draw site already clears via Array.Clear), so the shader falls back to the current-frame world matrix.</summary>
    public void SetPrevWorld(Matrix4x4 m) => CopyTransposed(m, WebGPUUniformLayout.PrevWorld);

    /// <summary>2-3 contract clause 6: writes the previous-frame non-jittered View x Projection matrix (all-zero means no history and therefore zero velocity).</summary>
    public void SetPrevViewProjection(Matrix4x4 m) => CopyTransposed(m, WebGPUUniformLayout.PrevViewProjection);

    /// <summary>2-3: writes previous-frame morph weights (consumed by Step C; Step A leaves them at 0 by not writing).</summary>
    public void SetPrevMorphWeights(Vector4 v)
    {
        int o = WebGPUUniformLayout.PrevMorphWeights;
        _data[o] = v.X; _data[o + 1] = v.Y; _data[o + 2] = v.Z; _data[o + 3] = v.W;
    }

    public void SetBaseColor(Vector4 v)
    {
        _data[WebGPUUniformLayout.BaseColor] = v.X;
        _data[WebGPUUniformLayout.BaseColor + 1] = v.Y;
        _data[WebGPUUniformLayout.BaseColor + 2] = v.Z;
        _data[WebGPUUniformLayout.BaseColor + 3] = v.W;
    }

    public void SetEmissive(Vector3 emissive, float ao)
    {
        _data[WebGPUUniformLayout.Emissive] = emissive.X;
        _data[WebGPUUniformLayout.Emissive + 1] = emissive.Y;
        _data[WebGPUUniformLayout.Emissive + 2] = emissive.Z;
        _data[WebGPUUniformLayout.Emissive + 3] = ao;
    }

    public void SetMaterial(float metallic, float roughness, float alpha, float alphaCutoff)
    {
        int o = WebGPUUniformLayout.Material;
        _data[o] = metallic;
        _data[o + 1] = roughness;
        _data[o + 2] = alpha;
        _data[o + 3] = alphaCutoff;
    }

    /// <summary>Writes morph weights for the non-instanced path (up to 4 targets, aligned with native MaterialParams.MorphWeights).</summary>
    public void SetMorphWeights(Vector4 v)
    {
        int o = WebGPUUniformLayout.MorphWeights;
        _data[o] = v.X; _data[o + 1] = v.Y; _data[o + 2] = v.Z; _data[o + 3] = v.W;
    }

    /// <summary>Writes the flags vec4 (x = previous-data sentinel bitfield for 2-3 Step C, <see cref="WebGPUPrevDataFlags"/>,
    /// y = renderMode, z = alphaMode, w = textureFlags). All values are stored as ints encoded in float bit patterns.
    /// <c>prevDataFlags</c> is always written, defaulting to 0, so scratch-buffer reuse across draws clears the field explicitly
    /// and prevents sentinels from leaking from the previous object.</summary>
    public void SetFlags(WebGPURenderMode renderMode, WebGPUAlphaMode alphaMode, int textureFlags, int prevDataFlags = 0)
    {
        int o = WebGPUUniformLayout.Flags;
        _data[o] = BitConverter.Int32BitsToSingle(prevDataFlags);
        _data[o + 1] = BitConverter.Int32BitsToSingle((int)renderMode);
        _data[o + 2] = BitConverter.Int32BitsToSingle((int)alphaMode);
        _data[o + 3] = BitConverter.Int32BitsToSingle(textureFlags);
    }

    /// <summary>Overwrites only flags.w (textureFlags) while preserving x, y, and z.</summary>
    public void SetTextureFlags(int textureFlags)
    {
        _data[WebGPUUniformLayout.Flags + 3] = BitConverter.Int32BitsToSingle(textureFlags);
    }

    /// <summary>Phase 4: per-draw outline color for the OutlineMask pass. Writes into the retired hdrParams slot 104-107,
    /// which WGSL <c>fs_main_outline_mask</c> reads through <c>u.hdrParams.rgb</c>, with zero layout or STRIDE changes.
    /// Non-mask draw sites do not write this slot, so leaving it at 0 has no side effects.</summary>
    public void SetOutlineMaskColor(Vector4 color)
    {
        int o = WebGPUUniformLayout.HdrParams;
        _data[o] = color.X;
        _data[o + 1] = color.Y;
        _data[o + 2] = color.Z;
        _data[o + 3] = color.W;
    }
}
