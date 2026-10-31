// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>
/// D1 贴花的共享层驱动：每帧做视锥剔除、稳定排序、帧缓冲打包，然后交给后端 <see cref="DecalPipeline"/>。
///
/// 生命周期与 <see cref="FogCompositePipeline"/> 一致：注册在初始化期完成（后端在此时建立 PSO 与根签名），
/// 之后每帧 Prepare 在 Pass 外调用一次，Draw 在 <see cref="RenderPassId.SceneDecal"/> 内消费一次。
/// 本帧没有可见贴花时 Prepare 返回 false，调用方连 BeginPass 都不需要发 —— 空表就是零开销。
/// </summary>
public sealed class DecalEffect : IDisposable
{
    readonly IGraphics _graphics;
    readonly DecalPipeline _pipeline;
    readonly Vector4[] _frame = new Vector4[DecalMath.VectorCount];
    readonly DecalDraw[] _draws = new DecalDraw[DecalMath.MaxDecals];
    readonly DecalVolume[] _sorted = new DecalVolume[DecalMath.MaxDecals];
    StorageBuffer? _buffer;
    int _count;
    int _pipelineCount;
    bool _disposed;

    internal DecalEffect(IGraphics g, DecalPipeline pipeline)
    {
        _graphics = g;
        _pipeline = pipeline;
    }

    /// <summary>本帧是否已经有 Prepared 的贴花表；Draw 只在为 true 时有意义。</summary>
    public bool Ready { get; private set; }

    /// <summary>本帧通过剔除的贴花数量，供日志与验收使用。</summary>
    public int VisibleCount => _pipelineCount;

    /// <summary>
    /// 本帧是否需要把 SceneNormal 当成第二个颜色附件。只有「可见贴花里至少一个声明了
    /// <see cref="DecalVolume.AffectRoughness"/>」且存在法线目标时才为 true，
    /// 否则不要为一个不改粗糙度的贴花多付一次附件 Load/Store。调用方据此决定 Pass 的法线附件。
    /// </summary>
    public bool WriteRoughness { get; private set; }

    /// <summary>
    /// 收集并准备本帧的贴花。任何一项前提缺失都返回 false 且不改动 Ready，
    /// 调用方据此完全跳过 Pass（不 BeginPass、不写测试计数）。
    /// </summary>
    public bool Prepare(IGraphics g, SceneDecals? source, RenderTarget? color, RenderTarget? depth, RenderTarget? normal)
    {
        Ready = false;
        _pipelineCount = 0;
        WriteRoughness = false;
        if (_disposed || !ReferenceEquals(g, _graphics) || color == null || depth == null) return false;
        if (source == null || source.Decals.Count == 0) return false;
        var view = RenderView.Current;
        if (view == null || !view.Prepared || view.Width <= 0 || view.Height <= 0) return false;
        _count = Collect(source, in view.Frustum);
        if (_count == 0) return false;
        var jitteredVp = view.View * view.RenderProjection;
        if (!Matrix4x4.Invert(jitteredVp, out var inverseJitteredVp)) return false;
        bool writeRoughness = normal != null && AnyRoughness();
        DecalMath.Pack(_frame, jitteredVp, inverseJitteredVp, view.Position, view.Width, view.Height,
            writeRoughness, _sorted.AsSpan(0, _count));
        _buffer ??= g.CreateStorageBuffer(DecalMath.ByteCount);
        if (_buffer == null || !g.TryUpdateStorageBuffer(_buffer, MemoryMarshal.AsBytes(_frame.AsSpan())))
            return false;
        for (int i = 0; i < _count; i++)
            _draws[i] = new DecalDraw(i, _sorted[i].Mode, _sorted[i].Texture);
        var inputs = new DecalInputs(color, depth, normal, _buffer, _draws, _count, writeRoughness);
        if (!_pipeline.Prepare(inputs, color)) return false;
        _pipelineCount = _count;
        WriteRoughness = writeRoughness;
        Ready = true;
        return true;
    }

    bool AnyRoughness()
    {
        for (int i = 0; i < _count; i++) if (_sorted[i].AffectRoughness) return true;
        return false;
    }

    /// <summary>在 <see cref="RenderPassId.SceneDecal"/> 内调用一次；Prepare 未成功时什么都不做。</summary>
    public void Draw(IGraphics g)
    {
        if (!Ready || _disposed || !ReferenceEquals(g, _graphics)) return;
        if (!_pipeline.Draw()) Ready = false;
    }

    /// <summary>
    /// 剔除并排序。排序必须稳定：同 <see cref="DecalVolume.SortOrder"/> 的贴花按注册顺序绘制，
    /// 否则每帧次序抖动会让半透明叠加结果在 TAA 下发闪。
    /// </summary>
    int Collect(SceneDecals source, in Frustum frustum)
    {
        int count = 0;
        var decals = source.Decals;
        for (int i = 0; i < decals.Count && count < DecalMath.MaxDecals; i++)
        {
            var decal = decals[i];
            if (!decal.Enabled) continue;
            decal.GetBounds(out var center, out var extents);
            if (!frustum.Intersects(in center, in extents)) continue;
            // 稳定插入排序：上限 64，插入排序比分配 key 数组更省也更不容易写错。
            int slot = count;
            while (slot > 0 && _sorted[slot - 1].SortOrder > decal.SortOrder)
            {
                _sorted[slot] = _sorted[slot - 1];
                slot--;
            }
            _sorted[slot] = decal;
            count++;
        }
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ready = false;
        WriteRoughness = false;
        _pipelineCount = 0;
        _count = 0;
        _buffer?.Dispose();
        _buffer = null;
        Array.Clear(_sorted);
        Array.Clear(_draws);
        _pipeline.Dispose();
    }
}
