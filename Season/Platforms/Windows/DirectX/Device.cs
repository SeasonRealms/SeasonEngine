// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Season.Basic;

namespace Season.Platforms.Windows.DirectX;

internal unsafe static class Device
{
    internal static D3D12 D3D12 = D3D12.GetApi();

    internal static Silk.NET.DXGI.DXGI Dxgi = Silk.NET.DXGI.DXGI.GetApi(null);

    internal static ID3D12Device* D3dDevice;

    internal static DescriptorAllocator DescriptorAllocator { get; } = new(2048);

    internal static Vector4 BackgroundColor;

    internal static Format DepthBufferFormat;

    internal static Format BackBufferFormat;

    internal static uint frameCount = 3;

    internal static SwapChain SwapChain;

    internal static FrameContext[] FrameContexts;

    internal static Display Display;

    // Convenient accessor for the current frame context
    internal static FrameContext CurrentFrame => FrameContexts[FrameIndex];

    internal static ID3D12CommandQueue* CommandQueue;

    // Access through FrameContext for compatibility
    internal static ID3D12CommandAllocator* CommandAllocator => CurrentFrame.CommandAllocator;

    internal static ID3D12GraphicsCommandList* GraphicsCommandList => CurrentFrame.CommandList;

    internal static ID3D12CommandQueue* CopyCommandQueue;

    internal static ID3D12CommandAllocator* CopyCommandAllocator;

    internal static ID3D12GraphicsCommandList* CopyGraphicsCommandList;

    /// <summary>Command list on the graphics queue dedicated to in-place texture
    /// pixel updates (singleton, reset after each use).</summary>
    internal static ID3D12CommandAllocator* UploadCommandAllocator;
    internal static ID3D12GraphicsCommandList* UploadCommandList;

    /// <summary>Independent command list on the graphics queue dedicated to
    /// ExecuteImmediateDirectTransition.
    /// It must stay separate from UploadCommandList; otherwise the render
    /// thread's UploadPixels and the loading thread's
    /// ExecuteImmediateDirectTransition can record into the same command list
    /// concurrently and crash Allocator.Reset.</summary>
    internal static ID3D12CommandAllocator* TransitionCommandAllocator;
    internal static ID3D12GraphicsCommandList* TransitionCommandList;

    /// <summary>Serializes the full ExecuteImmediateDirectTransition flow
    /// (record / submit / wait / reset), ensuring TransitionFence signal values
    /// stay consistent with queue submission order so a later signal cannot wake
    /// an earlier wait prematurely.</summary>
    static readonly object _immediateTransitionLock = new object();

    // Access through SwapChainManager for compatibility
    internal static IDXGISwapChain3* SwapChainNative => SwapChain != null ? SwapChain.NativeSwapChain : null;

    static IDXGIFactory4* DxgiFactory;

    // Access through Display for compatibility
    internal static uint _msaaSampleCount => Display?.MsaaSampleCount ?? 4;
    internal static uint _msaaQualityLevels => Display?.MsaaQualityLevels ?? 0;
    internal static ID3D12Resource* msaaRenderTarget => Display != null ? Display.MsaaRenderTarget : null;

    // ── HDR + Tone Mapping (1-4 finalized) D3D12-specific rules ──
    // For the cross-platform contract (quality tiers / ACES constants / clear
    // linearization / inverse-ACES text handling / single injection point for
    // exposure), see the shared RenderQuality class header summary. This backend
    // follows it item by item. D3D12-specific details:
    // 1. HdrSceneColor is decided by WindowsApp from RenderQuality before
    //    CreateSwapChain / Pipeline.Init (it drives PSO / MSAA target / resolve
    //    source format derivation) and must not change afterward.
    //    false = Step 2 baseline (BackbufferCompatible path, one-step fallback).
    // 2. Single-point format derivation: the main PSO, MSAA target, and
    //    ResolveSubresource all derive from SceneColorFormat. Do not hardcode
    //    formats on side paths. FinalBlit auto-selects tonemap variants from the
    //    source RT format (four BlitPipeline variants).
    // 3. HDR_CHAIN is injected at compile time: prepend #define HDR_CHAIN to the
    //    main shader / blit shader source (decided by HdrSceneColor), which
    //    keeps runtime branching at zero. Switching quality tiers requires a
    //    restart because the PSOs are already baked.
    // 4. Exposure is uploaded through two paths: the FinalBlit root constant
    //    (b0, SetGraphicsRoot32BitConstant every frame) plus
    //    main-pipeline b1.Params0.Y (SceneLightParams, used to keep text immune
    //    to inverse-ACES exposure changes). The single injection point for b1 is
    //    DXPrimitiveGroup.SetLighting. Every path that writes the lighting CB,
    //    including Update, must go through it. Raw Unsafe.Write causes the
    //    shader to read hdrExposure=0, which was the root cause of the washed-out
    //    gray-text issue.
    internal static bool HdrSceneColor;

    /// <summary>Actual color format of the Scene pass render target. Main-PSO
    /// baking, MSAA target creation, and resolve all derive from this.</summary>
    internal static Format SceneColorFormat => HdrSceneColor ? Format.FormatR16G16B16A16Float : BackBufferFormat;

    /// <summary>
    /// HDR chain exposure scale: this backend's read point for
    /// RenderQuality.HdrExposure (runtime knob, 1.0 = neutral).
    /// See rule 4 above for upload paths.
    /// </summary>
    internal static float HdrExposure => RenderQuality.Current.HdrExposure;

    /// <summary>
    /// Clear-color linearization for the HDR chain: convert display-space
    /// BackgroundColor to an approximate linear value with pow(2.2), then feed it
    /// into FinalBlit as the HDR scene's linear base color for the
    /// exposure + ACES + gamma chain.
    /// From Step B onward, the background goes through the full tone-mapping
    /// chain, so slight visual differences from the LDR baseline are expected.
    /// Alpha stays unchanged.
    /// </summary>
    internal static Vector4 LinearizeClearColor(in Vector4 c) => new(
        MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);

    // ── CaptureApp GPU readback ──
    static ID3D12Resource* _captureReadbackBuffer;
    static uint _captureWidth;
    static uint _captureHeight;
    static bool _capturePending;
    /// <summary>Byte size the readback buffer was allocated for, so it can be
    /// rebuilt when the window grows instead of copying past its end.</summary>
    static uint _captureReadbackBytes;
    /// <summary>Fence value recorded by CaptureBackBuffer; CompleteCapture waits
    /// for it before calling Map.</summary>
    static ulong _captureFenceValue;

    internal static uint FrameIndex;

    internal static ID3D12Resource*[] renderTargets => _renderTargetsCache;

    private static ID3D12Resource*[] _renderTargetsCache;

    internal static Silk.NET.Direct3D12.Viewport Viewport => Display?.Viewport ?? default;

    internal static Box2D<int> _scissorRect => Display?.ScissorRect ?? default;

    internal static RtvHeapManager RtvHeapManager;

    internal static DsvHeapManager DsvHeapManager;

    internal static DescriptorHeapManager SrvHeapManager;

    internal static CommandQueue DirectQueue;

    internal static CommandQueue CopyQueue;

    internal static ResourceManager ResourceManager;

    internal static TextureUploadBatch textureUploadBatch;

    internal static ID3D12Fence* Fence;

    internal static IntPtr FenceEvent;

    internal static ulong[] fenceValues;

    internal static ID3D12Fence* CopyFence;

    internal static IntPtr CopyFenceEvent;

    /// <summary>
    /// Dedicated fence event handle for ExecuteImmediateDirectTransition.
    /// Used when the background loading thread submits to the Direct Queue via
    /// EnsureCommonForCopyQueue -> ExecuteImmediateDirectTransition. It is kept
    /// separate from the render thread's FenceEvent to avoid cross-thread
    /// SetEventOnCompletion overwrites that can trigger
    /// CommandAllocator.Reset SEHException.
    /// </summary>
    internal static IntPtr TransitionFenceEvent;

    /// <summary>
    /// Dedicated fence event handle for UploadPixels.
    /// Used when the background loading / material replacement thread submits
    /// in-place texture updates on the Direct Queue. It stays separate from the
    /// render thread's FenceEvent to avoid cross-thread
    /// SetEventOnCompletion overwrites that can make WaitForGpu return early and
    /// then trigger FrameContext.CommandAllocator.Reset SEHException.
    /// </summary>
    internal static IntPtr UploadFenceEvent;

    /// <summary>
    /// Dedicated fence for ExecuteImmediateDirectTransition.
    /// The immediate-execution path must never signal arbitrary values on the
    /// ring fence (Fence / fenceValues), such as GetCompletedValue()+2.
    /// Doing so corrupts the monotonic completed value relied on by
    /// MoveToNextFrame / PumpDeferredReleases, which can skip frame waits, reset
    /// in-flight allocators too early, or release still-referenced resources.
    /// </summary>
    internal static ID3D12Fence* TransitionFence;
    static ulong _transitionFenceValue;

    /// <summary>Dedicated fence for UploadPixels (same rationale as
    /// TransitionFence).</summary>
    internal static ID3D12Fence* UploadFence;
    static long _uploadFenceValue;

    internal static ulong NextUploadFenceValue() => (ulong)Interlocked.Increment(ref _uploadFenceValue);

    internal static ulong copyFenceValue;

    internal static DXTexture White;

    internal static Dictionary<string, DXTexture> DictionaryDXTexture = new Dictionary<string, DXTexture>();

    internal static void Init(bool debug)
    {
        BackBufferFormat = Format.FormatR8G8B8A8Unorm;

        BackgroundColor = new Vector4(1f, 1f, 1f, 1f);

        DepthBufferFormat = Format.FormatD32Float;

        var dxgiFactoryFlags = TryEnableDebugLayer(debug) ? 0x01 : 0u;

        IDXGIFactory4* dxgiFactory;
        var iid = IDXGIFactory4.Guid;
        var result = Dxgi.CreateDXGIFactory2(dxgiFactoryFlags, &iid, (void**)&dxgiFactory);
        CheckResult(result);
        DxgiFactory = dxgiFactory;

        IDXGIAdapter1* dxgiAdapter;
        if (false) // WARP adapter disabled
        {
            iid = IDXGIAdapter.Guid;

            result = dxgiFactory->EnumWarpAdapter(&iid, (void**)&dxgiAdapter);
            CheckResult(result);
        }
        else
        {
            dxgiAdapter = GetHardwareAdapter((IDXGIFactory1*)dxgiFactory);
        }

        ID3D12Device* d3dDevice;
        iid = ID3D12Device.Guid;
        result = D3D12.CreateDevice((IUnknown*)dxgiAdapter, D3DFeatureLevel.Level110, &iid, (void**)&d3dDevice);
        CheckResult(result);
        D3dDevice = d3dDevice;

        ResourceManager = new ResourceManager(d3dDevice);

        // ID3D12InfoQueue exists only when the Debug Layer is enabled. If
        // Graphics Tools is not installed, QueryInterface returns E_NOINTERFACE,
        // which is expected and should not be treated as an error.
        iid = ID3D12InfoQueue.Guid;
        ID3D12InfoQueue* infoQueue = null;
        result = d3dDevice->QueryInterface(&iid, (void**)&infoQueue);

        if (HResult.IndicatesSuccess(result) && infoQueue != null)
        {
#if DEBUG
            // Break on Error/Corruption messages so the IDE stops directly at the
            // offending D3D call instead of guessing later from
            // GetDeviceRemovedReason.
            infoQueue->SetBreakOnSeverity(MessageSeverity.Corruption, 1);
            infoQueue->SetBreakOnSeverity(MessageSeverity.Error, 1);
#endif
        }

        DirectQueue = new CommandQueue(d3dDevice, CommandListType.Direct);
        CommandQueue = DirectQueue.NativeQueue;

        CopyQueue = new CommandQueue(d3dDevice, CommandListType.Copy);
        CopyCommandQueue = CopyQueue.NativeQueue;

        // Initialize the FrameContexts array
        FrameContexts = new FrameContext[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            FrameContexts[i] = new FrameContext(d3dDevice);
        }

        ID3D12CommandAllocator* copyCommandAllocator;

        iid = ID3D12CommandAllocator.Guid;
        result = d3dDevice->CreateCommandAllocator(CommandListType.Copy, &iid, (void**)&copyCommandAllocator);
        CheckResult(result);

        CopyCommandAllocator = copyCommandAllocator;

        ID3D12Fence* fence;

        Fence = DirectQueue.Fence;
        FenceEvent = DirectQueue.FenceEvent;

        fenceValues = new ulong[frameCount];
        Array.Fill(fenceValues, 1ul);

        CopyFence = CopyQueue.Fence;
        CopyFenceEvent = CopyQueue.FenceEvent;

        copyFenceValue = 1;

        // Create a dedicated fence event handle for the background loading
        // thread's ExecuteImmediateDirectTransition to avoid cross-thread
        // SetEventOnCompletion overwrites caused by sharing FenceEvent with the
        // render thread.
        TransitionFenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);
        if (TransitionFenceEvent == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        // Create a dedicated fence event handle for UploadPixels to avoid
        // conflicts with the FenceEvent used by MoveToNextFrame / WaitForGpu.
        UploadFenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);
        if (UploadFenceEvent == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        // Immediate paths (Transition / UploadPixels) each use their own fence.
        // Never signal arbitrary values on the ring fence, or frame sync and
        // deferred-release checks will break.
        iid = ID3D12Fence.Guid;

        ID3D12Fence* transitionFence;
        result = d3dDevice->CreateFence(0, FenceFlags.None, &iid, (void**)&transitionFence);
        CheckResult(result);
        TransitionFence = transitionFence;

        ID3D12Fence* uploadFence;
        result = d3dDevice->CreateFence(0, FenceFlags.None, &iid, (void**)&uploadFence);
        CheckResult(result);
        UploadFence = uploadFence;
    }

    static IDXGIAdapter1* GetHardwareAdapter(IDXGIFactory1* pFactory)
    {
        IDXGIAdapter1* adapter;

        // TODO DXGI_ERROR_NOT_FOUND is 0x887A0002 - maybe we should add Winerror.h somewhere in Silk.NET.Core?
        const int errorNotFound = unchecked((int)0x887A0002);

        for (var adapterIndex = 0u; errorNotFound != pFactory->EnumAdapters1(adapterIndex, &adapter); ++adapterIndex)
        {
            AdapterDesc1 desc;
            _ = adapter->GetDesc1(&desc);

            if ((desc.Flags & (uint)AdapterFlag.Software) != 0)
            {
                // Don't select the Basic Render Driver adapter.
                // If you want a software adapter, pass in "/warp" on the command line.
                continue;
            }

            // Check to see if the adapter supports the required Direct3D version, but don't create the
            // actual device yet.

            var iid = ID3D12Device.Guid;

            if (HResult.IndicatesSuccess(D3D12.CreateDevice((IUnknown*)adapter, D3DFeatureLevel.Level110, &iid, null)))
            {
                break;
            }
        }

        return adapter;
    }

    static bool TryEnableDebugLayer(bool debug)
    {
#if DEBUG
        // Enable the debug layer (requires the Graphics Tools "optional feature").
        // NOTE: Enabling the debug layer after device creation will invalidate the active device.

        using ComPtr<ID3D12Debug> debugController = null;
        var iid = ID3D12Debug.Guid;
        var hr = D3D12.GetDebugInterface(&iid, (void**)&debugController);

        if (HResult.IndicatesSuccess(hr))
        {
            debugController.EnableDebugLayer();
            //debugController.Get().EnableDebugLayer();
            //Log.LogInformation("Debug layer enabled");
            return debug = true;
        }
        else
        {
            //Log.LogWarning
            //(
            //    Marshal.GetExceptionForHR(hr),
            //    $"Failed to enable debug layer, failed with result {hr} (0x{hr:x8})"
            //);
        }
#endif

        return false;
    }

    internal static void CreateSwapChain(int width, int height)
    {
        if (SwapChain != null)
        {
            // Resize the existing swap chain
            SwapChain.Resize(width, height);
            Display.Resize(width, height);
        }
        else
        {
            // Create a new swap chain and display manager
            SwapChain = new SwapChain(DxgiFactory, CommandQueue, frameCount, BackBufferFormat);
            var swapChainPanel = WindowsApp.Window.Content;
            SwapChain.CreateForSwapChainPanel(swapChainPanel, width, height);

            // 2-1 contract rule 5: derive the MSAA sample count from the resolved
            // AA tier (mutually exclusive single choice, with fallback already
            // applied during WindowsApp initialization). Only the Msaa4x tier
            // creates an MSAA target; all other tiers use 1x.
            uint msaaSampleCount = RenderQuality.Current.AntiAliasing == Season.Rendering.AaMode.Msaa4x ? 4u : 1u;
            Display = new Display(D3dDevice, BackBufferFormat, DepthBufferFormat, msaaSampleCount);
            Display.SetClearColor(BackgroundColor);
            Display.Initialize(width, height);
        }

        // Update the frame index
        FrameIndex = SwapChain.CurrentBackBufferIndex;

        // Cache backbuffer references
        _renderTargetsCache = new ID3D12Resource*[frameCount];
        for (uint i = 0; i < frameCount; i++)
        {
            _renderTargetsCache[i] = SwapChain.GetBackBuffer(i);
        }

        // Update each FrameContext render target
        for (int i = 0; i < frameCount; i++)
        {
            FrameContexts[i].SetRenderTarget(_renderTargetsCache[i]);
        }

        CheckResult();
    }

    internal static void CreateDescriptorHeapsAndViews()
    {
        // RTV heap layout: [0..frameCount-1] backbuffers, [frameCount] MSAA,
        // [frameCount+1..] reserved offscreen RT slots (Step 2)
        RtvHeapManager = new RtvHeapManager(D3dDevice);
        RtvHeapManager.InitializeHeap(frameCount + 1 + OffscreenRtvCapacity);

        DsvHeapManager = new DsvHeapManager(D3dDevice);
        // DSV heap layout: slot 0 = global depth,
        // [1..OffscreenDsvCapacity] = reserved offscreen depth-only RT slots
        // (Step 3)
        DsvHeapManager.InitializeHeap(1 + OffscreenDsvCapacity);

        // SRV heap: capacity matches DescriptorAllocator
        SrvHeapManager = new DescriptorHeapManager(D3dDevice);
        SrvHeapManager.InitializeSrvHeap((uint)DescriptorAllocator.Capacity);

        var dsvDesc = new DepthStencilViewDesc
        {
            Format = DepthBufferFormat,
            ViewDimension = (_msaaSampleCount > 1) ? DsvDimension.Texture2Dms : DsvDimension.Texture2D
        };
        D3dDevice->CreateDepthStencilView(Display.DepthStencil, &dsvDesc, DsvHeapManager.GetCpuHandle());

        CheckResult();

        // Create RTVs for swap-chain buffers
        for (var i = 0u; i < frameCount; i++)
        {
            D3dDevice->CreateRenderTargetView(renderTargets[i], null, RtvHeapManager.GetCpuHandle(i));
        }

        // Create the RTV for the MSAA render target
        if (_msaaSampleCount > 1)
        {
            D3dDevice->CreateRenderTargetView(msaaRenderTarget, null, RtvHeapManager.GetCpuHandle(frameCount));
        }

        // Pass abstraction: wrap backbuffer / MSAA target for state tracking
        RebuildRenderTargetWrappers();

        textureUploadBatch = new TextureUploadBatch(D3dDevice);

        // White texture is created during Pipeline.Init(), which runs before frame-context
        // command lists are initialized. Create transfer command lists here so early texture
        // uploads have a valid copy/upload path.
        if (CopyGraphicsCommandList == null)
            CopyGraphicsCommandList = CreateCopyGraphicsCommandLists();
        if (UploadCommandList == null)
            UploadCommandList = CreateUploadCommandList();
        if (TransitionCommandList == null)
            TransitionCommandList = CreateTransitionCommandList();
    }

    internal static void CreateGraphicsCommandLists()
    {
        // Initialize each frame's command list from its FrameContext
        for (int i = 0; i < frameCount; i++)
        {
            FrameContexts[i].Initialize(Pipeline.OpaquePipelineState);
        }

        if (CopyGraphicsCommandList == null)
            CopyGraphicsCommandList = CreateCopyGraphicsCommandLists();
        if (UploadCommandList == null)
            UploadCommandList = CreateUploadCommandList();
        if (TransitionCommandList == null)
            TransitionCommandList = CreateTransitionCommandList();
    }

    static unsafe ID3D12GraphicsCommandList* CreateCopyGraphicsCommandLists()
    {
        ID3D12GraphicsCommandList* graphicsCommandList;

        var iid = ID3D12GraphicsCommandList.Guid;
        var result = D3dDevice->CreateCommandList(nodeMask: 0, CommandListType.Copy, CopyCommandAllocator, null, &iid, (void**)&graphicsCommandList);

        CheckResult(result);

        result = graphicsCommandList->Close();
        CheckResult(result);

        result = graphicsCommandList->Reset(CopyCommandAllocator, null);

        CheckResult(result);

        return graphicsCommandList;
    }

    static unsafe ID3D12GraphicsCommandList* CreateUploadCommandList()
    {
        ID3D12CommandAllocator* allocator;
        var allocIid = ID3D12CommandAllocator.Guid;
        var result = D3dDevice->CreateCommandAllocator(CommandListType.Direct, &allocIid, (void**)&allocator);
        CheckResult(result);
        UploadCommandAllocator = allocator;

        ID3D12GraphicsCommandList* cmdList;
        var iid = ID3D12GraphicsCommandList.Guid;
        result = D3dDevice->CreateCommandList(nodeMask: 0, CommandListType.Direct, allocator, null, &iid, (void**)&cmdList);
        CheckResult(result);

        result = cmdList->Close();
        CheckResult(result);
        result = cmdList->Reset(allocator, null);
        CheckResult(result);

        return cmdList;
    }

    static unsafe ID3D12GraphicsCommandList* CreateTransitionCommandList()
    {
        ID3D12CommandAllocator* allocator;
        var allocIid = ID3D12CommandAllocator.Guid;
        var result = D3dDevice->CreateCommandAllocator(CommandListType.Direct, &allocIid, (void**)&allocator);
        CheckResult(result);
        TransitionCommandAllocator = allocator;

        ID3D12GraphicsCommandList* cmdList;
        var iid = ID3D12GraphicsCommandList.Guid;
        result = D3dDevice->CreateCommandList(nodeMask: 0, CommandListType.Direct, allocator, null, &iid, (void**)&cmdList);
        CheckResult(result);

        result = cmdList->Close();
        CheckResult(result);
        result = cmdList->Reset(allocator, null);
        CheckResult(result);

        return cmdList;
    }

    internal static ID3D12Resource* CreateVertexBuffer<T>(uint length, out VertexBufferView vertexBufferView) where T : unmanaged
    {
        return ResourceManager.CreateVertexBuffer<T>(length, out vertexBufferView);
    }

    internal static void SetVertexBuffer<T>(ID3D12Resource* vertexBuffer, VertexBufferView vertexBufferView, T[] vertexs) where T : unmanaged
    {
        ResourceManager.UpdateBuffer(vertexBuffer, vertexBufferView.SizeInBytes, vertexs);
    }

    internal static ID3D12Resource* CreateVertexBuffer<T>(T[] vertexs, out VertexBufferView vertexBufferView) where T : unmanaged
    {
        var buffer = ResourceManager.CreateVertexBuffer<T>((uint)vertexs.Length, out vertexBufferView);
        ResourceManager.UpdateBuffer(buffer, vertexBufferView.SizeInBytes, vertexs);
        return buffer;
    }

    internal static ID3D12Resource* CreateIndexBuffer(uint[] indices, out IndexBufferView indexBufferView)
    {
        return ResourceManager.CreateIndexBuffer(indices, out indexBufferView);
    }

    internal static ID3D12Resource* CreateConstantBuffer(out byte* _mappedConstantBuffer)
    {
        return ResourceManager.CreateConstantBuffer((uint)Unsafe.SizeOf<MatrixBuffer>(), out _mappedConstantBuffer);
    }

    /// <summary>
    /// Blocks the CPU until the GPU finishes all submitted commands. Used only
    /// during shutdown / destruction paths.
    /// </summary>
    internal static bool CanWaitForGpu()
        => DirectQueue is not null && fenceValues is not null;

    internal static ulong GetCurrentRetireFenceValue()
        => fenceValues is null ? 1 : Math.Max(1, fenceValues[FrameIndex]);
    //=> fenceValues is null ? 0 : fenceValues[FrameIndex];

    internal static ulong GetCompletedFenceValue()
        => Fence == null ? 0 : Fence->GetCompletedValue();

    internal static void WaitForGpu()
    {
        ulong currentFenceValue = fenceValues[FrameIndex];
        DirectQueue.Signal(currentFenceValue);
        DirectQueue.WaitForFence(currentFenceValue);
        fenceValues[FrameIndex] = currentFenceValue + 1;
    }

    /// <summary>
    /// Shutdown only: reset all command allocators so they release the internal
    /// GPU-resource references held by recorded command lists, allowing later
    /// ID3D12Resource::Release() calls in PumpDeferredReleases(force: true) to
    /// pass the Debug Layer.
    /// Must be called after WaitForGpu() and before PumpDeferredReleases(true).
    /// </summary>
    internal static void ResetAllAllocatorsForShutdown()
    {
        // 1) FrameContext allocators (per-frame render commands reference every
        // texture involved in drawing and PSO binding)
        if (FrameContexts != null)
        {
            for (int i = 0; i < frameCount; i++)
            {
                // AfterRender leaves the command list closed, so Reset() is safe
                FrameContexts[i].Reset();
            }
        }

        // 2) Singleton allocator for the Copy queue
        if (CopyCommandAllocator != null && CopyGraphicsCommandList != null)
        {
            CopyGraphicsCommandList->Close();
            CopyCommandAllocator->Reset();
            CopyGraphicsCommandList->Reset(CopyCommandAllocator, null);
        }

        // 3) Graphics-queue allocator used by UploadPixels
        if (UploadCommandAllocator != null && UploadCommandList != null)
        {
            UploadCommandList->Close();
            UploadCommandAllocator->Reset();
            UploadCommandList->Reset(UploadCommandAllocator, null);
        }

        // 4) Dedicated allocator used by ExecuteImmediateDirectTransition
        if (TransitionCommandAllocator != null && TransitionCommandList != null)
        {
            TransitionCommandList->Close();
            TransitionCommandAllocator->Reset();
            TransitionCommandList->Reset(TransitionCommandAllocator, null);
        }
    }

    // ── Deferred release queue (same mechanism as VK Device.EnqueueDeferredRelease;
    //    it used to live under Graphics and was moved here so DirectX runtime
    //    destruction paths can reuse it, such as reclaiming old primitives after
    //    Outline capacity growth) ──
    readonly struct DeferredReleaseItem
    {
        public readonly ulong FenceValue;
        public readonly Action ReleaseAction;

        public DeferredReleaseItem(ulong fenceValue, Action releaseAction)
        {
            FenceValue = fenceValue;
            ReleaseAction = releaseAction;
        }
    }

    static readonly object _deferredReleaseLock = new object();
    static readonly Queue<DeferredReleaseItem> _deferredReleases = new Queue<DeferredReleaseItem>();

    /// <summary>Enqueues a release action into the deferred-release queue.
    /// Thread-safe and callable from loading threads.
    /// `fenceValue` must come from <c>GetCurrentRetireFenceValue()</c>: every
    /// in-flight frame signal value earlier than it is smaller, so the release is
    /// executed only after the GPU fence advances past that value. This matches
    /// VK Device.EnqueueDeferredRelease. The VK side also provides a convenience
    /// overload <c>EnqueueDeferredRelease(Action)</c> that implicitly fetches the
    /// retire value; this backend exposes only the explicit signature.</summary>
    internal static void EnqueueDeferredRelease(ulong fenceValue, Action releaseAction)
    {
        if (releaseAction == null)
            return;

        lock (_deferredReleaseLock)
        {
            _deferredReleases.Enqueue(new DeferredReleaseItem(fenceValue, releaseAction));
        }
    }

    /// <summary>Executes all deferred releases whose GPU fence has already been
    /// passed; when `force=true`, executes everything after the GPU is known to
    /// be idle.
    /// Call sites (mirrors VK Device.PumpDeferredReleases):
    ///   - every frame after AfterRender in the WindowsApp frame loop, without
    ///     force (see the main render loop in WindowsApp.cs);
    ///   - on the WindowsApp shutdown path after <c>WaitForGpu()</c> +
    ///     <c>ResetAllAllocatorsForShutdown()</c>, with force=true
    ///     (see the shutdown flow in WindowsApp.cs).</summary>
    internal static void PumpDeferredReleases(bool force = false)
    {
        ulong completedFence = force ? ulong.MaxValue : GetCompletedFenceValue();

        while (true)
        {
            DeferredReleaseItem item;

            lock (_deferredReleaseLock)
            {
                if (_deferredReleases.Count == 0)
                    break;

                item = _deferredReleases.Peek();
                if (!force && item.FenceValue > completedFence)
                    break;

                _deferredReleases.Dequeue();
            }

            item.ReleaseAction();
        }
    }

    /// <summary>
    /// Rebuilds the swap-chain backbuffers, MSAA RT, DepthStencil, and RTV/DSV
    /// descriptors after the window size changes.
    /// Must be called on the render thread before BeforeRender and must not run
    /// concurrently with in-flight command lists.
    /// Returns true if a rebuild really happened (and WaitForGpu has already
    /// completed). Returns false if it was skipped (invalid size /
    /// ResizeSemaphore timeout / exception). In that case the GPU is not idle, so
    /// the caller must not continue into BaseApp.Resize(), because ResizeCompute
    /// would destroy and rebuild compute-storage resources that are still
    /// referenced by in-flight command lists. Keep the resize flag and retry next
    /// frame.
    /// </summary>
    internal static bool HandleResize(int width, int height)
    {
        if (SwapChain == null || width <= 0 || height <= 0) return false;

        // 0) Exclude the background loading thread: wait for the current load to
        // complete, or give up on timeout. This prevents background GPU resource
        // work (texture creation / upload / state transitions) during resize,
        // which would make the Debug Layer detect in-flight commands during
        // CommandAllocator.Reset and raise SEHException.
        bool acquired = false;
        try
        {
            acquired = BaseApp.ResizeSemaphore.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (ObjectDisposedException ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [DX] HandleResize: ResizeSemaphore disposed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [DX] HandleResize: Wait threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (!acquired)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [DX] HandleResize: ResizeSemaphore wait timed out (background loading?), skip resize this frame");
            return false;
        }
        try
        {

        // 1) Wait for the GPU to finish all in-flight commands so ResizeBuffers
        // does not race an in-flight backbuffer.
        // DirectQueue is monotonically ordered, so Signal+Wait on the current
        // frame synchronizes all earlier submissions.
        // Note: do not wait on every fenceValues[i]. After the increment at the
        // end of MoveToNextFrame, the value at the current FrameIndex is "the next
        // value that will be signaled", which the GPU can never reach yet, and
        // SilkMarshal.WaitWindowsObjects(FenceEvent) would block forever.
        WaitForGpu();

        // 2) Reset all FrameContext command allocators so they release the COM
        // references to the backbuffer held by recorded commands.
        // CommandAllocator::Reset is the only way to release the refcounts added
        // by OMSetRenderTargets / ResourceBarrier and similar operations inside
        // command lists. Releasing only the SwapChain's own references is not
        // enough for the Debug Layer to allow ResizeBuffers.
        // After Reset, the command list is back in recording state, so it must be
        // closed immediately to restore the closed state. Otherwise the next
        // Reset in BeforeRender triggers SEHException because allocators require
        // closed command lists.
        for (int i = 0; i < frameCount; i++)
        {
            FrameContexts[i].Reset();
            FrameContexts[i].CommandList->Close();
        }

        // 3) Release the old backbuffer weak references stored by FrameContexts
        // (field clear only, no COM refcount involved).
        for (int i = 0; i < frameCount; i++)
            FrameContexts[i].SetRenderTarget(null);

        // 4) Resize the SwapChain (internally ReleaseBackBuffers + ResizeBuffers
        // + AcquireBackBuffers)
        SwapChain.Resize(width, height);

        // 5) Resize the Display (release and rebuild the MSAA RT, DepthStencil,
        // viewport, and scissor)
        Display.Resize(width, height);

        // 5.5) Rebuild offscreen RTs that follow the backbuffer size (step 1 has
        // already waited for the GPU, so the old resources can be released
        // immediately)
        RecreateOffscreenRenderTargets(width, height);

        // 6) Refresh _renderTargetsCache and FrameContext.RenderTarget pointers
        for (uint i = 0; i < frameCount; i++)
        {
            _renderTargetsCache[i] = SwapChain.GetBackBuffer(i);
            FrameContexts[(int)i].SetRenderTarget(_renderTargetsCache[i]);
        }

        // 7) Rewrite RTVs: reuse the existing CPU handles and overwrite them with
        // CreateRenderTargetView
        for (uint i = 0; i < frameCount; i++)
            D3dDevice->CreateRenderTargetView(_renderTargetsCache[i], null, RtvHeapManager.GetCpuHandle(i));

        // 8) Rewrite the MSAA RTV
        if (_msaaSampleCount > 1)
            D3dDevice->CreateRenderTargetView(msaaRenderTarget, null, RtvHeapManager.GetCpuHandle(frameCount));

        // 9) Rewrite the DSV (the DepthStencil resource has been rebuilt)
        var dsvDesc = new DepthStencilViewDesc
        {
            Format = DepthBufferFormat,
            ViewDimension = (_msaaSampleCount > 1) ? DsvDimension.Texture2Dms : DsvDimension.Texture2D
        };
        D3dDevice->CreateDepthStencilView(Display.DepthStencil, &dsvDesc, DsvHeapManager.GetCpuHandle());

        // 9.5) Refresh the pass RT wrappers because the backbuffer / MSAA target
        // pointers have changed
        RebuildRenderTargetWrappers();

        // 10) Resynchronize FrameIndex and fenceValues to a consistent baseline
        FrameIndex = SwapChain.CurrentBackBufferIndex;
        ulong completed = Fence->GetCompletedValue();
        for (int i = 0; i < frameCount; i++)
            fenceValues[i] = completed + 1;

        CheckResult();

        return true;

        }
        finally
        {
            BaseApp.ResizeSemaphore.Release();
        }
    }

    /// <summary>
    /// Ring fence flow: signal the current frame, switch to the next frame, and
    /// only let the CPU wait if the next frame's fence is still incomplete.
    /// </summary>
    internal static void MoveToNextFrame()
    {
        ulong currentFenceValue = fenceValues[FrameIndex];
        DirectQueue.Signal(currentFenceValue);

        FrameIndex = SwapChainNative->GetCurrentBackBufferIndex();

        // Block only if the fence from the previous use of this frame's
        // resources is still incomplete
        if (Fence->GetCompletedValue() < fenceValues[FrameIndex])
        {
            var hr = Fence->SetEventOnCompletion(fenceValues[FrameIndex], FenceEvent.ToPointer());
            CheckResult(hr);
            SilkMarshal.WaitWindowsObjects(FenceEvent);
        }

        fenceValues[FrameIndex] = currentFenceValue + 1;
    }

    internal static ulong CopySignal()
    {
        // Signal the Copy Queue fence, increment it, and return the current value
        ulong current = copyFenceValue;
        CopyQueue.Signal(current);
        copyFenceValue = current + 1;
        return current;
    }

    internal static void CopyWaitForCpu()
    {
        // Block the CPU until the Copy Queue finishes its latest submission
        CopyQueue.WaitForFence(copyFenceValue - 1);
    }

    internal static void DirectQueueWaitCopyFence(ulong fenceValue)
    {
        // Make the Direct Queue wait on the Copy Queue fence on the GPU side
        // so uploaded resources are used only after the copy completes
        var hr = CommandQueue->Wait(CopyFence, fenceValue);
        CheckResult(hr);
    }

    internal static unsafe void ExecuteImmediateDirectTransition(ID3D12Resource* resource, ResourceStates beforeState, ResourceStates afterState)
    {
        if (resource == null || beforeState == afterState)
        {
            return;
        }

        lock (_immediateTransitionLock)
        {
            var commandList = TransitionCommandList;
            var barrier = InitTransition(resource, beforeState, afterState);
            commandList->ResourceBarrier(1, &barrier);
            commandList->Close();

            ID3D12CommandList* commandListPtr = (ID3D12CommandList*)commandList;
            CommandQueue->ExecuteCommandLists(1, &commandListPtr);

            // Dedicated TransitionFence + monotonically increasing values:
            // the old implementation, Signal(Fence, GetCompletedValue()+2),
            // polluted the ring fence, causing MoveToNextFrame to skip frame
            // waits, PumpDeferredReleases to release resources too early, and
            // this wait itself to return spuriously when satisfied by a later
            // ring-fence signal.
            ulong fenceValue = ++_transitionFenceValue;
            CommandQueue->Signal(TransitionFence, fenceValue);
            while (TransitionFence->GetCompletedValue() < fenceValue)
            {
                TransitionFence->SetEventOnCompletion(fenceValue, TransitionFenceEvent.ToPointer());
                SilkMarshal.WaitWindowsObjects(TransitionFenceEvent);
            }

            // The GPU has finished this transition command. Reset the allocator
            // so it releases its internal references to texture resources.
            // Otherwise the Debug Layer will detect "resource still referenced"
            // during a later texture Dispose and raise SEHException.
            TransitionCommandAllocator->Reset();
            var result = TransitionCommandList->Reset(TransitionCommandAllocator, null);
            CheckResult(result);
        }
    }

    internal static ResourceBarrier InitTransition(ID3D12Resource* pResource, ResourceStates stateBefore, ResourceStates stateAfter,
uint subresource = D3D12.ResourceBarrierAllSubresources, ResourceBarrierFlags flags = ResourceBarrierFlags.None)
    {
        // TODO THIS IS A D3DX12 FUNCTION
        ResourceBarrier result = default;
        result.Type = ResourceBarrierType.Transition;
        result.Flags = flags;
        result.Anonymous.Transition.PResource = pResource;
        result.Anonymous.Transition.StateBefore = stateBefore;
        result.Anonymous.Transition.StateAfter = stateAfter;
        result.Anonymous.Transition.Subresource = subresource;
        return result;
    }

    // ── CaptureApp GPU readback implementation ──

    /// <summary>
    /// Pixel size of the backbuffer that capture and recording read from. Taken
    /// from the resource description rather than the viewport, because that is the
    /// surface CopyTextureRegion actually reads, and it falls back to the viewport
    /// only before the swap chain exists.
    /// </summary>
    internal static (int Width, int Height) GetBackBufferSize()
    {
        var targets = _renderTargetsCache;
        var backbuffer = targets != null && FrameIndex < targets.Length
            ? targets[FrameIndex]
            : null;

        if (backbuffer != null)
        {
            var desc = backbuffer->GetDesc();
            if (desc.Width > 0 && desc.Height > 0)
                return ((int)desc.Width, (int)desc.Height);
        }

        var viewport = Viewport;
        return ((int)viewport.Width, (int)viewport.Height);
    }

    /// <summary>
    /// Pixel size of the sub-rectangle of the backbuffer that is actually on
    /// screen. The swap chain is composed into the panel without an inverse
    /// composition-scale transform, so the renderer compensates the other way
    /// around: its output is shrunk into the top-left 1/CompositionScale corner of
    /// the backbuffer (the DPI transform in DXPrimitiveGroup.Update, matching the
    /// 2D "layout coordinates / scale" rule), and composition then magnifies that
    /// corner back to full panel size.
    /// Because that transform is applied after projection it widens the clip
    /// volume, so the rest of the backbuffer is not padding but real geometry from
    /// outside the visible frustum. Copying it out would make a screenshot or a
    /// recording show a wider view than the game does, aligned at the top-left and
    /// overshooting at the bottom-right.
    /// Degenerates to <see cref="GetBackBufferSize"/> when CompositionScale is 1,
    /// which is the case on every non-desktop backend.
    /// </summary>
    internal static (int Width, int Height) GetPresentedSize()
    {
        var (width, height) = GetBackBufferSize();

        var app = DeviceServices.BaseApp;
        float scaleX = app?.CompositionScale.X ?? 1f;
        float scaleY = app?.CompositionScale.Y ?? 1f;

        // A scale of 0 means the panel has not reported its composition scale
        // yet; the full backbuffer is the only meaningful answer at that point.
        if (scaleX > 1e-4f)
            width = Math.Clamp((int)MathF.Round(width / scaleX), 1, width);

        if (scaleY > 1e-4f)
            height = Math.Clamp((int)MathF.Round(height / scaleY), 1, height);

        return (width, height);
    }

    /// <summary>
    /// Shared copy-out for every readback consumer. The backbuffer (currently in
    /// <paramref name="startingState"/>) is transitioned to CopySource exactly
    /// once, each interested consumer records its own CopyTextureRegion inside
    /// that window, and the backbuffer then goes straight to Present. This keeps a
    /// screenshot taken during a recording session from costing a second pair of
    /// barriers.
    /// </summary>
    /// <param name="startingState">Current D3D12 resource state of the
    /// backbuffer (ResolveDest in the MSAA path).</param>
    /// <param name="singleShot">Serve a pending CaptureApp request; the result is
    /// mapped and delivered in CompleteCapture.</param>
    /// <param name="record">Serve the active recording session through
    /// <see cref="DXCaptureRing"/>.</param>
    /// <param name="recordFrameIndex">Constant-rate output frame index this
    /// backbuffer belongs to, or -1 when <paramref name="record"/> is false.</param>
    static void CopyBackBufferForCapture(ResourceStates startingState, bool singleShot, bool record, long recordFrameIndex)
    {
        var backbuffer = renderTargets[FrameIndex];
        if (backbuffer == null) return;

        var (width, height) = GetPresentedSize();
        if (width <= 0 || height <= 0) return;

        // Every consumer reads the same on-screen corner, never the whole
        // backbuffer; see GetPresentedSize for why the remainder must be dropped.
        var sourceBox = new Box
        {
            Left = 0,
            Top = 0,
            Front = 0,
            Right = (uint)width,
            Bottom = (uint)height,
            Back = 1,
        };

        // 1) Transition state: starting state -> CopySource
        var barrierToCopy = InitTransition(backbuffer, startingState, ResourceStates.CopySource);
        GraphicsCommandList->ResourceBarrier(1, &barrierToCopy);

        // 2) CopyTextureRegion: backbuffer -> readback buffer(s)
        if (singleShot)
            CopyToSingleShotReadback(backbuffer, (uint)width, (uint)height, &sourceBox);

        if (record)
            DXCaptureRing.Enqueue(backbuffer, recordFrameIndex, (uint)width, (uint)height, &sourceBox);

        // 3) Transition state: CopySource -> Present
        // (takes over the normal RenderTarget -> Present transition)
        var barrierToPresent = InitTransition(backbuffer, ResourceStates.CopySource, ResourceStates.Present);
        GraphicsCommandList->ResourceBarrier(1, &barrierToPresent);
    }

    static void CopyToSingleShotReadback(ID3D12Resource* backbuffer, uint width, uint height, Box* sourceBox)
    {
        _captureWidth = width;
        _captureHeight = height;

        // D3D12 requires row pitch alignment to
        // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT (256 bytes)
        uint rowPitch = ((_captureWidth * 4) + 255) & ~255u;
        uint totalBytes = rowPitch * _captureHeight;

        // Create or reuse the readback buffer. It must also be recreated when the
        // window grew, otherwise the copy would run past the end of a buffer sized
        // for the old resolution.
        if (_captureReadbackBuffer == null || _captureReadbackBytes < totalBytes)
        {
            if (_captureReadbackBuffer != null)
            {
                // The previous buffer can only be referenced by already completed
                // frames, because CompleteCapture drains every capture in the same
                // frame that recorded it.
                _captureReadbackBuffer->Release();
                _captureReadbackBuffer = null;
            }

            var heapProps = new HeapProperties(HeapType.Readback);
            var bufferDesc = new ResourceDesc(
                ResourceDimension.Buffer,
                0,
                totalBytes,
                1, 1, 1,
                Format.FormatUnknown,
                new SampleDesc(1, 0),
                TextureLayout.LayoutRowMajor,
                ResourceFlags.None);

            Guid riid = ID3D12Resource.Guid;
            void* pResource;
            int hr = D3dDevice->CreateCommittedResource(
                &heapProps, HeapFlags.None, &bufferDesc,
                ResourceStates.CopyDest, null,
                &riid, &pResource);
            CheckResult(hr);
            _captureReadbackBuffer = (ID3D12Resource*)pResource;
            _captureReadbackBytes = totalBytes;
        }

        TextureCopyLocation dstLoc = default;
        dstLoc.PResource = _captureReadbackBuffer;
        dstLoc.Type = TextureCopyType.PlacedFootprint;
        dstLoc.PlacedFootprint.Offset = 0;
        dstLoc.PlacedFootprint.Footprint.Format = BackBufferFormat;
        dstLoc.PlacedFootprint.Footprint.Width = _captureWidth;
        dstLoc.PlacedFootprint.Footprint.Height = _captureHeight;
        dstLoc.PlacedFootprint.Footprint.Depth = 1;
        dstLoc.PlacedFootprint.Footprint.RowPitch = rowPitch;

        TextureCopyLocation srcLoc = default;
        srcLoc.PResource = backbuffer;
        srcLoc.Type = TextureCopyType.SubresourceIndex;
        srcLoc.SubresourceIndex = 0;

        GraphicsCommandList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, sourceBox);

        _capturePending = true;
        // Record the current frame's fence value so CompleteCapture can wait for
        // CopyTextureRegion to finish before calling Map
        _captureFenceValue = fenceValues[FrameIndex];
    }

    /// <summary>
    /// Called after GPU execution completes (after Present + MoveToNextFrame).
    /// Maps the readback buffer and delivers it to the CaptureApp caller.
    /// </summary>
    internal static void CompleteCapture()
    {
        if (!_capturePending || _captureReadbackBuffer == null) return;
        _capturePending = false;

        // Wait for the GPU to finish CopyTextureRegion before mapping the
        // readback buffer, matching Vulkan Vk.DeviceWaitIdle / Metal
        // WaitUntilCompleted behavior.
        // The ring fence used by MoveToNextFrame guarantees only that the
        // swap-chain buffer can be reused, not that readback-buffer data is
        // already ready.
        if (_captureFenceValue > 0)
        {
            DirectQueue.WaitForFence(_captureFenceValue);
            _captureFenceValue = 0;
        }

        try
        {
            void* mappedData;
            int hr = _captureReadbackBuffer->Map(0, null, &mappedData);
            if (hr != 0) return;

            uint rowPitch = ((_captureWidth * 4) + 255) & ~255u;
            byte[] pixels = new byte[_captureWidth * _captureHeight * 4];

            fixed (byte* pDst = pixels)
            {
                byte* pSrc = (byte*)mappedData;
                for (uint row = 0; row < _captureHeight; row++)
                {
                    Buffer.MemoryCopy(
                        pSrc + row * rowPitch,
                        pDst + row * _captureWidth * 4,
                        _captureWidth * 4,
                        _captureWidth * 4);
                }
            }

            _captureReadbackBuffer->Unmap(0, null);

            // CaptureApp is meant to export the final visible image, not preserve
            // the intermediate alpha in the swap chain.
            // On Windows, backbuffer alpha near transparent edges is often an
            // intermediate coverage / blending value; writing it directly to PNG
            // causes dark or dirty edges.
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            var captureAppImage = new NativeImageData((int)_captureWidth, (int)_captureHeight, pixels);

            BaseApp.CaptureAppTcs?.TrySetResult(captureAppImage);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CaptureApp {ex}");

            BaseApp.CaptureAppTcs?.TrySetResult(null);
        }

        BaseApp.CaptureAppTcs = null;
    }

    // ── Pass scheduling (Step 1) ──
    // Pass wrappers for the backbuffer / MSAA target, maintained by
    // RebuildRenderTargetWrappers.
    internal static DXRenderTarget[] BackbufferRTs = null!;
    internal static DXRenderTarget? MsaaSceneRT;
    static DXRenderTarget? _activePass;

    /// <summary>Identifier of the current pass (set by BeginPass / restored by
    /// EndPass, default is Scene).
    /// Overlay uses this so Pipeline.SetPipeline can switch to a dedicated PSO
    /// without depth and with backbuffer format, because the main PSO's OM
    /// combination (SceneColorFormat / MSAA / DSV) is invalid under Overlay's OM
    /// state (single RTV, no DSV).</summary>
    internal static RenderPassId ActivePassId = RenderPassId.Scene;

    /// <summary>
    /// Rebuilds the DXRenderTarget wrappers for the backbuffer / MSAA target.
    /// Called once in CreateDescriptorHeapsAndViews and once in HandleResize
    /// after RTVs are rewritten.
    /// </summary>
    internal static void RebuildRenderTargetWrappers()
    {
        BackbufferRTs = new DXRenderTarget[frameCount];
        for (uint i = 0; i < frameCount; i++)
        {
            // The backbuffer starts in Present, matching the swap chain contract
            BackbufferRTs[i] = new DXRenderTarget(renderTargets[i], i, ResourceStates.Present, hasDepth: true);
        }

        // MSAA scene target: RTV index frameCount (reuses the existing heap
        // layout), created with initial RenderTarget state.
        // ColorNativeFormat records the actual format so BeginPass can
        // linearize clear colors correctly under the HDR chain.
        MsaaSceneRT = (_msaaSampleCount > 1)
            ? new DXRenderTarget(msaaRenderTarget, frameCount, ResourceStates.RenderTarget, hasDepth: true)
              { ColorNativeFormat = SceneColorFormat }
            : null;
    }

    // ── Offscreen RenderTargets (Step 2 color / Step 3 depth-only + non-full-size) ──
    // Fixed-capacity slots are reserved at the tail of the RTV/DSV heaps and
    // reused through a free-slot stack.
    // SRVs come from the existing DescriptorAllocator. On resize, resources are
    // rebuilt in the same slot and on the same wrapper object so external
    // references such as FrameSchedule.SceneColor / ShadowMap stay valid.

    const uint OffscreenRtvCapacity = 8;
    const uint OffscreenDsvCapacity = 4;
    static readonly Stack<uint> _freeOffscreenRtvSlots = new();
    static uint _nextOffscreenRtvSlot;
    static readonly Stack<int> _freeOffscreenDsvSlots = new();
    static int _nextOffscreenDsvSlot;
    static readonly List<DXRenderTarget> _offscreenRTs = new();

    static uint AllocateOffscreenRtvSlot()
    {
        if (_freeOffscreenRtvSlots.Count > 0)
            return _freeOffscreenRtvSlots.Pop();
        if (_nextOffscreenRtvSlot >= OffscreenRtvCapacity)
            throw new InvalidOperationException($"[CreateRenderTarget] Offscreen RTV slots are exhausted (capacity {OffscreenRtvCapacity}).");
        return frameCount + 1 + _nextOffscreenRtvSlot++;
    }

    static int AllocateOffscreenDsvSlot()
    {
        if (_freeOffscreenDsvSlots.Count > 0)
            return _freeOffscreenDsvSlots.Pop();
        if (_nextOffscreenDsvSlot >= OffscreenDsvCapacity)
            throw new InvalidOperationException($"[CreateRenderTarget] Offscreen DSV slots are exhausted (capacity {OffscreenDsvCapacity}).");
        return 1 + _nextOffscreenDsvSlot++;
    }

    static Format ToNativeColorFormat(RtFormat format) => format switch
    {
        RtFormat.BackbufferCompatible => BackBufferFormat,
        RtFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
        // 2-3 contract rule 2: motion vectors are UV-space displacements, can be
        // negative, and therefore must use a floating-point format
        RtFormat.Rg16Float => Format.FormatR16G16Float,
        _ => throw new NotSupportedException($"[CreateRenderTarget] Unsupported color format {format}."),
    };

    /// <summary>
    /// Creates an offscreen RenderTarget. Two shapes are supported (Step 3):
    /// - color-only: BackbufferCompatible / Rgba16Float, used for Scene/Post
    ///   output (reuses the global DSV when no private depth is needed);
    /// - depth-only: D32Float (typeless resource + D32 DSV + R32 SRV), used for
    ///   fixed-size shadow maps and 2-2 SceneDepth (full-size MatchBackbufferSize
    ///   depth input for Scene pass + compute).
    /// The color + private-depth combination currently has no consumer and is
    /// unsupported.
    /// </summary>
    internal static RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
    {
        bool hasColor = desc.ColorFormat != RtFormat.None;
        bool hasDepth = desc.DepthFormat != RtFormat.None;

        if (hasColor == hasDepth)
            throw new NotSupportedException("[CreateRenderTarget] Only color-only or depth-only targets are supported (color + private depth currently has no consumer).");
        if (hasDepth && desc.DepthFormat != RtFormat.D32Float)
            throw new NotSupportedException($"[CreateRenderTarget] Only D32Float is supported for depth format (received {desc.DepthFormat}).");

        var rt = new DXRenderTarget(null, hasColor ? AllocateOffscreenRtvSlot() : DXRenderTarget.NoRtv, ResourceStates.RenderTarget, hasDepth: false)
        {
            Desc = desc,
            OwnsResource = true,
            SrvIndex = DescriptorAllocator.Allocate(),
        };
        if (hasColor)
            rt.ColorNativeFormat = ToNativeColorFormat(desc.ColorFormat);
        if (hasDepth)
            rt.DsvIndex = AllocateOffscreenDsvSlot();

        uint width = desc.MatchBackbufferSize ? (uint)Display.Viewport.Width : desc.Width;
        uint height = desc.MatchBackbufferSize ? (uint)Display.Viewport.Height : desc.Height;
        CreateOffscreenResourceAndViews(rt, width, height);

        _offscreenRTs.Add(rt);
        return rt;
    }

    /// <summary>
    /// Creates GPU resources and views for an offscreen RT, or recreates them on
    /// resize. Reentrant: descriptors are overwritten in the same slots.
    /// Preconditions: the old resources are no longer referenced by in-flight
    /// work (initialization or after WaitForGpu in HandleResize).
    /// </summary>
    static void CreateOffscreenResourceAndViews(DXRenderTarget rt, uint width, uint height)
    {
        rt.Width = width;
        rt.Height = height;

        if (rt.Desc.ColorFormat != RtFormat.None)
            CreateOffscreenColorPlane(rt, width, height);
        if (rt.Desc.DepthFormat != RtFormat.None)
            CreateOffscreenDepthPlane(rt, width, height);

        rt.GpuSrvHandle = SrvHeapManager.GetGpuHandle(rt.SrvIndex);
    }

    static void CreateOffscreenColorPlane(DXRenderTarget rt, uint width, uint height)
    {
        if (rt.Color != null)
        {
            rt.Color->Release();
            rt.Color = null;
        }

        var resourceDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = rt.ColorNativeFormat,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget
        };

        var heapProps = new HeapProperties(HeapType.Default);

        // Align the optimized clear value with BeginPass.ClearRenderTargetView to
        // avoid slow-clear warnings:
        // - Rg16Float (velocity) always clears to zero;
        // - RTs with an explicit desc.ClearColor (for example OutlineMask, which
        //   always clears to zero) use that value;
        // - all other RTs use the scene background color (HDR RTs stay in sync
        //   with BeginPass clear-color linearization).
        Vector4 bg;
        if (rt.ColorNativeFormat == Format.FormatR16G16Float)
            bg = Vector4.Zero;
        else if (rt.Desc.ClearColor.HasValue)
            bg = rt.ColorNativeFormat == Format.FormatR16G16B16A16Float
                ? LinearizeClearColor(rt.Desc.ClearColor.Value)
                : rt.Desc.ClearColor.Value;
        else if (rt.ColorNativeFormat == Format.FormatR16G16B16A16Float)
            bg = LinearizeClearColor(BackgroundColor);
        else
            bg = BackgroundColor;
        var clearValue = new ClearValue { Format = rt.ColorNativeFormat };
        clearValue.Anonymous.Color[0] = bg.X;
        clearValue.Anonymous.Color[1] = bg.Y;
        clearValue.Anonymous.Color[2] = bg.Z;
        clearValue.Anonymous.Color[3] = bg.W;

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var result = D3dDevice->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            ResourceStates.RenderTarget,
            &clearValue,
            &iid,
            (void**)&resource);
        CheckResult(result);

        rt.Color = resource;
        rt.CurrentState = ResourceStates.RenderTarget;

        // RTV: overwrite the fixed slot
        D3dDevice->CreateRenderTargetView(rt.Color, null, RtvHeapManager.GetCpuHandle(rt.RtvIndex));

        // SRV: sampled by FinalBlit / later passes, overwritten in the same slot
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = rt.ColorNativeFormat,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = 0x00001688u,
            Texture2D = new Tex2DSrv { MipLevels = 1, MostDetailedMip = 0 }
        };
        D3dDevice->CreateShaderResourceView(rt.Color, &srvDesc, SrvHeapManager.GetCpuHandle(rt.SrvIndex));
    }

    /// <summary>
    /// Depth plane: R32_Typeless resource (DSV requires a D32_Float view, SRV
    /// requires an R32_Float view; creating the resource directly as D32Float
    /// would prevent SRV creation). Initial state is DepthWrite.
    /// </summary>
    static void CreateOffscreenDepthPlane(DXRenderTarget rt, uint width, uint height)
    {
        if (rt.Depth != null)
        {
            rt.Depth->Release();
            rt.Depth = null;
        }

        var resourceDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR32Typeless,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil
        };

        var heapProps = new HeapProperties(HeapType.Default);

        var clearValue = new ClearValue
        {
            Format = Format.FormatD32Float,
            Anonymous = new ClearValueUnion { DepthStencil = new DepthStencilValue(1.0f, 0) }
        };

        ID3D12Resource* resource;
        var iid = ID3D12Resource.Guid;
        var result = D3dDevice->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            ResourceStates.DepthWrite,
            &clearValue,
            &iid,
            (void**)&resource);
        CheckResult(result);

        rt.Depth = resource;
        rt.DepthCurrentState = ResourceStates.DepthWrite;

        // DSV: overwrite the private slot (typeless resources must specify the
        // view format explicitly)
        var dsvDesc = new DepthStencilViewDesc
        {
            Format = Format.FormatD32Float,
            ViewDimension = DsvDimension.Texture2D
        };
        D3dDevice->CreateDepthStencilView(rt.Depth, &dsvDesc, DsvHeapManager.GetCpuHandle((uint)rt.DsvIndex));

        // SRV: sampled by later passes as a shadow map (depth read as R32Float)
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR32Float,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = 0x00001688u,
            Texture2D = new Tex2DSrv { MipLevels = 1, MostDetailedMip = 0 }
        };
        D3dDevice->CreateShaderResourceView(rt.Depth, &srvDesc, SrvHeapManager.GetCpuHandle(rt.SrvIndex));
    }

    /// <summary>
    /// HandleResize helper: rebuild every offscreen RT that tracks the
    /// backbuffer size, while leaving fixed-size targets untouched.
    /// </summary>
    static void RecreateOffscreenRenderTargets(int width, int height)
    {
        foreach (var rt in _offscreenRTs)
        {
            if (rt.Desc.MatchBackbufferSize)
                CreateOffscreenResourceAndViews(rt, (uint)width, (uint)height);
        }
    }

    /// <summary>
    /// Releases an offscreen RT and recycles its RTV/DSV/SRV slots. The caller
    /// must guarantee that the GPU no longer references the resource
    /// (SceneColor / ShadowMap and similar objects live for the entire app
    /// lifetime and are not destroyed at runtime).
    /// </summary>
    internal static void DestroyOffscreenRenderTarget(DXRenderTarget rt)
    {
        if (!_offscreenRTs.Remove(rt))
            return;

        if (rt.Color != null)
        {
            rt.Color->Release();
            rt.Color = null;
        }

        if (rt.Depth != null)
        {
            rt.Depth->Release();
            rt.Depth = null;
        }

        if (rt.SrvIndex >= 0)
        {
            DescriptorAllocator.Free(rt.SrvIndex);
            rt.SrvIndex = -1;
        }

        if (rt.DsvIndex >= 0)
        {
            _freeOffscreenDsvSlots.Push(rt.DsvIndex);
            rt.DsvIndex = -1;
        }

        if (rt.RtvIndex != DXRenderTarget.NoRtv)
            _freeOffscreenRtvSlots.Push(rt.RtvIndex);
        rt.OwnsResource = false;
    }

    // ── GPU event markers (PIX / RenderDoc capture grouping) ──
    // BeginEvent metadata = PIX_EVENT_ANSI_VERSION(1), and pData is a
    // NUL-terminated ANSI string.
    // Without an attached tool the driver treats this as a no-op, so overhead is
    // negligible. Labels are pre-baked to avoid per-frame allocations.
    static readonly byte[][] _passLabels =
    [
        "Shadow\0"u8.ToArray(),
        "Scene\0"u8.ToArray(),
        "Post\0"u8.ToArray(),
        "OutlineMask\0"u8.ToArray(),
        "FinalBlit\0"u8.ToArray(),
        "Overlay\0"u8.ToArray(),
        "Pass\0"u8.ToArray(),
    ];

    internal static void PushDebugGroup(byte[] asciiLabelZ)
    {
        fixed (byte* pLabel = asciiLabelZ)
        {
            GraphicsCommandList->BeginEvent(1u, pLabel, (uint)asciiLabelZ.Length);
        }
    }

    internal static void PopDebugGroup()
    {
        GraphicsCommandList->EndEvent();
    }

    /// <summary>
    /// Per-frame setup: wait for the compositor to release the backbuffer, reset
    /// the command list, and bind descriptor heaps.
    /// Passes are no longer opened implicitly; Clear / RT binding now live in
    /// BeginPass.
    /// </summary>
    internal static void BeforeRender()
    {
        // === Wait for the DXGI compositor to release the backbuffer ===
        // This avoids whole-frame flicker when the CPU runs ahead of the
        // compositor (pink clears interleaving with the scene).
        // The waitable object signals only after the compositor has finished
        // presenting the backbuffer.
        if (SwapChain != null && SwapChain.FrameLatencyWaitableObject != IntPtr.Zero)
        {
            SilkMarshal.WaitWindowsObjects(SwapChain.FrameLatencyWaitableObject);
        }

        // Reset the command list and allocator through the FrameContext
        CurrentFrame.Reset(Pipeline.OpaquePipelineState);

        // Bind descriptor heaps once per frame; the binding remains valid across
        // passes
        ID3D12DescriptorHeap*[] heaps = [SrvHeapManager.Heap];
        fixed (ID3D12DescriptorHeap** heapsPtr = heaps)
        {
            GraphicsCommandList->SetDescriptorHeaps(1, heapsPtr);
        }
    }

    /// <summary>
    /// Begins a render pass.
    /// If desc.DepthTarget is non-null and there is no color target, it is a
    /// depth-only pass (Shadow).
    /// Otherwise, desc.ColorTarget == null means backbuffer and non-null means an
    /// offscreen RT. The Scene pass also supports dual explicit targets
    /// (2-2: ColorTarget + private SceneDepth instead of the global DSV).
    /// MSAA applies only to the Scene pass (render into the MSAA target, then
    /// resolve to ColorTarget/backbuffer in EndPass).
    /// Handles viewport/scissor (driven by target size) -> barrier -> Clear ->
    /// OMSetRenderTargets.
    /// </summary>
    internal static void BeginPass(in PassDesc desc)
    {
        ActivePassId = desc.Id;
        int passIndex = (int)desc.Id;
        if ((uint)passIndex >= (uint)(_passLabels.Length - 1))
            passIndex = _passLabels.Length - 1;
        PushDebugGroup(_passLabels[passIndex]);

        // Depth-only pass (Shadow): bind only the private DSV, with no color
        // target. The dual-explicit-target case (2-2 Scene + SceneDepth) goes
        // through the color path below.
        if (desc.ColorTarget == null && desc.DepthTarget is DXRenderTarget depthRT)
        {
            _activePass = depthRT;

            SetViewportScissor(depthRT.Width, depthRT.Height);

            depthRT.TransitionDepthTo(GraphicsCommandList, ResourceStates.DepthWrite);

            var depthDsvHandle = DsvHeapManager.GetCpuHandle((uint)depthRT.DsvIndex);
            if (desc.ClearDepthEnable)
            {
                GraphicsCommandList->ClearDepthStencilView(depthDsvHandle, ClearFlags.Depth, 1, 0, 0, null);
            }
            GraphicsCommandList->OMSetRenderTargets(0, null, 0, &depthDsvHandle);
            return;
        }

        // Resolve the color target: Scene+MSAA always uses the MSAA target
        // (resolve destination = explicit RT or backbuffer); all other passes use
        // the explicit RT or the backbuffer directly.
        var explicitRT = desc.ColorTarget as DXRenderTarget;
        DXRenderTarget colorRT;
        if (desc.Id == RenderPassId.Scene && _msaaSampleCount > 1)
        {
            if (MsaaSceneRT == null)
                throw new InvalidOperationException(
                    $"[BeginPass] MsaaSceneRT is null but _msaaSampleCount={_msaaSampleCount}. " +
                    "RebuildRenderTargetWrappers() may not have run after Display.Resize().");

            colorRT = MsaaSceneRT;
            colorRT.ResolveDest = explicitRT ?? BackbufferRTs[FrameIndex];
        }
        else
        {
            colorRT = explicitRT ?? BackbufferRTs[FrameIndex];
        }
        _activePass = colorRT;

        // Viewport/scissor are driven by the actual bound target: offscreen RTs
        // use their own size (supports non-full-size targets), while
        // backbuffer/MSAA uses Display, matching Step 1/2 behavior.
        if (colorRT.OwnsResource)
        {
            SetViewportScissor(colorRT.Width, colorRT.Height);
        }
        else
        {
            var viewport = Display.Viewport;
            var scissorRect = Display.ScissorRect;
            GraphicsCommandList->RSSetViewports(1, &viewport);
            GraphicsCommandList->RSSetScissorRects(1, &scissorRect);
        }

        // Transition the color target to RenderTarget. This is tracking-driven
        // and idempotent: offscreen RTs may end the previous frame in
        // PixelShaderResource / ResolveDest, and the backbuffer in Present, all
        // corrected automatically from CurrentState.
        colorRT.TransitionTo(GraphicsCommandList, ResourceStates.RenderTarget);

        var rtvHandle = RtvHeapManager.GetCpuHandle(colorRT.RtvIndex);

        if (desc.ClearColorEnable)
        {
            // Linearize clear colors for HDR targets (RGBA16F). This is the
            // inverse of the pow(1/2.2) used by FinalBlit tonemap variants, so
            // the background color remains visually consistent with the LDR
            // baseline while the LDR path passes through unchanged.
            var cc = colorRT.ColorNativeFormat == Format.FormatR16G16B16A16Float
                ? LinearizeClearColor(desc.ClearColor)
                : desc.ClearColor;
            float* clearColor = stackalloc float[4] { cc.X, cc.Y, cc.Z, cc.W };
            GraphicsCommandList->ClearRenderTargetView(rtvHandle, clearColor, 0, null);
        }

        if (desc.Id == RenderPassId.Scene || desc.Id == RenderPassId.OutlineMask)
        {
            // Scene / OutlineMask bind depth. Post/FinalBlit are single-sampled
            // fullscreen passes and do not read or write depth. When MSAA is
            // enabled, the global DSV is multisampled, and binding it together
            // with a single-sampled RT triggers a Debug Layer error.
            // 2-2: explicit SceneDepth (private depth for the AO tier,
            // single-sampled) takes priority. Msaa4x and AO are already made
            // mutually exclusive during initialization fallback, so a
            // multisampled color + single-sampled private depth mix cannot occur
            // here.
            // OutlineMask is a special case: the mask RT is always
            // single-sampled, and in the Msaa4x tier there is no private
            // SceneDepth to swap in because AO is forced off. Since the global
            // DSV is multisampled and cannot be mixed, this path degrades to no
            // depth binding (outlines skip occlusion tests but remain visible).
            var dsvHandle = DsvHeapManager.GetCpuHandle();
            if (desc.DepthTarget is DXRenderTarget sceneDepth)
            {
                sceneDepth.TransitionDepthTo(GraphicsCommandList, ResourceStates.DepthWrite);
                dsvHandle = DsvHeapManager.GetCpuHandle((uint)sceneDepth.DsvIndex);
            }
            bool bindDepth = desc.Id != RenderPassId.OutlineMask || _msaaSampleCount <= 1 || desc.DepthTarget is DXRenderTarget;

            // 2-3 contract rule 2: Scene uses three targets
            // (slot 0=color, slot 1=velocity, plus depth).
            // Offscreen RTV slots are not contiguous, so pass an array of
            // handles and set RTsSingleHandleToDescriptorRange=0.
            // MotionVectors and Msaa4x are already mutually exclusive after
            // initialization fallback, so this path never sees multisampled
            // color mixed with single-sampled velocity.
            if (desc.Id == RenderPassId.Scene && desc.VelocityTarget is DXRenderTarget velocityRT)
            {
                velocityRT.TransitionTo(GraphicsCommandList, ResourceStates.RenderTarget);

                var velocityRtvHandle = RtvHeapManager.GetCpuHandle(velocityRT.RtvIndex);

                // Velocity always clears to zero, matching the optimized clear
                // value: pixels not covered by geometry are static background.
                float* velocityClear = stackalloc float[4] { 0f, 0f, 0f, 0f };
                GraphicsCommandList->ClearRenderTargetView(velocityRtvHandle, velocityClear, 0, null);

                CpuDescriptorHandle* rtvs = stackalloc CpuDescriptorHandle[2] { rtvHandle, velocityRtvHandle };
                GraphicsCommandList->OMSetRenderTargets(2, rtvs, 0, bindDepth ? &dsvHandle : null);
            }
            else
            {
                GraphicsCommandList->OMSetRenderTargets(1, &rtvHandle, 1, bindDepth ? &dsvHandle : null);
            }

            if (bindDepth && desc.ClearDepthEnable)
            {
                GraphicsCommandList->ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1, 0, 0, null);
            }
        }
        else
        {
            GraphicsCommandList->OMSetRenderTargets(1, &rtvHandle, 1, null);
        }
    }

    static void SetViewportScissor(uint width, uint height)
    {
        var viewport = new Silk.NET.Direct3D12.Viewport
        {
            TopLeftX = 0,
            TopLeftY = 0,
            Width = width,
            Height = height,
            MinDepth = 0,
            MaxDepth = 1
        };
        var scissorRect = new Box2D<int>(Vector2D<int>.Zero, new Vector2D<int>((int)width, (int)height));
        GraphicsCommandList->RSSetViewports(1, &viewport);
        GraphicsCommandList->RSSetScissorRects(1, &scissorRect);
    }

    /// <summary>
    /// 1-5 contract rule 6: controlled viewport override inside the Shadow pass
    /// content code (the only exception).
    /// Sets an offset viewport+scissor per atlas quadrant
    /// (rectangle comes from CascadedShadow.GetAtlasViewport) without changing
    /// barriers or targets; only the rasterization area is repositioned.
    /// </summary>
    internal static void SetShadowViewport(int x, int y, int size)
    {
        var viewport = new Silk.NET.Direct3D12.Viewport
        {
            TopLeftX = x,
            TopLeftY = y,
            Width = size,
            Height = size,
            MinDepth = 0,
            MaxDepth = 1
        };
        var scissorRect = new Box2D<int>(new Vector2D<int>(x, y), new Vector2D<int>(x + size, y + size));
        GraphicsCommandList->RSSetViewports(1, &viewport);
        GraphicsCommandList->RSSetScissorRects(1, &scissorRect);
    }

    /// <summary>
    /// Ends the current render pass and finalizes according to the target:
    /// - depth-only (Shadow): transition depth to PixelShaderResource so later
    ///   passes can sample it directly;
    /// - MSAA Scene: resolve to ResolveDest
    ///   (backbuffer -> Present/Capture; offscreen -> remain in ResolveDest for
    ///   Blit to take over);
    /// - direct backbuffer rendering: transition to Present (readback for capture
    ///   and recording happens once per frame in AfterRender, not here);
    /// - offscreen without MSAA: remain in RenderTarget, and later consumers
    ///   (Post/Blit) will transition to PixelShaderResource themselves.
    /// </summary>
    internal static void EndPass()
    {
        var colorRT = _activePass;
        if (colorRT == null)
            throw new InvalidOperationException("[EndPass] called without matching BeginPass.");

        if (colorRT.Color == null && colorRT.Depth != null)
        {
            // Finalize depth-only (Shadow): next frame's BeginPass will
            // transition it back to DepthWrite through idempotent tracking.
            colorRT.TransitionDepthTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
        }
        else if (colorRT == MsaaSceneRT)
        {
            var dest = colorRT.ResolveDest!;   // Bound in BeginPass: backbuffer or offscreen SceneColor

            // Prepare resolve: MSAA target -> resolve source, destination ->
            // resolve destination
            colorRT.TransitionTo(GraphicsCommandList, ResourceStates.ResolveSource);
            dest.TransitionTo(GraphicsCommandList, ResourceStates.ResolveDest);

            // Execute resolve. The format follows the Scene target:
            // LDR = backbuffer format, HDR = RGBA16F
            GraphicsCommandList->ResolveSubresource(dest.Color, 0, colorRT.Color, 0, SceneColorFormat);

            // Restore msaaRenderTarget from ResolveSource back to RenderTarget so
            // the next frame can render into it again
            colorRT.TransitionTo(GraphicsCommandList, ResourceStates.RenderTarget);

            if (dest == BackbufferRTs[FrameIndex])
            {
                FinishBackbufferForPresent(dest);
            }
            // Offscreen destination stays in ResolveDest; BlitToBackbuffer will
            // later transition it to PixelShaderResource
        }
        else if (colorRT == BackbufferRTs[FrameIndex])
        {
            FinishBackbufferForPresent(colorRT);
        }
        // Offscreen without MSAA stays in RenderTarget and needs no final barrier

        _activePass = null;
        ActivePassId = RenderPassId.Scene;
        PopDebugGroup();
    }

    /// <summary>
    /// Finalization when the backbuffer is the pass endpoint: transition to
    /// Present. Readback is deliberately not done here, because the backbuffer is
    /// the endpoint of more than one pass per frame (FinalBlit and then Overlay),
    /// so capturing here would sometimes catch the frame before the 2D controls
    /// were drawn. <see cref="CaptureFinishedFrame"/> runs once per frame instead.
    /// </summary>
    static void FinishBackbufferForPresent(DXRenderTarget backbuffer)
    {
        backbuffer.TransitionTo(GraphicsCommandList, ResourceStates.Present);
    }

    /// <summary>
    /// Copies the finished frame out for screenshots and recording. Called from
    /// <see cref="AfterRender"/> right before the command list closes, which is
    /// the only point where the backbuffer is guaranteed to hold the complete
    /// frame including the Overlay pass, and the only point that is reached
    /// exactly once per frame — recording pacing must not be sampled twice, or the
    /// frame that wins the time slice becomes a coin flip between the pre-Overlay
    /// and post-Overlay contents.
    /// The backbuffer is in Present by then, and Present -> CopySource -> Present
    /// costs one extra barrier pair only while a capture is actually running.
    /// </summary>
    static void CaptureFinishedFrame()
    {
        bool singleShot = BaseApp.CaptureAppTcs != null;
        bool record = DXCaptureRing.WantsFrame(out long recordFrameIndex);

        if (!singleShot && !record) return;

        var backbuffer = BackbufferRTs[FrameIndex];
        if (backbuffer == null) return;

        CopyBackBufferForCapture(backbuffer.CurrentState, singleShot, record, recordFrameIndex);
        backbuffer.CurrentState = ResourceStates.Present;
    }

    /// <summary>
    /// Called inside the FinalBlit pass: transitions the source offscreen RT to
    /// PixelShaderResource and samples it to the screen with a fullscreen
    /// triangle. The backbuffer is already bound as the current RT by
    /// BeginPass(FinalBlit).
    /// If the source size differs from the backbuffer
    /// (fractional-resolution Post output), linear upsampling is selected
    /// automatically.
    /// If the source is Rgba16Float (HDR chain), the tonemap variant is selected
    /// automatically (1-4 Step A).
    /// 2-1 Step B: when bloomTex is non-null and ready, switch to the
    /// tonemap+bloom variant (bloom added in pre-ACES linear space). Bloom is
    /// valid only in the HDR chain (tonemap); otherwise this falls back cleanly
    /// to existing variants.
    /// 2-1 Step C: when fxaa=true (source is the LDR PostColor from the post
    /// uber output, with luma in alpha), switch to the FXAA variant for screen
    /// presentation. Texel size is uploaded every frame, so resize needs no
    /// rebuild. This path is mutually exclusive with tonemap/bloom because the
    /// composition already finished in Post.
    /// 2-2 Step B: when aoTex is non-null and ready, switch to the AO variant
    /// (apply AO occlusion before adding bloom in pre-ACES linear space). Like
    /// bloom, this is valid only in the HDR chain; otherwise it falls back
    /// cleanly.
    /// 2-3 contract rule 12: when sceneTex is non-null and ready (TAA resolve
    /// output), use it as the scene source instead. Variant selection still
    /// follows srcRT's description. That storage texture matches SceneColor in
    /// size and rgba16float format, and TaaEffect already bypasses publication on
    /// size mismatch. srcRT is still transitioned to sample state idempotently to
    /// keep state tracking consistent.
    /// </summary>
    internal static void BlitToBackbuffer(RenderTarget src, DXTexture bloomTex = null, bool fxaa = false, DXTexture aoTex = null,
        DXTexture sceneTex = null, RenderTarget outlineMask = null, float outlineWidth = 0f)
    {
        var srcRT = (DXRenderTarget)src;
        srcRT.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);

        if (fxaa)
        {
            BlitPipeline.DrawFxaa(srcRT.GpuSrvHandle, 1f / srcRT.Width, 1f / srcRT.Height);
        }
        else
        {
            bool linear = srcRT.Width != (uint)Display.Viewport.Width || srcRT.Height != (uint)Display.Viewport.Height;
            bool tonemap = srcRT.Desc.ColorFormat == RtFormat.Rgba16Float;
            bool bloom = tonemap && bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
            if (bloom)
                bloomTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource); // Post-dispatch state is already this one; transition is idempotent
            bool ao = tonemap && aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
            if (ao)
                aoTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
            var sceneHandle = srcRT.GpuSrvHandle;
            if (sceneTex != null && System.Threading.Volatile.Read(ref sceneTex.Ready))
            {
                sceneTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
                sceneHandle = sceneTex.GpuDescriptorHandle;
            }
            BlitPipeline.Draw(sceneHandle, linear, tonemap, bloom ? bloomTex.GpuDescriptorHandle : default, bloom,
                ao ? aoTex.GpuDescriptorHandle : default, ao);
        }

        if (outlineMask is DXRenderTarget outlineMaskRt && outlineWidth > 0f)
        {
            // Color is already carried per pixel by the mask (multiple colors in
            // one frame), so composition does not need a per-frame color
            // parameter.
            outlineMaskRt.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
            BlitPipeline.DrawOutlineComposite(outlineMaskRt.GpuSrvHandle,
                1f / MathF.Max(outlineMaskRt.Width, 1u),
                1f / MathF.Max(outlineMaskRt.Height, 1u),
                outlineWidth);
        }
    }

    /// <summary>
    /// 2-1 Step C: Post-pass content (the body of FrameSchedule.RenderPost):
    /// uber composition. Source SceneColor is tonemapped (+ bloom) into the
    /// currently bound LDR PostColor, with luma baked into alpha so FXAA does
    /// not need to recompute it.
    /// Input sampling-state transitions are centralized here through the
    /// platform-bound API, matching the existing BlitToBackbuffer pattern.
    /// AfterScene already restores PixelShaderResource after dispatch, so the
    /// extra transitions here are idempotent.
    /// 2-2 Step B: when aoTex is non-null and ready, switch to the uber AO
    /// variant (same composition formula as BlitToBackbuffer).
    /// 2-3 contract rule 12: sceneTex follows the same semantics as in
    /// BlitToBackbuffer (TAA resolve output used as the scene source).
    /// </summary>
    internal static void RenderPostUber(RenderTarget sceneColor, DXTexture bloomTex = null, DXTexture aoTex = null,
        DXTexture sceneTex = null)
    {
        var srcRT = (DXRenderTarget)sceneColor;
        srcRT.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);

        bool bloom = bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
        if (bloom)
            bloomTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
        bool ao = aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
        if (ao)
            aoTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
        var sceneHandle = srcRT.GpuSrvHandle;
        if (sceneTex != null && System.Threading.Volatile.Read(ref sceneTex.Ready))
        {
            sceneTex.TransitionTo(GraphicsCommandList, ResourceStates.PixelShaderResource);
            sceneHandle = sceneTex.GpuDescriptorHandle;
        }
        BlitPipeline.DrawUber(sceneHandle, bloom ? bloomTex.GpuDescriptorHandle : default, bloom,
            ao ? aoTex.GpuDescriptorHandle : default, ao);
    }

    /// <summary>
    /// Per-frame finalization: Close + ExecuteCommandLists + Present + frame
    /// switch + Capture completion.
    /// No longer includes Clear / resolve / RT barriers because they were moved
    /// into EndPass.
    /// </summary>
    internal static void AfterRender()
    {
        CheckResult();

        // Last chance to read the frame out: the backbuffer now holds every pass
        // including Overlay, and the command list is still open.
        CaptureFinishedFrame();

        var result = GraphicsCommandList->Close();
        CheckResult(result);

        // Submit the command list
        const int CommandListsCount = 1;
        var ppCommandLists = stackalloc ID3D12CommandList*[CommandListsCount]
        {
            (ID3D12CommandList*)GraphicsCommandList,
        };
        CommandQueue->ExecuteCommandLists(CommandListsCount, ppCommandLists);

        CheckResult(D3dDevice->GetDeviceRemovedReason());

        CheckResult(SwapChainNative->Present(SyncInterval: 1, Flags: 0));

        // Ring fence: Signal + switch frame + wait when needed
        // (this is the only place that touches the ring fence)
        MoveToNextFrame();

        // Complete the CaptureApp GPU readback; by this point the GPU has
        // already executed all commands for this frame
        CompleteCapture();

        // Hand every recording readback whose fence has already passed to the
        // encoder. Never blocks here: unfinished slots are picked up next frame.
        DXCaptureRing.Tick();

        CheckResult();
    }

    internal static void CheckResult(int result)
    {
        var ex2 = Marshal.GetExceptionForHR(result);

        if (ex2 is not null)
        {
            CheckResult();
        }
    }

    internal static void CheckResult()
    {
        var result0 = D3dDevice->GetDeviceRemovedReason();
        var ex0 = Marshal.GetExceptionForHR(result0);
        if (ex0 is not null)
        {
            throw ex0;
        }
    }

}
