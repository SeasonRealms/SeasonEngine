// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.Generic;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan 场景快照页，对齐 DX <c>FrameContext.DrawSnapshots</c> 的职责：退休校验与宿主可见页游标。
///
/// 与 DX 的一处本质差异写在这里，避免后来者误以为漏了深拷贝：
/// DX 的每次绘制都要把矩阵/材质/骨骼/实例数据写进一张共享的 write-combined 上传页，
/// 于是收集时必须把那段字节整块拷走，否则下一次绘制就会覆写它；
/// VK 的旋转环是“所有者 + 帧槽”独占的（<c>PrimitiveData.MatrixBuffers[fi]</c>、
/// <c>VKSpriteQuad._matrixBuffers[fi]</c>、<c>_instanceBuffer</c> 等），
/// 直绘路径本来也只把句柄交给命令缓冲，帧内不会有人覆写别人的槽位。
/// 所以这里不做字节拷贝，只做生命周期钉住：重放期用到的控件/纹理/缓冲在本帧 GPU 退休前不能被释放。
/// </summary>
internal sealed class VKDrawSnapshotArena : IDisposable
{
    readonly List<object> _retained = new();

    // 引用相等：同一对象重复钉住不应重复入表；Texture/PrimitiveData 没有可用的值相等语义。
    readonly HashSet<object> _retainedSet = new(ReferenceEqualityComparer.Instance);

    ulong _stamp;

    bool _disposed;

    /// <summary>本帧钉住的对象数量，供测试与日志观察收集规模。</summary>
    internal int RetainedCount => _retained.Count;

    /// <summary>
    /// 进入新的一帧：校验退休戳单调，并回收上一帧的钉住。
    /// 与 DX 的 <c>DXDrawSnapshotArena.BeginFrame</c> 同形，只是 VK 队列由设备时间线信号量保证退休，
    /// 这里只需要拒绝“时间线倒退”的调用。
    /// </summary>
    internal void BeginFrame(ulong stamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stamp == 0 || stamp < _stamp)
            throw new InvalidOperationException("场景快照的退休戳必须单调递增。");
        if (stamp == _stamp) return;

        // 收集/重放期间会改写本帧槽的描述符集（骨骼或上一帧 SSBO 容量变化时全部槽一起重写），
        // 而同一槽上一帧的提交可能仍在 GPU 上：未声明 UPDATE_AFTER_BIND 的 set
        // 被 pending 命令缓冲引用时改写，会让那条缓冲整体失效。
        // 这与 VKCompute.AcquireSet、VKPrimitiveGroup.WriteDescriptorSet 是同一套约束；
        // 每帧只在进入收集时等一次，同槽的提交通常早已完成。
        Device.WaitForFrameSlot((int)Device.FrameIndex);

        ReleaseRetained();
        _stamp = stamp;
    }

    /// <summary>钉住一个在重放期仍会被命令缓冲引用的对象；同一帧内重复钉住是空操作。</summary>
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
