// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

internal unsafe class FrameContext
{
    private ID3D12CommandAllocator* _commandAllocator;
    private ID3D12GraphicsCommandList* _commandList;

    public ID3D12CommandAllocator* CommandAllocator
    {
        get => _commandAllocator;
        private set
        {
            if (_commandAllocator != null && value == null)
                System.Diagnostics.Debug.WriteLine(
                    $"[FrameContext] CommandAllocator SET TO NULL! Stack:\n{Environment.StackTrace}");
            _commandAllocator = value;
        }
    }

    public ID3D12GraphicsCommandList* CommandList
    {
        get => _commandList;
        private set
        {
            if (_commandList != null && value == null)
                System.Diagnostics.Debug.WriteLine(
                    $"[FrameContext] CommandList SET TO NULL! Stack:\n{Environment.StackTrace}");
            _commandList = value;
        }
    }
    public ID3D12Resource* RenderTarget { get; set; }
    public ulong FenceValue { get; set; } = 1;

    /// <summary>
    /// Whether this frame's command list is currently recording. D3D12 rejects an allocator Reset while
    /// any list created from it is still open, and the shutdown path cannot assume the normal close ran:
    /// AfterRender checks for device removal before it closes anything.
    /// </summary>
    public bool IsRecording { get; private set; }

    /// <summary>Called by the owner once this frame's list has been closed.</summary>
    public void MarkClosed() => IsRecording = false;

    /// <summary>
    /// Closes a list that is still recording so its allocator becomes resettable. Returns true when the
    /// list is closed on return; false when the close itself failed, in which case the caller must skip
    /// the Reset - issuing it against an open list raises its own debug-layer error and hides the
    /// original fault.
    /// </summary>
    public bool CloseIfRecording()
    {
        if (!IsRecording) return true;
        if (CommandList == null) return false;
        MarkClosed();
        return CommandList->Close() >= 0;
    }

    private readonly ID3D12Device* _device;
    DXDrawSnapshotArena? _drawSnapshots;

    // Allocated on demand only; appended within the same frame, and pages and descriptors are reclaimed
    // only after the previous round's fence completes.
    internal DXDrawSnapshotArena DrawSnapshots
    {
        get
        {
            if (CommandList == null || CommandList != Device.GraphicsCommandList)
                throw new InvalidOperationException("Only the current frame's draw resources can be captured.");
            var arena = _drawSnapshots ??= new DXDrawSnapshotArena();
            arena.BeginFrame(Device.GetCurrentRetireFenceValue(), Device.GetCompletedFenceValue());
            return arena;
        }
    }

    public FrameContext(ID3D12Device* device)
    {
        _device = device;
    }

    public void Initialize(ID3D12PipelineState* initialPso)
    {
        // Create the command allocator
        ID3D12CommandAllocator* allocator;
        var iid = ID3D12CommandAllocator.Guid;
        var result = _device->CreateCommandAllocator(CommandListType.Direct, &iid, (void**)&allocator);
        if (result != 0) throw new Exception($"Failed to create command allocator: {result}");
        CommandAllocator = allocator;

        // Create the command list
        ID3D12GraphicsCommandList* commandList;
        iid = ID3D12GraphicsCommandList.Guid;
        result = _device->CreateCommandList(0, CommandListType.Direct, allocator, initialPso, &iid, (void**)&commandList);
        if (result != 0) throw new Exception($"Failed to create command list: {result}");

        // The initial state is open, so close it
        commandList->Close();
        CommandList = commandList;
    }

    public void Reset(ID3D12PipelineState* pso = null)
    {
        if (CommandAllocator == null)
            throw new InvalidOperationException(
                "FrameContext.CommandAllocator is null. " +
                "Initialize() must be called (via Device.CreateGraphicsCommandLists()) before Reset().");

        if (CommandList == null)
            throw new InvalidOperationException(
                "FrameContext.CommandList is null. " +
                "Initialize() must be called (via Device.CreateGraphicsCommandLists()) before Reset().");

        // A frame that threw before it reached its own Close() leaves this list recording: AfterRender
        // runs its removal check and its capture while the list is still open. D3D12 then rejects the
        // allocator Reset below, and that second error is louder than whatever went wrong first.
        if (!CloseIfRecording())
            throw new InvalidOperationException(
                "FrameContext.Reset cannot continue: the previous frame's command list is still recording " +
                "and failed to close, so its allocator cannot be reset.");

        // Snapshot the pointer value before Reset
        var cmdListBefore = (IntPtr)CommandList;

        // A removed device faults here instead of returning its HRESULT: this Reset is only the first D3D12
        // call of the frame, so it must not be reported as the cause. The filter keeps a genuine API misuse
        // on its own exception path - only a device that is actually gone is relabelled.
        try
        {
            CommandAllocator->Reset();
            CommandList->Reset(CommandAllocator, pso);
        }
        catch (SEHException exception) when (Device.IsDeviceGone())
        {
            throw Device.DeviceRemovedFromSeh(exception, "resetting the frame's command allocator and list");
        }

        IsRecording = true;

        // Snapshot and compare the pointer value after Reset
        var cmdListAfter = (IntPtr)CommandList;
        if (cmdListBefore != cmdListAfter)
            System.Diagnostics.Debug.WriteLine(
                $"[FrameContext.Reset] CommandList pointer CHANGED! Before=0x{cmdListBefore:X16} After=0x{cmdListAfter:X16}\n{Environment.StackTrace}");
    }

    public void SetRenderTarget(ID3D12Resource* renderTarget)
    {
        RenderTarget = renderTarget;
    }

    public void Dispose()
    {
        _drawSnapshots?.Dispose();
        _drawSnapshots = null;
        if (CommandList != null)
        {
            CommandList->Release();
            CommandList = null;
        }
        if (CommandAllocator != null)
        {
            CommandAllocator->Release();
            CommandAllocator = null;
        }
        // RenderTarget is managed by SwapChain and is not released here
    }
}

/// <summary>Frame-local copy of immutable draw data. Upload pages and standalone SRVs are reused across frames, and no mutable control is retained.</summary>
internal unsafe sealed class DXDrawSnapshotArena : IDisposable
{
    internal readonly record struct Slice(nint Resource, ulong Offset, uint Size)
    {
        internal ulong GpuAddress => ((ID3D12Resource*)Resource)->GetGPUVirtualAddress() + Offset;
    }

    sealed class Page : IDisposable
    {
        internal ID3D12Resource* Resource;
        internal byte* Mapped;
        internal readonly ulong Capacity;
        internal ulong Used;
        internal Page(ulong capacity)
        {
            Capacity = capacity;
            Resource = Device.ResourceManager.CreateBuffer(HeapType.Upload, capacity, ResourceStates.GenericRead);
            void* mapped;
            int hr = Resource->Map(0, null, &mapped);
            if (hr < 0) { Resource->Release(); Resource = null; Marshal.ThrowExceptionForHR(hr); }
            Mapped = (byte*)mapped;
        }
        public void Dispose()
        {
            if (Resource == null) return;
            Resource->Unmap(0, null); Resource->Release(); Resource = null; Mapped = null;
        }
    }

    readonly List<Page> _pages = new();
    readonly List<int> _descriptors = new();
    readonly List<nint> _retained = new();
    // Resource identities retained within the frame (a geometry buffer captured by reference needs an AddRef to last until the GPU safe point).
    readonly HashSet<nint> _retainedSet = new();
    readonly Dictionary<(nint Resource, ulong Offset, uint Size, uint Alignment), (Slice Slice, nint Mapped)> _bufferCopies = new();
    // View cache key: compare resource identity + view descriptor bytes directly for equality. Putting a
    // ShaderResourceViewDesc in a tuple would go through ValueType's reflective Equals/GetHashCode, which
    // measured a quarter of the total time on per-primitive lookups.
    readonly struct ViewKey : IEquatable<ViewKey>
    {
        internal readonly nint Resource;
        internal readonly ShaderResourceViewDesc Desc;
        internal ViewKey(nint resource, in ShaderResourceViewDesc desc) { Resource = resource; Desc = desc; }

        public bool Equals(ViewKey other)
        {
            if (Resource != other.Resource) return false;
            var self = Desc;
            var that = other.Desc;
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref self, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref that, 1)));
        }

        public override bool Equals(object? obj) => obj is ViewKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(Resource, Desc.Format, (int)Desc.ViewDimension, Desc.Texture2D.MipLevels);
    }
    readonly Dictionary<ViewKey, GpuDescriptorHandle> _views = new();
    int _pageCursor, _descriptorCursor;
    ulong _stamp;
    bool _disposed;
    internal int PageCount => _pages.Count;
    internal int DescriptorCount => _descriptors.Count;

    internal void BeginFrame(ulong stamp, ulong completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stamp == 0 || stamp < _stamp) throw new InvalidOperationException("Invalid draw snapshot frame stamp.");
        if (stamp == _stamp) return;
        if (_stamp > completed) throw new InvalidOperationException("The draw snapshot is still in use by the GPU and cannot be reused.");
        ReleaseRetained();
        _bufferCopies.Clear(); _views.Clear();
        foreach (var page in _pages) page.Used = 0;
        _pageCursor = _descriptorCursor = 0;
        _stamp = stamp;
    }

    void ValidateFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stamp == 0 || _stamp != Device.GetCurrentRetireFenceValue())
            throw new InvalidOperationException("A draw snapshot can only be captured in its owning frame.");
    }

    static ulong Align(ulong value, uint alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    internal Slice CaptureBytes(ReadOnlySpan<byte> bytes, uint alignment = 256)
    {
        ValidateFrame();
        if (bytes.IsEmpty || alignment == 0 || alignment > 65536)
            throw new ArgumentOutOfRangeException(nameof(alignment), "A snapshot must contain data, and the alignment must be within 1..65536.");
        while (true)
        {
            if (_pageCursor == _pages.Count)
                _pages.Add(new Page(Math.Max(65536UL, Align((ulong)bytes.Length, 65536))));
            var page = _pages[_pageCursor];
            ulong offset = Align(page.Used, alignment);
            if (offset <= page.Capacity && (ulong)bytes.Length <= page.Capacity - offset)
            {
                bytes.CopyTo(new Span<byte>(page.Mapped + offset, bytes.Length));
                page.Used = offset + (uint)bytes.Length;
                return new Slice((nint)page.Resource, offset, (uint)bytes.Length);
            }
            _pageCursor++;
        }
    }

    // Deep-copy the CPU-writable resource rather than only AddRef; subsequent Update/Dispose must not
    // rewrite what was already captured this frame. Used only for buffers that may still be a per-primitive
    // reuse ring (the matrix and instance data of 2D quads/text).
    internal Slice CaptureBuffer(ID3D12Resource* source, ulong offset, uint size, uint alignment = 256)
    {
        ValidateFrame();
        if (source == null) throw new ArgumentNullException(nameof(source));
        var desc = source->GetDesc();
        if (desc.Dimension != ResourceDimension.Buffer || size == 0 || size > int.MaxValue
            || offset > desc.Width || size > desc.Width - offset)
            throw new ArgumentOutOfRangeException(nameof(size));
        HeapProperties heap; HeapFlags flags;
        int hr = source->GetHeapProperties(&heap, &flags);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (heap.Type != HeapType.Upload)
            throw new InvalidOperationException("A mutable draw buffer must come from a CPU-readable Upload heap.");
        void* mapped;
        hr = source->Map(0, null, &mapped);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            var key = ((nint)source, offset, size, alignment);
            var bytes = new ReadOnlySpan<byte>((byte*)mapped + offset, (int)size);
            // A VB/IB/skeleton shared within the frame is often used by multiple primitives. Reuse is only
            // possible while the bytes are still exactly identical; caching by resource address alone is not
            // enough, otherwise a control updated between two Draws would pollute the snapshot.
            if (_bufferCopies.TryGetValue(key, out var cached)
                && bytes.SequenceEqual(new ReadOnlySpan<byte>((void*)cached.Mapped, (int)size))) return cached.Slice;
            var slice = CaptureBytes(bytes, alignment);
            _bufferCopies[key] = (slice, (nint)(_pages[_pageCursor].Mapped + slice.Offset));
            return slice;
        }
        finally
        {
            // Capture only reads the source buffer, so it must not declare the source buffer as written by the CPU.
            Silk.NET.Direct3D12.Range written = default;
            source->Unmap(0, &written);
        }
    }

    internal GpuDescriptorHandle CaptureStructuredBuffer(ID3D12Resource* source, uint stride, uint count)
    {
        if (stride == 0 || stride > 2048 || stride % 4 != 0 || count == 0)
            throw new ArgumentOutOfRangeException(nameof(stride));
        // A structured buffer (skeleton palette, morph weights) is immutable within the frame just like a
        // vertex buffer: the direct draw path binds the pre-built SRV of the very same resource. So the view
        // is created on the source resource directly, without deep-copying the whole upload data again.
        var desc = new ShaderResourceViewDesc
        {
            ViewDimension = SrvDimension.Buffer, Shader4ComponentMapping = 0x1688,
            Buffer = new() { FirstElement = 0, NumElements = count, StructureByteStride = stride }
        };
        return CaptureView(source, desc);
    }

    // Rebuild the SRV from the resource and the original view descriptor; copying a descriptor from the
    // shader-visible heap is forbidden. Even if the source object revokes/rewrites its own descriptor, the
    // captured resource identity is retained until the GPU safe point.
    internal GpuDescriptorHandle CaptureView(ID3D12Resource* resource, ShaderResourceViewDesc desc)
    {
        ValidateFrame();
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        var key = new ViewKey((nint)resource, in desc);
        if (_views.TryGetValue(key, out var captured)) return captured;
        if (_descriptorCursor == _descriptors.Count) _descriptors.Add(Device.DescriptorAllocator.Allocate());
        int id = _descriptors[_descriptorCursor++];
        resource->AddRef();
        _retained.Add((nint)resource);
        _retainedSet.Add((nint)resource);
        Device.D3dDevice->CreateShaderResourceView(resource, &desc, Device.SrvHeapManager.GetCpuHandle(id));
        var view = Device.SrvHeapManager.GetGpuHandle(id);
        _views.Add(key, view);
        return view;
    }

    // A geometry buffer (VB/IB) is immutable within the frame: the direct draw path also hands the raw GPU
    // address to the command list, with the same lifetime contract of "last until the end of this frame".
    // So only a reference needs to be retained here, and no per-frame deep copy is made.
    internal void Retain(ID3D12Resource* resource)
    {
        if (resource == null || !_retainedSet.Add((nint)resource)) return;
        resource->AddRef();
        _retained.Add((nint)resource);
    }

    void ReleaseRetained()
    {
        foreach (nint resource in _retained) ((ID3D12Resource*)resource)->Release();
        _retained.Clear();
        _retainedSet.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Device.EnqueueDeferredRelease(_stamp, () =>
        {
            ReleaseRetained();
            foreach (var page in _pages) page.Dispose();
            foreach (int id in _descriptors) Device.DescriptorAllocator.Free(id);
            _pages.Clear(); _descriptors.Clear(); _bufferCopies.Clear(); _views.Clear();
        });
    }
}
