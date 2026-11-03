// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>A single decal draw request: the backend uses this to select the PSO (by <see cref="Mode"/>) and resolve the texture (by <see cref="Texture"/>).</summary>
/// <param name="Index">The slot of the decal data in this frame's buffer, passed by the backend to the shader as a per-draw constant.</param>
public readonly record struct DecalDraw(int Index, DecalMode Mode, string? Texture);

/// <summary>
/// The decal inputs handed to the backend each frame. Prepare resolves resources outside the pass and converts depth/color into a sampleable state; Draw may only
/// be consumed once inside <see cref="RenderPassId.SceneDecal"/> -- the same timing contract as <see cref="FogCompositeInputs"/>.
/// </summary>
/// <param name="Color">This frame's SceneColor, which is both the color target and the base image being blended onto.</param>
/// <param name="Depth">This frame's SceneDepth, used only as a sampling source and not attached as a depth attachment.</param>
/// <param name="Normal">This frame's SceneNormal; null means both grazing fade and roughness writing fall back to depth reconstruction or are simply disabled.</param>
/// <param name="FrameBuffer">The contiguous buffer of the frame header plus the decal array; see <see cref="DecalMath"/> for the layout.</param>
/// <param name="Draws">The slot table ordered by draw order; the first <paramref name="Count"/> entries are valid.</param>
public readonly record struct DecalInputs(RenderTarget Color, RenderTarget Depth, RenderTarget? Normal,
    StorageBuffer FrameBuffer, DecalDraw[] Draws, int Count, bool WriteRoughness);

/// <summary>
/// D1 screen-space decal backend entry point. Each of the four backends implements this; the shared layer only handles culling, sorting, and packing.
/// Returning false means drawing is abandoned for this frame (resource shape mismatch, missing capability); the caller must treat it as "no decals" rather than a partial draw.
/// </summary>
public abstract class DecalPipeline : IDisposable
{
    public abstract bool Prepare(in DecalInputs inputs, RenderTarget destination);

    public abstract bool Draw();

    public abstract void Dispose();
}

/// <summary>
/// Byte layout of the frame buffer, shared identically by all four backends and C#; exposed so math tests can verify it field by field (consistent with the convention of <c>VolumetricFogEffect.PackFrame</c>).
///
/// Everything is stored as float4 in <see cref="System.Numerics"/> raw row order:
/// <list type="bullet">
/// <item>0..3 the inverse of the un-jittered VP (including the backend NDC transform), used to reconstruct world coordinates from depth;</item>
/// <item>4..7 the un-jittered VP (including the backend NDC transform); the vertex shader uses it to bring the decal box into clip space;</item>
/// <item>8 (camera world position.xyz, 1/width); 9 (width, height, 1/height, 1); 10 (decal count, whether to write roughness, reserved, reserved);</item>
/// <item>from 16, each decal occupies <see cref="DecalStride"/> float4s:
/// 0..3 world->local (already normalized to [-1,1]³), 4..7 local->world,
/// 8 (tint.rgb, opacity), 9 (mode, inner feather, grazing fade, intensity),
/// 10 (target roughness, whether to overwrite roughness, sort key, whether it has a texture), 11 reserved.</item>
/// </list>
/// </summary>
public static class DecalMath
{
    public const int HeaderVectorCount = 16;
    public const int DecalOffset = HeaderVectorCount;
    public const int DecalStride = 12;
    public const int MaxDecals = SceneDecals.MaxActiveDecals;
    public const int VectorCount = DecalOffset + MaxDecals * DecalStride;
    public const int ByteCount = VectorCount * 16;

    /// <summary>Writes this frame's header and decal array into the buffer. The caller is responsible for ordering the decal list by the sort result first.</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, in Matrix4x4 inverseJitteredVp,
        Vector3 position, float width, float height, bool writeRoughness, ReadOnlySpan<DecalVolume> decals)
    {
        if (data.Length < VectorCount) throw new ArgumentException("Decal frame buffer is too small.", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, inverseJitteredVp);
        WriteMatrix(data, 4, jitteredVp);
        data[8] = new(position, width > 0 ? 1f / width : 0f);
        data[9] = new(width, height, height > 0 ? 1f / height : 0f, 1f);
        data[10] = new(decals.Length, writeRoughness ? 1 : 0, 0, 0);
        int offset = DecalOffset;
        for (int i = 0; i < decals.Length; i++)
        {
            var decal = decals[i];
            WriteMatrix(data, offset, decal.WorldToLocalMatrix);
            WriteMatrix(data, offset + 4, decal.WorldMatrix);
            data[offset + 8] = new(decal.Tint, decal.Opacity);
            data[offset + 9] = new((int)decal.Mode, decal.EdgeFade, decal.AngleFade, decal.Intensity);
            data[offset + 10] = new(decal.Roughness, decal.AffectRoughness && writeRoughness ? 1 : 0,
                decal.SortOrder, decal.Texture != null ? 1 : 0);
            offset += DecalStride;
        }
    }

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);
}
