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
