// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>Draw request for a single water surface: the backend uses it to fetch the corresponding slot in the frame buffer and resolve the normal texture via <see cref="Texture"/>.</summary>
/// <param name="Index">Slot of the water data in this frame buffer, passed to the shader by the backend as per-draw constants.</param>
/// <param name="Texture">Normal texture name; null means use the procedural dual sine wave.</param>
/// <param name="TilesX">Column count for the P4 tiled instancing; 1 when Gerstner is disabled or tiling is off.</param>
/// <param name="TilesZ">Row count for the P4 tiled instancing; 1 when tiling is off.</param>
/// <param name="Subdiv">Number of subdivision segments per tile; 0 means no tiling, falling back to a single rectangle with 6 vertices.</param>
public readonly record struct WaterDraw(int Index, string? Texture, int TilesX, int TilesZ, int Subdiv)
{
    /// <summary>Vertex count for this draw: two triangles when not tiled, or subdiv² quads per tile when tiled.</summary>
    public int VertexCount => Subdiv > 0 ? Subdiv * Subdiv * 6 : 6;

    /// <summary>Instance count for this draw: one when not tiled, or the total tile count when tiled.</summary>
    public int InstanceCount => Subdiv > 0 ? TilesX * TilesZ : 1;
}

/// <summary>
/// Water input handed to the backend each frame. The key difference from <see cref="DecalInputs"/> is that it has two scene colors:
/// <see cref="Color"/> is the color target (write), while <see cref="RefractionSource"/> is the opaque scene color snapshot taken before the pass (read).
/// The same resource cannot be both an attachment and a sampling source, so refraction must rely on this snapshot rather than reading <see cref="Color"/> directly.
/// </summary>
/// <param name="Color">This frame's SceneColor, used only as the color target; this pass attaches no depth attachment.</param>
/// <param name="RefractionSource">The scene color copied before the pass, with the same size and format as <paramref name="Color"/>.</param>
/// <param name="Depth">This frame's SceneDepth, used only as a sampling source for the software depth test and thickness absorption.</param>
/// <param name="SkyView">Name of the P0 procedural sky Sky-View LUT; when null, reflection falls back to a constant sky color.</param>
/// <param name="FrameBuffer">Contiguous buffer of the frame header plus the water array; see <see cref="WaterMath"/> for the layout.</param>
/// <param name="Draws">Slot table ordered by draw order; the first <paramref name="Count"/> entries are valid.</param>
public readonly record struct WaterInputs(RenderTarget Color, RenderTarget RefractionSource, RenderTarget Depth,
    string? SkyView, StorageBuffer FrameBuffer, WaterDraw[] Draws, int Count);

/// <summary>
/// Screen-space reflection parameters for P3 water: shared by all water surfaces in a water table, filled by the shared layer based on this frame's Hi-Z readiness.
/// When <see cref="Enabled"/> is false the shader never touches those four pyramid textures and falls back to pure sky reflection
/// -- the backend's Hi-Z texture name may not resolve to any object at all.
/// </summary>
/// <param name="Intensity">Scale for the hit confidence; 0 is equivalent to turning reflection off, and 1 fully trusts on-screen hits.</param>
/// <param name="MaxDistance">How far a ray can travel along the water surface (meters). Reflections beyond this distance fall back to sky directly.</param>
/// <param name="Thickness">Thickness tolerance of the on-screen surface (view-space meters). This decides whether a ray passing through thin objects counts as a hit.</param>
public readonly record struct WaterReflection(bool Enabled, float Intensity, float Near, float Far,
    float MaxDistance, float Thickness);

/// <summary>
/// Backend entry point for the P2 water material. Implemented by each of the four backends; the shared layer only handles culling, sorting, and packing.
/// Returning false means drawing is skipped this frame (mismatched resource shape, missing capability); the caller must treat it as "no water" rather than partial drawing.
///
/// Why a separate pipeline instead of a material branch of the main shader: water needs to sample SceneColor and SceneDepth,
/// but in the transparent stage the main shader receives both as attachments, and the same resource cannot be both an attachment and a sampling source. Only a separate pass can
/// simultaneously read (snapshot + depth) and write (scene color), and it also saves the depth attachment -- occlusion instead uses the software depth test in the shader.
/// </summary>
public abstract class WaterPipeline : IDisposable
{
    public abstract bool Prepare(in WaterInputs inputs, RenderTarget destination);

    public abstract bool Draw();

    public abstract void Dispose();
}

/// <summary>
/// Byte layout of the water frame buffer, shared by the four backends and C#; made public so math tests can verify it field by field
/// (consistent with the convention of <see cref="DecalMath"/>).
///
/// Everything is stored as float4 in raw <see cref="System.Numerics"/> row order:
/// <list type="bullet">
/// <item>0..3 the inverse of the un-jittered VP (including the backend NDC transform), used to reconstruct world position and thickness from depth;</item>
/// <item>4..7 the un-jittered VP (including the backend NDC transform), used by the vertex shader to place the unit plane into clip space;</item>
/// <item>8 (camera world position.xyz, 1/width); 9 (width, height, 1/width, 1/height);</item>
/// <item>10 (water count, whether it is raining, time in seconds, whether the sky LUT is available);
/// 11 (sun direction.xyz, sun intensity), the fallback color for water specular and sky reflection;</item>
/// <item>12 (whether screen-space reflection is enabled, reflection intensity scale, near plane, far plane), 13 (max ray distance, thickness tolerance, 0, 0);</item>
/// <item>From 16 on, <see cref="WaterStride"/> float4 per water surface:
/// 0..3 model-to-world, 4 (tint.rgb, opacity),
/// 5 (absorption, refraction strength, normal strength, normal tiling),
/// 6 (scroll speed, ripple strength, fresnelF0, roughness),
/// 7 (whether there is a normal map, sort key, UV origin.x, UV origin.z),
/// 8..11 the four P4 Gerstner waves, one float4 each (amplitude, wave number, direction.x, direction.z),
/// empty slots with k &lt;= 0 are skipped in the shader, so vertices do not move at all when wind speed is 0;
/// 12 (tile columns, tile rows, subdivision segments, horizontal displacement factor), a subdivision segment count of 0 means no tiling;
/// 13 (phase velocity scale, actual wave number, 0, 0).</item>
/// </list>
/// </summary>
public static class WaterMath
{
    public const int HeaderVectorCount = 16;
    public const int WaterOffset = HeaderVectorCount;

    /// <summary>
    /// Number of float4 occupied by each water surface. P2/P3 use 8; P4 inserts the four Gerstner waves (+8..+11) and the tiling parameters (+12..+13)
    /// into the same segment, leaving the remaining +14/+15 empty. It was expanded to 16 instead of 14 so that the slot address stays a power of two --
    /// the multiplication of <c>WaterOffset</c> in the shader is therefore as cheap as before, and it is easier to keep adding fields later.
    /// </summary>
    public const int WaterStride = 16;
    public const int MaxWaters = SceneWaters.MaxActiveWaters;
    public const int VectorCount = WaterOffset + MaxWaters * WaterStride;
    public const int ByteCount = VectorCount * 16;

    /// <summary>Number of Gerstner wave components, corresponding one-to-one with +8..+11 in the frame layout.</summary>
    public const int WaveCount = 4;

    /// <summary>
    /// Maximum number of tiles a single water surface can be split into. It is also the encoding base for WebGPU: WGSL has no push constant,
    /// so the slot and tile must be packed into <c>instance_index</c> (the low <see cref="TileBits"/> bits are the tile, the high bits are the slot),
    /// which is why this number must be a power of two.
    /// </summary>
    public const int MaxTilesPerWater = 256;

    /// <summary>Low-bit count derived from <see cref="MaxTilesPerWater"/>: WGSL takes the tile from the low bits of the instance index and the slot from the high bits.</summary>
    public const int TileBits = 8;

    /// <summary>Maximum tile count per axis: 16×16 is exactly <see cref="MaxTilesPerWater"/>; going denser would require a different scheme.</summary>
    public const int MaxTilesPerAxis = 16;

    /// <summary>
    /// Upper bound on subdivision segments per tile. Together with <see cref="MaxTilesPerAxis"/> it caps a single water surface at
    /// 256×32²×6 ≈ 1.57M vertices; in practice ocean surfaces use 20, and ponds are not tiled at all.
    /// </summary>
    public const int MaxSubdiv = 32;

    /// <summary>Deep-water gravitational acceleration (m/s²). All four backends hard-code the same value; it is used only to convert wave number into angular frequency.</summary>
    public const float Gravity = 9.81f;

    /// <summary>Writes this frame's header and water array into the buffer. The caller is responsible for sorting the water table beforehand.</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, in Matrix4x4 inverseJitteredVp,
        Vector3 position, float width, float height, bool raining, float time, bool skyView,
        in WaterReflection reflection, Vector3 sunDirection, float sunIntensity, ReadOnlySpan<WaterPlane> waters)
    {
        if (data.Length < VectorCount) throw new ArgumentException("Water frame buffer is too small.", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, inverseJitteredVp);
        WriteMatrix(data, 4, jitteredVp);
        data[8] = new(position, width > 0 ? 1f / width : 0f);
        data[9] = new(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f);
        data[10] = new(waters.Length, raining ? 1 : 0, time, skyView ? 1 : 0);
        data[11] = new(sunDirection, sunIntensity);
        data[12] = new(reflection.Enabled ? 1f : 0f, reflection.Intensity, reflection.Near, reflection.Far);
        data[13] = new(reflection.MaxDistance, reflection.Thickness, 0f, 0f);
        int offset = WaterOffset;
        for (int i = 0; i < waters.Length; i++)
        {
            var water = waters[i];
            var uv = water.UvOrigin;
            WriteMatrix(data, offset, water.WorldMatrix);
            data[offset + 4] = new(water.Tint.X, water.Tint.Y, water.Tint.Z, water.Opacity);
            data[offset + 5] = new(water.Absorption, water.RefractionStrength, water.NormalStrength, water.NormalTiling);
            data[offset + 6] = new(water.ScrollSpeed, water.RippleStrength, water.FresnelF0, water.Roughness);
            data[offset + 7] = new(water.NormalTexture != null ? 1 : 0, water.SortOrder, uv.X, uv.Y);
            // P4: four Gerstner waves + tiling parameters. With wind waves disabled BuildWaves writes all four to 0 (the shader skips when k <= 0),
            // and Tiling zeroes the subdivision segment count at the same time, so the backend falls back to a single 6-vertex rectangle -- pixel-identical to the flat P3 water surface.
            int waves = BuildWaves(data, offset + 8, water.WindSpeed, water.WindDirection,
                water.SwellHeight, WaveCount);
            var tiling = Tiling(water);
            data[offset + 12] = new(tiling.X, tiling.Z, tiling.Subdiv, water.Choppiness);
            data[offset + 13] = new(water.WaveSpeed, waves, 0f, 0f);
            offset += WaterStride;
        }
    }

    /// <summary>
    /// Tile grid and subdivision level for this water surface. A returned <c>Subdiv</c> of 0 means no tiling, and the backend then draws a single 6-vertex rectangle.
    ///
    /// With wind waves disabled there is no tiling at all: Gerstner is the only thing that needs vertex density, and without it there is no reason to pay
    /// a hundred times the vertices for a flat plane. When the tile count exceeds the limit it also falls back to a single rectangle rather than doing "half tiling" -- that would put a seam in the middle of the screen.
    /// </summary>
    public static (int X, int Z, int Subdiv) Tiling(in WaterPlane water)
    {
        if (water.WindSpeed <= 0.01f || water.SwellHeight <= 0f || water.TileSize <= 0f || water.TileSubdiv <= 0)
            return (1, 1, 0);
        int x = (int)MathF.Ceiling(water.Size.X / water.TileSize);
        int z = (int)MathF.Ceiling(water.Size.Y / water.TileSize);
        x = Math.Clamp(x, 1, MaxTilesPerAxis);
        z = Math.Clamp(z, 1, MaxTilesPerAxis);
        if (x * z > MaxTilesPerWater) return (1, 1, 0);
        // Clamp the subdivision too: the vertex count is subdiv²×6×tiles, and one misplaced zero by an author means millions of vertices,
        // with nothing else in the shader's vertex budget to stop it.
        return (x, z, Math.Clamp(water.TileSubdiv, 1, MaxSubdiv));
    }

    /// <summary>
    /// Generates the parameters of four Gerstner components from the wind speed, writes them into <c>water + 8 .. + 11</c>, and returns the number of waves actually written.
    ///
    /// Wavelength spectrum: the dominant wavelength of a fully developed sea is λ_peak = 2πU²/g (the PM spectrum peak); here it is multiplied by 2 to push once toward the long-wave side,
    /// because geometry on the vertex side can only carry long waves -- short waves are left to the high-frequency detail on the pixel side, where density is not limited by the mesh.
    /// The four components decrease geometrically by 1/1.55, the weights decrease by 1/1.45, and it is finally normalized to <paramref name="swellHeight"/>/2:
    /// the peak of sin is 1, so the crest-to-trough difference is twice the amplitude.
    ///
    /// Phase velocity is not stored in the frame buffer: it is uniquely determined by deep-water dispersion ω = sqrt(g·k), and the shader computes it on the fly with the same constant,
    /// so the two sides never end up with different frequencies from interpolating separately. The author-facing "speed" is just the <see cref="WaterPlane.WaveSpeed"/> multiplier.
    /// </summary>
    public static int BuildWaves(Span<Vector4> data, int offset, float windSpeed, float windDirection,
        float swellHeight, int count)
    {
        count = Math.Clamp(count, 0, WaveCount);
        if (windSpeed <= 0.01f || swellHeight <= 0f || count == 0)
        {
            data.Slice(offset, WaveCount).Clear();
            return 0;
        }

        // Dominant wavelength: the PM spectrum peak times 2. The 24m lower bound keeps it at least geometrically meaningful on the vertex side (the in-tile grid spacing is usually 8~12m).
        float longest = Math.Clamp(6.2831853f * windSpeed * windSpeed / Gravity * 2f, 24f, 320f);

        // Sum the weights first, then normalize: only the first count components are taken, and the total amplitude still equals swellHeight/2, so it does not get shorter when waves are removed.
        Span<float> weight = stackalloc float[WaveCount];
        float weightSum = 0f;
        for (int i = 0; i < count; i++)
        {
            weight[i] = 1f / MathF.Pow(1.45f, i);
            weightSum += weight[i];
        }
        float peak = swellHeight * 0.5f;

        float wavelength = longest;
        for (int i = 0; i < count; i++)
        {
            float k = 6.2831853f / wavelength;
            float amplitude = peak * weight[i] / weightSum;
            // The directions fan out around the dominant wind by about ±45°: a set of perfectly parallel crests would read as regular stripes at grazing angles.
            float angle = windDirection + (i - (count - 1) * 0.5f) * 0.52f;
            data[offset + i] = new(amplitude, k, MathF.Cos(angle), MathF.Sin(angle));
            wavelength /= 1.55f;
        }
        return count;
    }

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);
}
