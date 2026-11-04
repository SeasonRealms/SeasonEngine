// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Frame-slotted, GPU-resident bone palette that a producer writes and the instanced vertex shader reads.
///
/// <para>
/// Why this exists next to the backend-owned palette ring: that ring is an upload-heap (D3D12) or host-visible (Vulkan) block
/// the CPU maps and writes into every frame, which is only reachable from the CPU. The moment the palette is produced by a
/// compute kernel, the VS has to read a <see cref="StorageBuffer"/> instead, through the slot the shader already declares for
/// the palette: t6 for this frame and t8 for the previous one. This type is the shape all four backends share so the producer
/// and the consumer never need to know which one they run on.
/// </para>
///
/// Contract:
/// <list type="number">
/// <item>Buffers are created with <see cref="StorageBufferUsage.ComputeAndVertexShaderRead"/>. That is what makes D3D12 own a
/// structured vertex-readable view of the resource and Vulkan/WebGPU accept the binding in the vertex stage; the read state itself is
/// recorded by the handoff below. A buffer created with any other usage must be rejected rather than silently mis-rendered.</item>
/// <item>Layout is [instanceSlot * Stride + jointIndex] matrices of 64 bytes, bit-identical to the CPU ring the shader already
/// addresses. Nothing about the shader changes when the palette becomes compute-written.</item>
/// <item>Slots are a frame ring of the backend's own depth. Reading the previous frame is "the slot the previous frame wrote",
/// exactly like the CPU ring, so <c>Previous</c> must never alias <c>Current</c>.</item>
/// <item>The producer must call <c>Graphics.TryPrepareVertexShaderRead</c> on each buffer after writing it and before the first
/// draw that reads it, outside any render pass. It is the compute-write-to-vertex-read handoff, and it is a no-op returning
/// true on the backends that track the dependency themselves.</item>
/// <item>Ownership of the object stays outside this type: whoever creates the ring disposes it, and a control that only binds one never
/// disposes it. What a backend may still do is <see cref="Resize"/> the slots to the shape it can actually bind - its own frame-ring
/// depth, the palette stride it draws with, at least the instances it submits - because a ring whose depth differs from the frame ring
/// would bind the wrong slot (or none). A producer must therefore read <see cref="Slots"/>/<see cref="Current"/> every frame instead of
/// caching a buffer reference across frames, and must treat this type as the authority on stride and capacity rather than the reverse.</item>
/// </list>
/// </summary>
public sealed class InstanceBoneStorage : IDisposable
{
    /// <summary>Bytes of one palette matrix, the stride the shader's StructuredBuffer declaration assumes on every backend.</summary>
    public const int BoneMatrixBytes = 64;

    /// <summary>One buffer per in-flight frame. Empty means "not allocated yet".</summary>
    public StorageBuffer?[] Slots = Array.Empty<StorageBuffer?>();

    /// <summary>Matrices per instance, i.e. the total joint count of every skin of the template.</summary>
    public int Stride = 1;

    /// <summary>Instances the buffers were sized for. A write past this is a bug, not a silent clamp.</summary>
    public int Capacity;

    /// <summary>
    /// P3 L2: blocks reserved after the instance blocks for the pose pool, one per shared (clip, tick) group. Zero is the
    /// historical unpooled layout. The reservation is a capacity rather than the frame's actual group count because the ring is
    /// shaped before the frame's clocks are read; no frame can share more groups than it has instances, so
    /// <see cref="Capacity"/> blocks is the exact worst case and costs one extra palette per instance.
    /// </summary>
    public int PoolRows;

    /// <summary>Slot the current frame writes and the VS reads at t6. Set by the owner, which is the only party that knows the frame ring.</summary>
    public int CurrentSlot;

    /// <summary>Slot the previous frame wrote and the VS reads at t8. Must differ from <see cref="CurrentSlot"/>.</summary>
    public int PreviousSlot;

    /// <summary>CPU staging used only by the pre-compute stand-in path; the compute producer never touches it.</summary>
    public byte[] Staging = Array.Empty<byte>();

    /// <summary>Buffer read at t6 this frame, or null when the ring is not allocated.</summary>
    public StorageBuffer? Current => At(CurrentSlot);

    /// <summary>Buffer read at t8 this frame, or null when the ring is not allocated.</summary>
    public StorageBuffer? Previous => At(PreviousSlot);

    /// <summary>Matrices the whole ring holds per slot; also the element count a structured view must be created with.</summary>
    public int MatrixCount => Math.Max(1, Stride) * (Math.Max(1, Capacity) + Math.Max(0, PoolRows));

    /// <summary>Bytes per slot buffer.</summary>
    public int SlotBytes => MatrixCount * BoneMatrixBytes;

    StorageBuffer? At(int slot) => (uint)slot < (uint)Slots.Length ? Slots[slot] : null;

    /// <summary>Whether both the current and the previous slot resolve to a buffer, i.e. whether a draw may read this ring at all.</summary>
    public bool Ready => Current != null && Previous != null;

    /// <summary>
    /// Allocates the ring. Callers pass their own frame-ring depth, because that is a backend property (the web backend has no
    /// frame ring and ping-pongs two slots instead). Returns false when the backend cannot create vertex-readable storage
    /// buffers, in which case the caller must keep its CPU path.
    /// </summary>
    public static InstanceBoneStorage? Create(IGraphics g, int slots, int stride, int capacity)
    {
        if (g == null || !g.VertexShaderStorageReadSupported || slots <= 0)
            return null;

        var ring = new InstanceBoneStorage();
        try
        {
            ring.Resize(g, slots, stride, capacity);
            if (!ring.Ready)
            {
                ring.Dispose();
                return null;
            }
            return ring;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp?.AddLog(LogType.Error,
                $"{DateTime.UtcNow} [InstanceBoneStorage] creation failed: {ex.Message}");
            ring.Dispose();
            return null;
        }
    }

    /// <summary>Reallocates every slot at a new stride/capacity, retiring the old buffers through the backend's own release queue.
    /// <paramref name="poolRows"/> reserves that many extra blocks after the instance blocks for the pose pool.</summary>
    public bool Resize(IGraphics g, int slots, int stride, int capacity, int poolRows = 0)
    {
        if (g == null || !g.VertexShaderStorageReadSupported || slots <= 0 || stride <= 0 || capacity <= 0 || poolRows < 0)
            return false;

        if (Slots.Length == slots && Stride == stride && Capacity == capacity && PoolRows == poolRows
            && Slots.All(static s => s != null))
            return true;

        Dispose();

        Stride = stride;
        Capacity = capacity;
        PoolRows = poolRows;
        Slots = new StorageBuffer?[slots];
        for (int i = 0; i < slots; i++)
            Slots[i] = g.CreateStorageBuffer((uint)SlotBytes, StorageBufferUsage.ComputeAndVertexShaderRead);

        Staging = new byte[SlotBytes];
        return Ready;
    }

    /// <summary>
    /// Writes one instance's palette into the CPU staging block. Only the pre-compute stand-in uses this: it feeds the ring the
    /// same bytes the backend would otherwise have mapped straight into its upload heap, which is what makes the storage-buffer
    /// route verifiable against the CPU route pixel for pixel before any kernel exists.
    /// Bindings beyond <see cref="Stride"/> are dropped and the tail is zeroed, mirroring the CPU ring.
    /// </summary>
    public unsafe void Stage(int instanceIndex, Matrix4x4[] palette)
    {
        if (Staging.Length == 0 || (uint)instanceIndex >= (uint)Capacity)
            return;

        int count = Math.Min(palette.Length, Stride);
        int offset = instanceIndex * Stride * BoneMatrixBytes;
        ref byte destination = ref Staging[offset];
        fixed (Matrix4x4* source = palette)
            Unsafe.CopyBlock(ref destination, ref *(byte*)source, (uint)(count * BoneMatrixBytes));

        int tail = (Stride - count) * BoneMatrixBytes;
        if (tail > 0)
            new Span<byte>(Staging, offset + count * BoneMatrixBytes, tail).Clear();
    }

    /// <summary>
    /// Uploads the staged frame into <see cref="Current"/> and hands it to the vertex stage. Returns false when the handoff
    /// could not be recorded, in which case the frame must not draw from this ring.
    /// </summary>
    public bool Publish(IGraphics g, int instanceCount)
    {
        int bytes = Math.Min(instanceCount, Capacity) * Stride * BoneMatrixBytes;
        return bytes > 0 && Publish(g, new ReadOnlySpan<byte>(Staging, 0, bytes));
    }

    /// <summary>
    /// Uploads a block that is already laid out exactly like the ring and hands it to the vertex stage. The span overload exists for
    /// backends whose palette is one contiguous array with a fixed stride (the web path keeps row <c>instance × 100</c>), where staging the
    /// same bytes a second time to satisfy a per-instance signature would only copy them twice.
    /// </summary>
    public bool Publish(IGraphics g, ReadOnlySpan<byte> paletteBytes)
    {
        var current = Current;
        if (g == null || current == null)
            return false;

        if (paletteBytes.Length == 0 || paletteBytes.Length > SlotBytes)
            return false;

        if (!g.TryUpdateStorageBuffer(current, paletteBytes))
            return false;

        return g.TryPrepareVertexShaderRead(current);
    }

    /// <summary>Releases every slot through the backend's own retire queue. Only the creator calls this; a backend that merely binds the
    /// ring never does, which is why <see cref="Resize"/> (the one exception, documented above) is the only mutation available to one.</summary>
    public void Dispose()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i]?.Dispose();
            Slots[i] = null;
        }
        Slots = Array.Empty<StorageBuffer?>();
        Staging = Array.Empty<byte>();
        Capacity = 0;
        PoolRows = 0;
    }
}
