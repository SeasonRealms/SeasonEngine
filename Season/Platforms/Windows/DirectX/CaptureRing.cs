// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Season.Basic;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Multi-slot backbuffer readback ring used by <see cref="IMediaRecorder"/>.
///
/// <para>
/// Why a ring instead of reusing the <c>CaptureApp</c> path: that path owns a
/// single readback buffer and calls <c>WaitForFence</c> immediately, which stalls
/// the CPU on the GPU every frame. That is fine for a one-shot screenshot and
/// fatal for 30 fps recording. Here each captured frame lands in its own slot and
/// is only mapped once its fence has already completed, so in steady state the
/// render thread never waits: a frame copied during frame N is picked up during
/// frame N+1 or N+2, by which time the GPU is long done with it.
/// </para>
///
/// <para>
/// Ordering guarantee: slots are filled and drained strictly FIFO. Fence values
/// increase monotonically with enqueue order, so FIFO completion order equals
/// FIFO enqueue order, and the encoder therefore receives frames with
/// monotonically increasing indices.
/// </para>
///
/// <para>
/// Back pressure: when every slot is in flight the render thread blocks on the
/// oldest fence rather than dropping the frame. That lowers the render rate under
/// extreme load but never corrupts the output timeline, because pacing is
/// wall-clock based and the encoder fills any gap with duplicates.
/// </para>
///
/// <para>All members must be called from the render thread only.</para>
/// </summary>
internal unsafe static class DXCaptureRing
{
    struct Slot
    {
        public ID3D12Resource* Buffer;

        /// <summary>Ring-fence value that guards this slot's copy. 0 means free.</summary>
        public ulong FenceValue;

        /// <summary>Position on the recorder's constant-rate output timeline.</summary>
        public long FrameIndex;

        public uint Width;

        public uint Height;

        public uint RowPitch;
    }

    static FrameCaptureRequest? _active;

    static Slot[] _slots = [];

    static uint _slotWidth;

    static uint _slotHeight;

    static uint _slotRowPitch;

    static uint _slotBytes;

    static int _head;

    static int _tail;

    static int _pending;

    /// <summary>
    /// Render-thread gate called while the backbuffer is still being recorded.
    /// Returns true when the current frame belongs on the recording timeline, and
    /// yields its output frame index. Costs one null check when idle.
    /// </summary>
    internal static bool WantsFrame(out long frameIndex)
    {
        var request = BaseApp.ActiveFrameCapture;

        if (request == null)
        {
            frameIndex = -1;
            return false;
        }

        // A new session replaces the previous request object. Retire the old one
        // first so its remaining frames are delivered before the size/pool of the
        // new session takes over.
        if (!ReferenceEquals(request, _active))
        {
            Retire();
            _active = request;
        }

        return request.ShouldCapture(out frameIndex);
    }

    /// <summary>
    /// Records the backbuffer copy into a free slot. The caller must already have
    /// transitioned the backbuffer to <see cref="ResourceStates.CopySource"/> and
    /// is responsible for transitioning it back.
    /// </summary>
    /// <param name="backbuffer">Source surface for the copy.</param>
    /// <param name="frameIndex">Position on the constant-rate output timeline.</param>
    /// <param name="width">Width of the region to read back, which is the visible
    /// part of the backbuffer rather than its full width.</param>
    /// <param name="height">Height of that same region.</param>
    /// <param name="sourceBox">Region of <paramref name="backbuffer"/> to copy,
    /// matching <paramref name="width"/> and <paramref name="height"/>.</param>
    internal static void Enqueue(ID3D12Resource* backbuffer, long frameIndex, uint width, uint height, Box* sourceBox)
    {
        if (_active == null || backbuffer == null) return;

        if (width == 0 || height == 0) return;

        EnsureSlots(width, height);

        if (_slots.Length == 0) return;

        int index = AcquireSlot();
        if (index < 0) return;

        ref var slot = ref _slots[index];

        TextureCopyLocation destination = default;
        destination.PResource = slot.Buffer;
        destination.Type = TextureCopyType.PlacedFootprint;
        destination.PlacedFootprint.Offset = 0;
        destination.PlacedFootprint.Footprint.Format = Device.BackBufferFormat;
        destination.PlacedFootprint.Footprint.Width = width;
        destination.PlacedFootprint.Footprint.Height = height;
        destination.PlacedFootprint.Footprint.Depth = 1;
        destination.PlacedFootprint.Footprint.RowPitch = _slotRowPitch;

        TextureCopyLocation source = default;
        source.PResource = backbuffer;
        source.Type = TextureCopyType.SubresourceIndex;
        source.SubresourceIndex = 0;

        Device.GraphicsCommandList->CopyTextureRegion(&destination, 0, 0, 0, &source, sourceBox);

        slot.Width = width;
        slot.Height = height;
        slot.RowPitch = _slotRowPitch;
        slot.FrameIndex = frameIndex;

        // fenceValues[FrameIndex] is the value MoveToNextFrame signals at the end
        // of this frame, so it is exactly the point where the copy has landed.
        slot.FenceValue = Device.fenceValues[Device.FrameIndex];
    }

    /// <summary>
    /// Called once per frame after Present / MoveToNextFrame. Delivers every slot
    /// whose fence has already completed without ever blocking, then releases the
    /// ring when the session has been stopped.
    /// </summary>
    internal static void Tick()
    {
        if (_active != null && BaseApp.ActiveFrameCapture == null)
        {
            // The session was stopped. Every outstanding copy was submitted in an
            // earlier frame and MoveToNextFrame has already signaled its fence, so
            // draining right here cannot block for long, and it lets Stop() return
            // without waiting for more frames to be rendered.
            Retire();
            return;
        }

        DrainCompleted();
    }

    /// <summary>
    /// Drains and releases everything. Used when a session is replaced, when the
    /// device is torn down, and when the swap-chain size changes.
    /// </summary>
    internal static void Retire()
    {
        if (_active == null && _slots.Length == 0) return;

        DrainAll();

        ReleaseSlots();

        var retired = _active;
        _active = null;
        retired?.SignalCompleted();
    }

    static void DrainCompleted()
    {
        if (_pending == 0) return;

        ulong completed = Device.GetCompletedFenceValue();

        while (_pending > 0 && _slots[_head].FenceValue <= completed)
            DeliverHead();
    }

    static void DrainAll()
    {
        while (_pending > 0)
        {
            ulong fenceValue = _slots[_head].FenceValue;

            // Never wait on the frame currently being recorded: its fence is only
            // signaled by MoveToNextFrame, which has not run yet. Both callers
            // release the ring right afterwards, so leaving the slot behind is safe.
            if (fenceValue >= PendingFrameFenceValue) return;

            if (fenceValue > Device.GetCompletedFenceValue())
                Device.DirectQueue.WaitForFence(fenceValue);

            DeliverHead();
        }
    }

    /// <summary>
    /// Fence value the frame being recorded right now will signal in
    /// MoveToNextFrame. Ring-fence values increase strictly monotonically, so any
    /// slot holding this value belongs to the unsubmitted current frame and must
    /// never be waited on.
    /// </summary>
    static ulong PendingFrameFenceValue => Device.fenceValues[Device.FrameIndex];

    static int AcquireSlot()
    {
        if (_pending == _slots.Length)
        {
            // Every slot is in flight: the encoder or the GPU is behind. Block on
            // the oldest one instead of losing the frame.
            ulong fenceValue = _slots[_head].FenceValue;

            // Unless the oldest slot is this very frame, which would deadlock. That
            // needs a second capture inside one frame, and skipping it is correct.
            if (fenceValue >= PendingFrameFenceValue) return -1;

            long stallStart = Stopwatch.GetTimestamp();

            if (fenceValue > Device.GetCompletedFenceValue())
                Device.DirectQueue.WaitForFence(fenceValue);

            DeliverHead();

            if (_active != null)
                _active.ReadbackStallMilliseconds +=
                    (long)Stopwatch.GetElapsedTime(stallStart).TotalMilliseconds;
        }

        int index = _tail;
        _tail = (_tail + 1) % _slots.Length;
        _pending++;
        return index;
    }

    static void DeliverHead()
    {
        ref var slot = ref _slots[_head];

        _head = (_head + 1) % _slots.Length;
        _pending--;

        ulong fenceValue = slot.FenceValue;
        slot.FenceValue = 0;

        var request = _active;
        if (request == null || fenceValue == 0 || slot.Buffer == null) return;

        void* mapped;
        if (slot.Buffer->Map(0, null, &mapped) != 0) return;

        int destinationStride = request.Width * 4;
        var buffer = request.Pool.Rent();

        try
        {
            bool sizeMismatch = slot.Width != (uint)request.Width || slot.Height != (uint)request.Height;
            if (sizeMismatch)
            {
                // A window resize mid-session must not break the encoder's
                // fixed-size invariant, so the frame is cropped or letterboxed
                // onto the locked canvas. Clearing first keeps the padding black
                // instead of showing the previous frame's leftovers.
                Array.Clear(buffer);
                request.SizeMismatchFrames++;
            }

            int rows = Math.Min((int)slot.Height, request.Height);
            int rowBytes = Math.Min((int)slot.Width, request.Width) * 4;

            fixed (byte* destination = buffer)
            {
                byte* source = (byte*)mapped;

                for (int row = 0; row < rows; row++)
                {
                    Buffer.MemoryCopy(
                        source + (long)row * slot.RowPitch,
                        destination + (long)row * destinationStride,
                        rowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            slot.Buffer->Unmap(0, null);
        }

        // Handed over outside the map window: OnFrame may block on encoder back
        // pressure, and holding a mapped readback buffer while blocking would pin
        // the slot for no reason.
        request.DeliveredFrames++;
        request.OnFrame(buffer, slot.FrameIndex);
    }

    static void EnsureSlots(uint width, uint height)
    {
        int slotCount = Math.Clamp(_active?.ReadbackSlots ?? 4, 2, 8);

        if (_slots.Length == slotCount && _slotWidth == width && _slotHeight == height)
            return;

        // Deliver what is already in flight before the old buffers go away.
        DrainAll();
        ReleaseSlots();

        // D3D12 requires readback row pitch aligned to
        // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT (256 bytes).
        _slotRowPitch = ((width * 4) + 255) & ~255u;
        _slotBytes = _slotRowPitch * height;
        _slotWidth = width;
        _slotHeight = height;

        var slots = new Slot[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            var heapProperties = new HeapProperties(HeapType.Readback);
            var bufferDesc = new ResourceDesc(
                ResourceDimension.Buffer,
                0,
                _slotBytes,
                1, 1, 1,
                Format.FormatUnknown,
                new SampleDesc(1, 0),
                TextureLayout.LayoutRowMajor,
                ResourceFlags.None);

            Guid riid = ID3D12Resource.Guid;
            void* resource;
            int hr = Device.D3dDevice->CreateCommittedResource(
                &heapProperties, HeapFlags.None, &bufferDesc,
                ResourceStates.CopyDest, null,
                &riid, &resource);

            if (hr != 0)
            {
                // Out of readback memory: keep the slots created so far and run
                // with a shallower ring rather than failing the whole session.
                Array.Resize(ref slots, i);
                break;
            }

            slots[i].Buffer = (ID3D12Resource*)resource;
        }

        _slots = slots;
        _head = 0;
        _tail = 0;
        _pending = 0;
    }

    static void ReleaseSlots()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            var buffer = _slots[i].Buffer;
            if (buffer == null) continue;

            if (_slots[i].FenceValue != 0)
            {
                // The GPU may still be copying into this slot, so hand it to the
                // deferred queue instead of freeing it underneath an in-flight copy.
                var pending = (IntPtr)buffer;
                Device.EnqueueDeferredRelease(
                    Device.GetCurrentRetireFenceValue(),
                    () => ((ID3D12Resource*)pending)->Release());
            }
            else
            {
                buffer->Release();
            }

            _slots[i].Buffer = null;
        }

        _slots = [];
        _slotWidth = 0;
        _slotHeight = 0;
        _slotRowPitch = 0;
        _slotBytes = 0;
        _head = 0;
        _tail = 0;
        _pending = 0;
    }
}
