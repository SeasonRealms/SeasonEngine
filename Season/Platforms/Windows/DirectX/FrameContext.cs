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

    private readonly ID3D12Device* _device;
    DXDrawSnapshotArena? _drawSnapshots;

    // 仅按需分配；同帧追加，上一轮 fence 完成后才回收页和描述符。
    internal DXDrawSnapshotArena DrawSnapshots
    {
        get
        {
            if (CommandList == null || CommandList != Device.GraphicsCommandList)
                throw new InvalidOperationException("只能捕获当前帧的绘制资源。");
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

        // Snapshot the pointer value before Reset
        var cmdListBefore = (IntPtr)CommandList;

        CommandAllocator->Reset();
        CommandList->Reset(CommandAllocator, pso);

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

/// <summary>不可变绘制数据的帧内副本。上传页和独立 SRV 跨帧复用，不保留可变控件。</summary>
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
    // 帧内保留的资源身份（几何缓冲按引用捕获时需要 AddRef 撑到 GPU 安全点）。
    readonly HashSet<nint> _retainedSet = new();
    readonly Dictionary<(nint Resource, ulong Offset, uint Size, uint Alignment), (Slice Slice, nint Mapped)> _bufferCopies = new();
    // 视图缓存的键：直接对资源身份 + 视图描述符字节做等值比较。元组里放 ShaderResourceViewDesc
    // 会走 ValueType 的反射式 Equals/GetHashCode，逐图元查询时实测占总耗时的四分之一。
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
        if (stamp == 0 || stamp < _stamp) throw new InvalidOperationException("绘制快照帧标记无效。");
        if (stamp == _stamp) return;
        if (_stamp > completed) throw new InvalidOperationException("绘制快照仍在 GPU 使用中，不能复用。");
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
            throw new InvalidOperationException("绘制快照只能在所属帧捕获。");
    }

    static ulong Align(ulong value, uint alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    internal Slice CaptureBytes(ReadOnlySpan<byte> bytes, uint alignment = 256)
    {
        ValidateFrame();
        if (bytes.IsEmpty || alignment == 0 || alignment > 65536)
            throw new ArgumentOutOfRangeException(nameof(alignment), "快照必须包含数据，且对齐量位于 1..65536。");
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

    // 深复制 CPU 可写资源，而非仅 AddRef；后续 Update/Dispose 不得改写本帧已捕获的内容。
    // 只用于仍可能是跨图元复用环的缓冲（2D 四边形/文本的矩阵与实例数据）。
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
            throw new InvalidOperationException("可变绘制缓冲必须来自 CPU 可读的 Upload heap。");
        void* mapped;
        hr = source->Map(0, null, &mapped);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            var key = ((nint)source, offset, size, alignment);
            var bytes = new ReadOnlySpan<byte>((byte*)mapped + offset, (int)size);
            // 同帧共享 VB/IB/骨骼经常被多个图元使用。只有字节仍完全相同时才能复用，
            // 不能仅按资源地址缓存，否则控件在两次 Draw 之间更新会污染快照。
            if (_bufferCopies.TryGetValue(key, out var cached)
                && bytes.SequenceEqual(new ReadOnlySpan<byte>((void*)cached.Mapped, (int)size))) return cached.Slice;
            var slice = CaptureBytes(bytes, alignment);
            _bufferCopies[key] = (slice, (nint)(_pages[_pageCursor].Mapped + slice.Offset));
            return slice;
        }
        finally
        {
            // 捕获只读取原缓冲，不能宣布原缓冲被 CPU 改写。
            Silk.NET.Direct3D12.Range written = default;
            source->Unmap(0, &written);
        }
    }

    internal GpuDescriptorHandle CaptureStructuredBuffer(ID3D12Resource* source, uint stride, uint count)
    {
        if (stride == 0 || stride > 2048 || stride % 4 != 0 || count == 0)
            throw new ArgumentOutOfRangeException(nameof(stride));
        // 结构化缓冲（骨骼调色板、形变权重）与顶点缓冲一样在帧内不可变：直绘路径绑定的就是
        // 同一资源的预建 SRV。因此直接在源资源上建视图，不再整块深拷贝上传数据。
        var desc = new ShaderResourceViewDesc
        {
            ViewDimension = SrvDimension.Buffer, Shader4ComponentMapping = 0x1688,
            Buffer = new() { FirstElement = 0, NumElements = count, StructureByteStride = stride }
        };
        return CaptureView(source, desc);
    }

    // 从资源和原始视图描述重建 SRV；禁止从 shader-visible heap 复制描述符。
    // 即使源对象撤销/改写自己的描述符，捕获的资源身份也保留到 GPU 安全点。
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

    // 几何缓冲（VB/IB）在帧内不可变：直绘路径也直接把原始 GPU 地址交给命令列表，
    // 寿命契约同样是“撑到本帧结束”。因此这里只需要保留引用，不再逐帧深复制。
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
