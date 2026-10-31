// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>单个贴花的绘制请求：后端据此选 PSO（按 <see cref="Mode"/>）并解析纹理（按 <see cref="Texture"/>）。</summary>
/// <param name="Index">贴花数据在本帧缓冲中的槽位，后端作为每 draw 常量传给着色器。</param>
public readonly record struct DecalDraw(int Index, DecalMode Mode, string? Texture);

/// <summary>
/// 每帧交给后端的贴花输入。Prepare 在 Pass 外解析资源并把深度/颜色转换成可采样状态，Draw 只能在
/// <see cref="RenderPassId.SceneDecal"/> 内消费一次 —— 与 <see cref="FogCompositeInputs"/> 的时序契约相同。
/// </summary>
/// <param name="Color">本帧 SceneColor，既是颜色目标也是被混合的底图。</param>
/// <param name="Depth">本帧 SceneDepth，只作为采样源，不挂成深度附件。</param>
/// <param name="Normal">本帧 SceneNormal；null 表示掠射淡出与粗糙度写入都退回深度重建或直接关闭。</param>
/// <param name="FrameBuffer">帧头 + 贴花数组的连续缓冲，布局见 <see cref="DecalMath"/>。</param>
/// <param name="Draws">按绘制次序排好的槽位表，前 <paramref name="Count"/> 项有效。</param>
public readonly record struct DecalInputs(RenderTarget Color, RenderTarget Depth, RenderTarget? Normal,
    StorageBuffer FrameBuffer, DecalDraw[] Draws, int Count, bool WriteRoughness);

/// <summary>
/// D1 屏幕空间贴花的后端入口。四个后端各自实现，共享层只负责剔除、排序与打包。
/// 返回 false 表示本帧放弃绘制（资源形状不符、能力缺失），调用方必须当成「没有贴花」处理而不是部分绘制。
/// </summary>
public abstract class DecalPipeline : IDisposable
{
    public abstract bool Prepare(in DecalInputs inputs, RenderTarget destination);

    public abstract bool Draw();

    public abstract void Dispose();
}

/// <summary>
/// 帧缓冲的字节布局，四端与 C# 共享同一份定义；公开以便数学测试逐字段核对（与 <c>VolumetricFogEffect.PackFrame</c> 的约定一致）。
///
/// 全部按 <see cref="System.Numerics"/> 原始行序存放 float4：
/// <list type="bullet">
/// <item>0..3 反抖动的 VP（含后端 NDC 变换）的逆矩阵，用于从深度重建世界坐标；</item>
/// <item>4..7 反抖动的 VP（含后端 NDC 变换），顶点着色器用它把贴花盒放进裁剪空间；</item>
/// <item>8 (相机世界坐标.xyz, 1/宽)；9 (宽, 高, 1/高, 1)；10 (贴花数, 是否写粗糙度, 保留, 保留)；</item>
/// <item>16 起每个贴花 <see cref="DecalStride"/> 个 float4：
/// 0..3 世界→局部（已归一化到 [-1,1]³），4..7 局部→世界，
/// 8 (tint.rgb, opacity)，9 (模式, 内部羽化, 掠射淡出, 强度)，
/// 10 (目标粗糙度, 是否改写粗糙度, 排序键, 是否有贴图)，11 保留。</item>
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

    /// <summary>把本帧的帧头与贴花数组写进缓冲。调用方负责先按排序结果把贴花表排好。</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, in Matrix4x4 inverseJitteredVp,
        Vector3 position, float width, float height, bool writeRoughness, ReadOnlySpan<DecalVolume> decals)
    {
        if (data.Length < VectorCount) throw new ArgumentException("贴花帧缓冲不足。", nameof(data));
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
