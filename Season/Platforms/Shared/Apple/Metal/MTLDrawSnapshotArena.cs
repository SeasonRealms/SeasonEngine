// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Generic;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal 场景快照页，与 DX <c>FrameContext.DrawSnapshots</c>、VK <c>VKDrawSnapshotArena</c> 同职责：
/// 退休校验 + 重放期资源钉住。
///
/// 与另外两端的一处本质差异写在这里，避免后来者以为漏了深拷贝：
/// DX 每次绘制都要把矩阵/材质/骨骼/实例数据写进一张共享的 write-combined 上传页，
/// 所以收集时必须把那段字节整块拷走，否则下一次绘制就会覆写它；
/// Metal 的旋转环同样是“所有者 + 帧槽”独占的（<c>PrimitiveData.MatrixBuffers[fi]</c>、
/// <c>MTLSpriteQuad._matrixBuffers[fi]</c>、文字各自按帧槽的实例/参数缓冲），
/// 直绘路径本来也只把 <c>IMTLBuffer</c> 句柄交给 encoder，帧内没有谁会覆写别人的槽位。
/// 所以这里同样不做字节拷贝，只做生命周期钉住。
///
/// 与 VK 的差别是这里没有描述符页游标：
/// Metal 的绑定是 <c>SetVertexBuffer</c> / <c>SetFragmentTexture</c> 直接带对象引用与偏移，
/// 没有“描述符集一旦绑定就不可改写”的约束，因此不需要描述符写入页与提交游标。
/// </summary>
internal sealed class MTLDrawSnapshotArena : IDisposable
{
    readonly List<object> _retained = new();

    // 引用相等：同一对象重复钉住不应重复入表；Texture/PrimitiveData 没有可用的值相等语义。
    readonly HashSet<object> _retainedSet = new(ReferenceEqualityComparer.Instance);

    ulong _stamp;

    bool _disposed;

    /// <summary>本帧钉住的对象数量，供测试与日志观察收集规模。</summary>
    internal int RetainedCount => _retained.Count;

    /// <summary>当前退休戳；收集器据此拒绝跨帧消费。</summary>
    internal ulong Stamp => _stamp;

    /// <summary>
    /// 进入新的一帧：校验退休戳单调，并回收上一帧的钉住。
    /// Metal 的帧槽退休由 <c>Device</c> 的 in-flight 信号量保证（BeginFrame 已经等过该槽），
    /// 所以这里只需要拒绝“时间线倒退”的调用。
    /// </summary>
    internal void BeginFrame(ulong stamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stamp == 0 || stamp < _stamp)
            throw new InvalidOperationException("场景快照的退休戳必须单调递增。");
        if (stamp == _stamp) return;

        ReleaseRetained();
        _stamp = stamp;
    }

    /// <summary>钉住一个在重放期仍会被 encoder 引用的对象；同一帧内重复钉住是空操作。</summary>
    internal void Retain(object? resource)
    {
        if (_disposed || resource == null || !_retainedSet.Add(resource)) return;
        _retained.Add(resource);
    }

    /// <summary>钉住一组对象，便于一次性登记。</summary>
    internal void Retain<T>(T[]? resources)
    {
        if (resources == null) return;
        for (int i = 0; i < resources.Length; i++) Retain(resources[i]);
    }

    void ReleaseRetained()
    {
        _retained.Clear();
        _retainedSet.Clear();
    }

    /// <summary>帧内异常路径：撤销钉住，不留下跨帧残留。</summary>
    internal void Reset()
    {
        ReleaseRetained();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseRetained();
    }
}
