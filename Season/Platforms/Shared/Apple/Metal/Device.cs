// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using MetalKit;
using Season.Basic;
using Season.MSDF;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal bootstrap: IMTLDevice binding, MTKView association, frame-context ring creation, and upper-layer manager instantiation.
/// It corresponds one to one with the DX12 and Vulkan Device implementations.
/// Vulkan concepts such as SwapChain, RenderPass, and Framebuffer are managed internally by MTKView on Metal,
/// so this class no longer implements direct equivalents for those steps.
/// CreateSwapChain only creates the queue and uploader.
///
/// Nine-step bootstrap chain:
///   Init(view) → CreateSwapChain → CreateDescriptorHeapsAndViews → Pipeline.Init →
///   BaseApp.InitLights → MTLSprite2D.Init → CreateGraphicsCommandLists →
///   Graphics.Instance = new Apple.Graphics() → BaseApp.Create()
///
/// ── Metal-specific rule index, stabilized across 1-1 steps 0 through 3.
/// See shared RenderPass.cs and the IGraphics summary for the cross-platform contract:
/// 1. A pass is an encoder.
///    The pass state machine lives in the C# Device layer.
///    BeginPass creates a new RenderCommandEncoder and switches GraphicsEncoder to it,
///    so every draw path is routed through that encoder automatically.
///    encoder.Label is set to the pass name for Xcode GPU capture markers.
///    Bindings are naturally cleared at each pass boundary,
///    so baseline binding replay must run for every pass,
///    including viewport and scissor sized to the target plus fallback TextDrawParams at VS buffer 7 and FS buffer 3.
///    The first draw inside a pass must rebind the pipeline and resources.
/// 2. Metal is a zero-explicit-barrier platform.
///    The driver tracks hazards automatically and attachment-to-sampling transitions complete implicitly.
///    That naturally satisfies the shared-layer rule that no barriers may appear inside a pass.
///    In contrast, Vulkan relies on RenderPass baking plus deferred queues, while DX uses state tracking.
/// 3. PSO and pass attachment sets are structurally identical.
///    All existing PSOs are baked for BGRA8Unorm color plus Depth32Float depth.
///    BackbufferCompatible offscreen render targets are forced to include matching Depth32Float,
///    so every color pass, whether backbuffer or offscreen, has the same attachment structure.
///    Scene PSOs therefore need no variants, and BlitPipeline is baked with both attachments as well.
///    Depth-only passes, such as D32 shadow maps, require the dedicated 1-5 PSO,
///    while Rgba16Float render targets for 1-4 HDR need new PSO variants.
/// 4. Coordinate direction:
///    Metal framebuffers and textures both use downward Y.
///    The FinalBlit point variant keeps exact identity mapping through texture.read(uint2(pos.xy))
///    with no compensation, equivalent to VK texelFetch and WebGPU textureLoad.
///    The linear variant, selected automatically when source size differs from the backbuffer,
///    flips NDC upward Y into downward texture V in the vertex shader using the same formula as WebGPU.
/// 5. Resource lifetime:
///    command buffers keep retained references by default, so in-flight frames may still reference old textures.
///    Render-target rebuild and Dispose can therefore release immediately, with no Vulkan-style timeline-delayed release queue.
///    MatchBackbufferSize render targets are rebuilt lazily by EnsureSize during BeginPass resolution,
///    matching the WebGPU pattern while keeping the wrapper object externally stable.
///    Fixed-size render targets, such as the shadow map, do not resize.
/// 6. Frame-level and pass-level responsibilities are separated.
///    BeforeRender handles in-flight throttling, backbuffer RPD acquisition, frame skipping when no drawable is available,
///    and command-buffer allocation. Completion registration occurs immediately before Commit.
///    Then BeginPass and EndPass repeat for N passes, followed by AfterRender,
///    which performs a defensive EndEncoding fallback, CaptureApp blit, Present, and Commit.
///    One command buffer maps to one Commit, while multiple passes are represented only by multiple encoder segments.
///    The Msaa4x tier is an offscreen-only path, exactly like the HDR chain: the three scene pass ids
///    (Scene / SceneOpaque / SceneTransparent) render into a backend-private multisample color+depth pair and the
///    color attachment resolves into the single-sampled SceneColor, so the CaptureApp convention (read the 1x
///    backbuffer) is unchanged.
/// 7. Render-quality 1-4 HDR chain, with shared contracts documented in RenderQuality summary and Metal-specific details here:
///    1) HdrSceneColor is finalized by AppDelegate from RenderQuality tiers before Pipeline.Init, where PSOs are baked,
///       and must never change afterward.
///       false means the step-2 baseline through the BackbufferCompatible path, which acts as a one-switch fallback.
///    2) Format derivation happens at a single point:
///       the main PSO bake is driven by SceneColorFormat.
///       Under rule 3, the HDR PSO variant means rebaking the whole main PSO set for RGBA16Float.
///       HdrSceneColor already includes the UseOffscreenSceneColor condition,
///       Scene pass always renders offscreen, and the main PSO never renders directly to the backbuffer.
///       FinalBlit selects tonemap variants automatically from the source RT format through BlitPipeline.
///    3) HDR_CHAIN is injected at compile time:
///       the main shader source is prefixed with #define HDR_CHAIN when HdrSceneColor is enabled.
///       Runtime stays branch-free.
///       MTLShaderCompiler caches by full source string, so injection automatically creates a new cache key.
///       Changing the quality tier requires restart.
///    4) Clear-color linearization:
///       BeginPass applies pow(2.2) to Rgba16Float targets through LinearizeClearColor,
///       which is the inverse of the pow(1/2.2) on the blit side,
///       keeping background appearance consistent with the LDR baseline.
///    5) Exposure is delivered through two paths at step B:
///       FinalBlit pushes it through SetFragmentBytes at buffer 0 on every Draw,
///       and the main pipeline uses SceneLightParams.Params0.Y so text can remain inverse-ACES exposure immune.
///       The only valid injection point in the main pipeline is MTLPrimitiveGroup.SetLighting.
///       Every path that writes the lighting UBO, including Update, must go through it.
///       Writing with bare WriteStruct would leave params0.y at zero in the shader, matching the shared four-backend rule.
/// </summary>
internal static class Device
{
    // ===== Core API =====
    internal static IMTLDevice MtlDevice = null!;

    internal static MTKView View = null!;

    internal static CommandQueue GraphicsQueue = null!;

    /// <summary>
    /// Contract 1-9: Metal's own accounting of the bytes this process holds on the device, plus the ceiling when the
    /// platform publishes one. <c>CurrentAllocatedSize</c> exists on both iOS and macOS and includes everything the driver
    /// allocated on our behalf, which is the closest thing Metal has to a VRAM counter. The ceiling is macOS-only
    /// (<c>RecommendedMaxWorkingSetSize</c>): iOS has no per-process GPU budget, so it stays 0 there and the caller reports
    /// usage without a total rather than inventing one.
    /// Never throws; returns false when the device is not up yet.
    /// </summary>
    internal static bool TryQueryGpuMemory(out long usedBytes, out long totalBytes)
    {
        usedBytes = 0;
        totalBytes = 0;

        var device = MtlDevice;
        if (device == null) return false;

        try
        {
            usedBytes = (long)device.CurrentAllocatedSize;
        }
        catch
        {
            // A diagnostic readout must never break the frame that asked for it.
            return false;
        }

#if MACCATALYST
        try
        {
            totalBytes = (long)device.RecommendedMaxWorkingSetSize;
        }
        catch
        {
            totalBytes = 0;
        }
#endif

        return true;
    }

    // ===== Shared rendering parameters, aligned with DX and VK =====
    internal static int frameCount = 3;

    internal static int FrameIndex;

    internal static Vector4 BackgroundColor = new(1f, 1f, 1f, 1f);

    internal static MTLPixelFormat BackBufferFormat = MTLPixelFormat.BGRA8Unorm;

    internal static MTLPixelFormat DepthBufferFormat = MTLPixelFormat.Depth32Float;

    // ── Render-quality 1-4 HDR chain, with private details described in rule 7 above ──

    /// <summary>Render-quality 1-4 tier setting finalized by AppDelegate before Pipeline.Init as UseOffscreenSceneColor && RenderQuality.HdrSceneColor. It must not change afterward.</summary>
    internal static bool HdrSceneColor;

    /// <summary>The actual color format used by the Scene pass render target. Main PSO baking is driven from this value and must not bypass it with hard-coded formats.</summary>
    internal static MTLPixelFormat SceneColorFormat => HdrSceneColor ? MTLPixelFormat.RGBA16Float : BackBufferFormat;

    /// <summary>HDR-chain exposure multiplier. This is the local read-only forwarding of shared RenderQuality.HdrExposure for step-B delivery, where 1.0 means neutral exposure.</summary>
    internal static float HdrExposure => RenderQuality.Current.HdrExposure;

    /// <summary>Sample count of the scene passes in the Msaa4x tier (1 = off), finalized by AppDelegate before
    /// Pipeline.Init because every main PSO bakes it through RasterSampleCount, exactly like Display.MsaaSampleCount
    /// on D3D12 and VkDevice Display on Vulkan. Offscreen targets handed out to the shared layer stay single-sampled:
    /// the multisampled attachments are backend-private.</summary>
    internal static uint MsaaSampleCount = 1;

    /// <summary>Backend-private multisample color+depth pair for the scene passes, created lazily and rebuilt when
    /// the SceneColor target changes size or format.</summary>
    internal static readonly MTLMsaaTarget MsaaSceneTarget = new();

    /// <summary>
    /// Linearizes the HDR-chain clear color by applying pow(2.2) to BackgroundColor in display space,
    /// using it as the linear background color for the HDR scene before it enters the FinalBlit tonemap chain.
    /// Alpha stays unchanged.
    /// </summary>
    internal static Vector4 LinearizeClearColor(in Vector4 c) => new(
        MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);

    // ===== Upper-layer resources, uploads, and shared textures =====
    internal static ResourceManager ResourceManager = null!;

    internal static TextureUploadBatch TextureUploadBatch = null!;

    internal static Display Display = null!;

    internal static FrameContext[] FrameContexts = null!;

    internal static Dictionary<string, Texture> DictionaryTexture = new();

    internal static Texture White = null!;

    /// <summary>Main command buffer for the current frame, assigned after BeforeRender. Equivalent to DX and VK Device.GraphicsCommandList.</summary>
    internal static IMTLCommandBuffer GraphicsCommandBuffer = null!;

    /// <summary>Main RenderCommandEncoder for the current frame, used inside render passes. All bind and draw calls route through this object.</summary>
    internal static IMTLRenderCommandEncoder GraphicsEncoder = null!;

    /// <summary>Semaphore for frame-concurrency throttling. BeforeRender waits on it, and CommandBuffer.AddCompletedHandler releases it.</summary>
    static SemaphoreSlim _inFlight = null!;
    static bool _frameOpen;

    /// <summary>Backbuffer render-pass descriptor for the current frame, acquired in BeforeRender and used by BeginPass after configuring load and store actions from PassDesc.</summary>
    static MTLRenderPassDescriptor? _backbufferRpd;

    /// <summary>Pass debug labels used for encoder.Label and Xcode GPU capture markers in step 0. The index matches RenderPassId.</summary>
    static readonly string[] _passLabels = { "Shadow", "Scene", "Post", "OutlineMask", "FinalBlit", "Overlay", "SceneOpaque", "FogComposite", "SceneTransparent", "WeatherComposite", "SceneDecal", "SceneWater", "SsrComposite", "SceneWaterfall", "ShadowFar", "ShadowLocal" };

    /// <summary>Phase 4 current pass id, written by BeginPass and reset by EndPass.
    /// Pipeline.SetPipeline uses it to route OutlineMask passes to the mask PSO,
    /// where the mask RT is always BGRA8 and therefore differs from HDR Scene PSO attachment formats and must be rebound.
    /// Overlay passes are routed to the overlay family, which renders directly to the backbuffer
    /// and therefore also requires dedicated PSOs with depth fully disabled.</summary>
    internal static Season.Rendering.RenderPassId ActivePassId;

    internal static MTLRenderTarget? ActiveColorTarget { get; private set; }
    internal static IMTLTexture? ActiveColorTexture { get; private set; }

    /// <summary>A0.1: the explicit depth RT actually bound as the depth attachment in this Pass, null when there is no explicit depth, with the same semantics as VK/DX Device.ActiveDepthTarget.
    /// Side-path pipelines use this to confirm that the depth semantics seen during preparation have not changed.</summary>
    internal static MTLRenderTarget? ActiveDepthTarget { get; private set; }

    /// <summary>A0.1: the texture actually bound to DepthAttachment in this Pass, implicit depth included.
    /// Metal bakes the attachment formats into the PSO, so a side-path pipeline that records its own draws inside a Pass can only get DepthAttachmentPixelFormat from here:
    /// the absence of explicit depth does not mean there is no depth (the RT may carry one of its own), and a NoImplicitDepth target cannot even enable depth testing.</summary>
    internal static IMTLTexture? ActiveDepthTexture { get; private set; }

    /// <summary>A0.1: the velocity texture actually bound as color attachment 1 in this Pass, null in the single-target shape.
    /// Likewise for baking the PSO for side-path pipelines: both the attachment count and slot 1's format must match the actual Pass.</summary>
    internal static IMTLTexture? ActiveVelocityTexture { get; private set; }

    /// <summary>A0.2: the normal texture actually bound as color attachment 2 in this Pass, which can only be non-null when velocity is also an attachment.
    /// Side-path pipelines use this to bake slot 2's format into the PSO.</summary>
    internal static IMTLTexture? ActiveNormalTexture { get; private set; }

    // ── GPU readback for CaptureApp ──
    static IMTLBuffer? _captureStagingBuffer;
    static uint _captureWidth;
    static uint _captureHeight;
    static uint _captureBytesPerRow;
    static bool _capturePending;

    /// <summary>Initializes IMTLDevice and associates it with MTKView.</summary>
    internal static void Init(MTKView view)
    {
        View = view;
        MtlDevice = view.Device ?? MTLDevice.SystemDefault!;
        view.Device = MtlDevice;
        view.ColorPixelFormat = BackBufferFormat;
        view.DepthStencilPixelFormat = DepthBufferFormat;
        view.SampleCount = 1;
        view.PreferredFramesPerSecond = 60;
        // On A13 devices running iOS 17.2, the combination of FramebufferOnly = true
        // and StoreAction.Store can cause rendered frames to be silently dropped by the GPU driver.
        // The simulator is unaffected because it uses macOS Metal.
        // Set this to false to stay compatible with all Apple GPU families.
        view.FramebufferOnly = false;
        view.ClearColor = new MTLClearColor(BackgroundColor.X, BackgroundColor.Y, BackgroundColor.Z, BackgroundColor.W);
        view.ClearDepth = 1.0;
        view.Paused = false;
        view.EnableSetNeedsDisplay = false;
    }

    /// <summary>Creates the IMTLCommandQueue and upper-layer resource manager. Equivalent to DX and VK CreateSwapChain.</summary>
    internal static void CreateSwapChain(int width, int height)
    {
        GraphicsQueue = new CommandQueue(MtlDevice);
        ResourceManager = new ResourceManager(MtlDevice);
        TextureUploadBatch = new TextureUploadBatch(MtlDevice, GraphicsQueue);
    }

    /// <summary>Creates Display, meaning viewport and scissor. Equivalent to DX and VK CreateDescriptorHeapsAndViews.</summary>
    internal static void CreateDescriptorHeapsAndViews()
    {
        Display = new Display();
        Display.SetClearColor(BackgroundColor);
        var size = View.DrawableSize;
        int w = (int)size.Width;
        int h = (int)size.Height;
        if (w <= 0) w = (int)View.Bounds.Width;
        if (h <= 0) h = (int)View.Bounds.Height;
        Display.Initialize(w, h);
    }

    /// <summary>Creates per-frame FrameContext objects plus the 1x1 white fallback texture. Equivalent to DX and VK CreateGraphicsCommandLists.</summary>
    internal static void CreateGraphicsCommandLists()
    {
        FrameContexts = new FrameContext[frameCount];
        for (int i = 0; i < frameCount; i++) FrameContexts[i] = new FrameContext();
        FrameIndex = 0;
        _inFlight = new SemaphoreSlim(frameCount, frameCount);

        if (White == null)
        {
            White = new Texture("White", null);
            DictionaryTexture["White"] = White;
        }

        // Upload White synchronously.
        TextureUploadBatch.Execute();

        // Render-quality 1-7:
        // prebuild the 1x1 black fallback cube here instead of creating it lazily on the first BeginPass.
        // Its upload uses an independent command buffer plus WaitUntilCompleted.
        // If this were inserted during BeginPass, the current frame command buffer would already have
        // an open render encoder and would not yet be committed,
        // so forcing a same-queue wait there would introduce an avoidable queueing hazard.
        _ = MTLTextureCube.DummyBlack;
    }

    // ── CaptureApp GPU readback implementation ──

    /// <summary>
    /// Called after EndEncoding and before PresentDrawable.
    /// It creates an MTLBlitCommandEncoder to copy the drawable texture into a staging buffer.
    /// </summary>
    internal static void CaptureBackBuffer(FrameContext frame)
    {
        var drawable = View.CurrentDrawable;
        if (drawable == null) return;

        var tex = drawable.Texture;
        _captureWidth = (uint)tex.Width;
        _captureHeight = (uint)tex.Height;

        // Create or reuse the staging buffer, where StorageModeShared allows CPU readback.
        _captureBytesPerRow = checked((_captureWidth * 4 + 255) & ~255u);
        nuint totalBytes = checked((nuint)_captureBytesPerRow * _captureHeight);
        if (_captureStagingBuffer == null || _captureStagingBuffer.Length < totalBytes)
        {
            var replacement = MtlDevice.CreateBuffer(totalBytes, MTLResourceOptions.StorageModeShared)
                ?? throw new InvalidOperationException("Cannot allocate Metal readback buffer.");
            _captureStagingBuffer?.Dispose();
            _captureStagingBuffer = replacement;
        }

        // Create a BlitCommandEncoder to execute the texture-to-buffer copy.
        // The native binding rejects a null descriptor,
        // so pass a default MTLBlitPassDescriptor, matching every other blit-encoder call site in the Metal backend.
        using var blitEncoder = frame.CommandBuffer!.CreateBlitCommandEncoder(new MTLBlitPassDescriptor())
            ?? throw new InvalidOperationException("Cannot create Metal readback encoder.");
        blitEncoder.CopyFromTexture(
            tex, 0, 0,
            new MTLOrigin(0, 0, 0),
            new MTLSize((nint)_captureWidth, (nint)_captureHeight, 1),
            _captureStagingBuffer, 0,
            _captureBytesPerRow,
            totalBytes);
        blitEncoder.EndEncoding();

        _capturePending = true;
    }

    /// <summary>
    /// Called after Commit.
    /// It waits for the GPU, reads RGBA data from the staging buffer, and notifies the CaptureApp caller.
    /// </summary>
    internal static void CompleteCapture()
    {
        if (!_capturePending || _captureStagingBuffer == null) return;
        _capturePending = false;

        try
        {
            int w = (int)_captureWidth;
            int h = (int)_captureHeight;
            byte[] pixels = new byte[w * h * 4];

            // Metal backbuffers use BGRA8Unorm, so readback data is laid out as B, G, R, A.
            // NativeImageData expects RGBA8, so swap B and R here.
            unsafe
            {
                byte* pSrc = (byte*)_captureStagingBuffer.Contents;
                fixed (byte* pDst = pixels)
                {
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int dst = (y * w + x) * 4;
                        nuint src = (nuint)y * _captureBytesPerRow + (nuint)x * 4;
                        pDst[dst + 0] = pSrc[src + 2];
                        pDst[dst + 1] = pSrc[src + 1];
                        pDst[dst + 2] = pSrc[src + 0];
                        pDst[dst + 3] = pSrc[src + 3];
                    }
                }
            }

            var captureAppImage = new NativeImageData(w, h, pixels);

            BaseApp.CaptureAppTcs?.TrySetResult(captureAppImage);
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CaptureApp {ex}");

            BaseApp.CaptureAppTcs?.TrySetResult(null);
        }

        BaseApp.CaptureAppTcs = null;
    }

    /// <summary>Begins a frame, covering the frame-level responsibilities of render-quality 1-1 step 1:
    /// throttle in-flight frames, acquire the current view RPD, and allocate the command buffer.
    /// Pass-level responsibilities such as clear setup, encoder creation, and baseline binding replay now live in BeginPass.</summary>
    /// <returns>true when the frame can be rendered, or false when no drawable is available and the frame should be skipped.</returns>
    internal static bool BeforeRender()
    {
        // 1) Throttle frame concurrency.
        _inFlight.Wait();
        try
        {
            WaitForFrameSlot();
            var frame = FrameContexts[FrameIndex];
            var rpd = View.CurrentRenderPassDescriptor;
            if (rpd == null)
            {
                _inFlight.Release();
                return false;
            }
            _backbufferRpd = rpd;
            var cmd = GraphicsQueue.CreateCommandBuffer();
            frame.CommandBuffer = cmd;
            GraphicsCommandBuffer = cmd;
            _frameOpen = true;
            return true;
        }
        catch
        {
            _backbufferRpd = null;
            _inFlight.Release();
            throw;
        }
    }

    /// <summary>
    /// Opens a single render pass, covering the pass-level responsibilities of render-quality 1-1 steps 1 through 3.
    /// Target resolution starts from ColorTarget ?? DepthTarget.
    /// For offscreen RPDs, both non-null means the 2-2 dual-target Scene pass,
    /// where the color target contributes the RPD and the depth plane is rebound explicitly
    /// from SceneDepth.DepthTexture.
    /// Both null means the backbuffer RPD is used.
    /// Load and store actions are configured from PassDesc,
    /// then a new RenderCommandEncoder is opened, where Metal treats the pass as the encoder itself
    /// and bindings are naturally cleared.
    /// Finally baseline bindings are replayed, including viewport and scissor sized to the target.
    /// Depth-only targets such as shadow maps attach only DepthAttachment and have no color configuration.
    /// The 2-3 triple-target case, SceneColor plus SceneVelocity plus explicit SceneDepth,
    /// binds the velocity RT at ColorAttachments[1].
    /// </summary>
    internal static void BeginPass(in Season.Rendering.PassDesc desc)
    {
        if (GraphicsEncoder != null)
            throw new InvalidOperationException("Cannot nest Metal render Passes.");
        if (desc.Id is (Season.Rendering.RenderPassId.FogComposite or Season.Rendering.RenderPassId.WeatherComposite)
            && (desc.ColorTarget is not MTLRenderTarget || desc.DepthTarget != null || desc.VelocityTarget != null))
            throw new ArgumentException("Fog composite must use a separate color target, and cannot attach depth or velocity.", nameof(desc));
        // P0 SSR composite differs from fog composite in only one respect: the color target is SceneColor itself (additive increment, no separate target),
        // but it likewise cannot attach depth, velocity, or normal —— these resources are all still sampling sources at this point.
        if (desc.Id == Season.Rendering.RenderPassId.SsrComposite
            && (desc.ColorTarget is not MTLRenderTarget || desc.DepthTarget != null
                || desc.VelocityTarget != null || desc.NormalTarget != null))
            throw new ArgumentException("SSR composite must attach only the scene color, and cannot attach depth, velocity, or normal.", nameof(desc));
        ActiveColorTarget = null;
        ActiveColorTexture = null;
        // These fields record what "actually became an attachment", not what PassDesc requested; the values are only set as each binding is done below.
        ActiveDepthTarget = null;
        ActiveDepthTexture = null;
        ActiveVelocityTexture = null;
        ActiveNormalTexture = null;
        // Phase 4: record the current pass id so Pipeline.SetPipeline can route OutlineMask passes to the mask PSO.
        ActivePassId = desc.Id;

        // 2-2 dual-target path:
        // when the color target is non-null, DepthTarget is the explicit SceneDepth in depth-only form,
        // also used for shader reads.
        // Rebuild it lazily for synchronized size changes before rebinding the depth plane.
        MTLRenderTarget? explicitDepth = null;
        if (desc.ColorTarget != null && desc.DepthTarget is MTLRenderTarget dt)
        {
            if (dt.Desc.MatchBackbufferSize)
                dt.EnsureSize(Display.Width, Display.Height);
            explicitDepth = dt;
        }

        // 2-3 triple-target path:
        // velocity RT in Rg16Float, lazily rebuilt on synchronized size changes.
        MTLRenderTarget? velocityRT = null;
        if (desc.ColorTarget != null && desc.VelocityTarget is MTLRenderTarget vrt)
        {
            if (vrt.Desc.MatchBackbufferSize)
                vrt.EnsureSize(Display.Width, Display.Height);
            velocityRT = vrt;
        }

        // A0.2: SceneNormal normally rides on velocity (RenderQuality.EffectiveSceneNormals), at ColorAttachments[2].
        // D1 exception: the decal Pass has no velocity attachment, so SceneNormal directly takes color attachment 1 (see the binding branch below).
        MTLRenderTarget? normalRT = null;
        if ((velocityRT != null || desc.Id == Season.Rendering.RenderPassId.SceneDecal)
            && desc.NormalTarget is MTLRenderTarget nrt)
        {
            if (nrt.Desc.MatchBackbufferSize)
                nrt.EnsureSize(Display.Width, Display.Height);
            normalRT = nrt;
        }

        MTLRenderPassDescriptor rpd;
        double vpWidth, vpHeight;
        bool depthOnly = false;
        // Non-null while this pass draws into the Msaa4x multisample pair and resolves into the offscreen color target.
        MTLMsaaTarget? msaa = null;
        // Resolve destination of that pair: the single-sampled colour plane of the target this pass was handed.
        // Captured here because the pattern-matched target below is out of scope by the time load/store actions are set.
        IMTLTexture? msaaResolve = null;
        // D1: decals only blend and do not clear, and no Clear is allowed regardless of how PassDesc is written —— what would be cleared is exactly the opaque result just drawn.
        // P2 water surface is the same: it blends onto the already-composited scene color, and clearing would wipe the entire background to a single color.
        // P0 SSR composite is the same: the reflection increment is added additively into SceneColor, and a single clear would wipe out the whole frame's scene.
        bool decalPass = desc.Id is Season.Rendering.RenderPassId.SceneDecal or Season.Rendering.RenderPassId.SceneWater
            or Season.Rendering.RenderPassId.SsrComposite or Season.Rendering.RenderPassId.SceneWaterfall;
        var c = desc.ClearColor;

        if ((desc.ColorTarget ?? desc.DepthTarget) is MTLRenderTarget rt)
        {
            // Offscreen targets:
            // MatchBackbufferSize is rebuilt lazily during resolution, matching the WebGPU pattern.
            // Fixed-size render targets such as the shadow map pass through EnsureSize idempotently.
            if (rt.Desc.MatchBackbufferSize)
                rt.EnsureSize(Display.Width, Display.Height);

            depthOnly = rt.IsDepthOnly;
            // Linearize clear color for HDR targets in Rgba16Float.
            // This is the inverse of the pow(1/2.2) used by FinalBlit tonemap variants,
            // keeping background appearance consistent with the LDR baseline.
            // The LDR path passes through unchanged. See rule 7.4 in the class header.
            if (!depthOnly && rt.Desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float)
                c = LinearizeClearColor(c);
            // Msaa4x tier: the scene passes draw into the backend-private multisample pair and resolve into this
            // target. It is offscreen-only (the init gate requires the offscreen SceneColor chain), single-target
            // (velocity is mutually exclusive with Msaa4x) and depth-explicit-free (no SceneDepth exists).
            if (MsaaSampleCount > 1 && !depthOnly && IsMsaaScenePass(desc.Id)
                && desc.VelocityTarget == null && desc.DepthTarget == null)
            {
                msaa = MsaaSceneTarget;
                msaa.EnsureSize(rt.Width, rt.Height, rt.ColorPixelFormat, Device.DepthBufferFormat, MsaaSampleCount);
                msaaResolve = rt.ColorTexture;
            }

            rpd = new MTLRenderPassDescriptor();
            if (!depthOnly)
                rpd.ColorAttachments[0].Texture = msaa?.ColorTexture ?? rt.ColorTexture;
            // Fog composite uses a separate depthless full-screen pipeline; the transparent stage then attaches the original scene depth.
            // For D1 decals and P2 water surface, depth is a sampling source rather than an attachment: the same resource cannot be both sampled and used as the depth attachment.
            // P0 SSR composite has no depth requirement, and the implicit depth carried by SceneColor must still be left for the transparent stage at this point, so it cannot be attached here.
            rpd.DepthAttachment.Texture = msaa?.DepthTexture ?? (desc.Id is Season.Rendering.RenderPassId.FogComposite
                or Season.Rendering.RenderPassId.WeatherComposite or Season.Rendering.RenderPassId.SceneDecal
                or Season.Rendering.RenderPassId.SceneWater or Season.Rendering.RenderPassId.SsrComposite
                or Season.Rendering.RenderPassId.SceneWaterfall
                ? null : explicitDepth?.DepthTexture ?? rt.DepthTexture);
            // When color is absent, rt itself is that depth map (Shadow), and it is indeed this Pass's depth attachment;
            // otherwise only an explicit depth RT counts, and when the RT's own implicit depth is used there is no RT to record.
            if (rpd.DepthAttachment.Texture != null)
                ActiveDepthTarget = desc.ColorTarget == null ? rt : explicitDepth;

            // 2-3 triple-target velocity attachment at ColorAttachments[1].
            if (velocityRT != null && velocityRT.ColorTexture != null)
            {
                rpd.ColorAttachments[1].Texture = velocityRT.ColorTexture;
                if (normalRT != null && normalRT.ColorTexture != null)
                    rpd.ColorAttachments[2].Texture = normalRT.ColorTexture;
            }
            // D1 decals have no velocity attachment, so SceneNormal directly takes color attachment 1: attachment indices must be contiguous starting from 0,
            // otherwise a PSO writing [[color(1)]] would not match the actual Pass.
            else if (normalRT != null && normalRT.ColorTexture != null)
                rpd.ColorAttachments[1].Texture = normalRT.ColorTexture;

            // Offscreen color and shadow depth will be sampled later by blit, Post, or 1-5,
            // so store behavior is expressed explicitly by PassDesc.
            vpWidth = rt.Width;
            vpHeight = rt.Height;
        }
        else
        {
            rpd = _backbufferRpd ?? throw new Exception("BeginPass called without BeforeRender");
            vpWidth = Display.Viewport.Width;
            vpHeight = Display.Viewport.Height;
        }

        // Clear and load/store actions are expressed explicitly by PassDesc,
        // which is important for tiler GPU bandwidth efficiency.
        if (!depthOnly)
        {
            rpd.ColorAttachments[0].LoadAction = desc.ClearColorEnable && !decalPass ? MTLLoadAction.Clear : MTLLoadAction.Load;
            rpd.ColorAttachments[0].StoreAction = MTLStoreAction.Store;
            rpd.ColorAttachments[0].ClearColor = new MTLClearColor(c.X, c.Y, c.Z, c.W);

            // Msaa4x tier: the multisampled attachment is resolved into the single-sampled SceneColor at pass end
            // (one resolve per scene pass, which is exactly the pass boundary). The resolve destination is the
            // target this pass was handed, so the opaque resolve is what the transparent stage loads back in.
            // The opaque root must keep the multisampled content as well, because SceneTransparent loads it;
            // the two passes that can close the scene (Scene unsegmented and SceneTransparent) discard theirs.
            if (msaa != null && msaaResolve != null)
            {
                rpd.ColorAttachments[0].StoreAction = desc.Id == Season.Rendering.RenderPassId.SceneOpaque
                    ? MTLStoreAction.StoreAndMultisampleResolve
                    : MTLStoreAction.MultisampleResolve;
                rpd.ColorAttachments[0].ResolveTexture = msaaResolve;
            }

            // 2-3 velocity attachment:
            // clearing to (0, 0) means zero velocity, and store makes it available to AfterScene and TAA.
            if (velocityRT != null)
            {
                rpd.ColorAttachments[1].LoadAction = desc.ClearColorEnable ? MTLLoadAction.Clear : MTLLoadAction.Load;
                rpd.ColorAttachments[1].StoreAction = MTLStoreAction.Store;
                rpd.ColorAttachments[1].ClearColor = new MTLClearColor(0, 0, 0, 0);
            }
            if (normalRT != null)
            {
                // The normal attachment is 2 in the dual-target shape, and 1 in the single-target-plus-attachment shape that decals use.
                int normalSlot = velocityRT != null ? 2 : 1;
                rpd.ColorAttachments[normalSlot].LoadAction = desc.ClearColorEnable && !decalPass ? MTLLoadAction.Clear : MTLLoadAction.Load;
                rpd.ColorAttachments[normalSlot].StoreAction = MTLStoreAction.Store;
                rpd.ColorAttachments[normalSlot].ClearColor = new MTLClearColor(0, 0, 0, 0);
            }
        }
        rpd.DepthAttachment.LoadAction = desc.ClearDepthEnable ? MTLLoadAction.Clear : MTLLoadAction.Load;
        rpd.DepthAttachment.StoreAction = desc.StoreDepth ? MTLStoreAction.Store : MTLStoreAction.DontCare;
        rpd.DepthAttachment.ClearDepth = 1.0;

        var frame = FrameContexts[FrameIndex];
        var enc = frame.CommandBuffer!.CreateRenderCommandEncoder(rpd) ?? throw new Exception("CreateRenderCommandEncoder returned null");
        enc.Label = _passLabels[(int)desc.Id];
        frame.Encoder = enc;
        GraphicsEncoder = enc;
        ActiveColorTarget = desc.ColorTarget as MTLRenderTarget;
        ActiveColorTexture = rpd.ColorAttachments[0].Texture;
        // Read back from rpd rather than inferring from PassDesc: fog composite nulls out depth, and the backbuffer path's depth does not come from any RT.
        ActiveDepthTexture = rpd.DepthAttachment.Texture;
        ActiveVelocityTexture = velocityRT != null ? rpd.ColorAttachments[1].Texture : null;
        // When velocity is absent the normal takes attachment 1 instead (D1 decal Pass), so the slot can only be inferred from the actual bindings.
        ActiveNormalTexture = normalRT != null ? rpd.ColorAttachments[velocityRT != null ? 2 : 1].Texture : null;

        // Baseline binding replay:
        // each new pass encoder starts with cleared bindings,
        // so viewport and scissor sized to the target plus fallback TextDrawParams must be replayed every pass.
        // MSL statically declares VS buffer 7 and FS buffer 3,
        // so even non-text draws need valid bindings there.
        // Instanced text rendering overrides them later with its own per-frame buffers.
        enc.SetViewport(new MTLViewport { OriginX = 0, OriginY = 0, Width = vpWidth, Height = vpHeight, ZNear = 0.0, ZFar = 1.0 });
        enc.SetScissorRect(new MTLScissorRect { X = 0, Y = 0, Width = (nuint)vpWidth, Height = (nuint)vpHeight });

        // Composite pipelines fully bind their own resources in Draw and do not access the main material's ambient and lighting fallback resources.
        if (desc.Id is Season.Rendering.RenderPassId.FogComposite or Season.Rendering.RenderPassId.WeatherComposite
            or Season.Rendering.RenderPassId.SsrComposite) return;

        // Immediate-2D compatibility mode (Season.Rendering.Immediate2DMode):
        // Pipeline.Init and every 3D resource bound below were skipped for the session,
        // and the single Overlay pass of the frame renders through the self-contained Draw2DPipeline,
        // so the main-shader fallback bindings must not be replayed here.
        if (Season.Rendering.Immediate2DMode.Enabled)
            return;

        enc.SetVertexBuffer(Pipeline.DefaultTextDrawParamsBuffer, 0, 7);
        enc.SetFragmentBuffer(Pipeline.DefaultTextDrawParamsBuffer, 0, 3);

        // Contract clauses 8(b) and 8(c) of 2-3:
        // the velocity variant statically declares buffer 9 for the previous instance stream
        // and buffer 10 for the previous bone palette.
        // Because of that, draws such as text and sprites that do not go through Pipeline.DrawPrimitive
        // still need valid fallback bindings.
        // Their hasPrev* sentinels remain zero, so shaders do not consume them.
        // These bindings exist only to satisfy Metal API Validation.
        if (RenderQuality.Current.MotionVectors)
        {
            enc.SetVertexBuffer(Pipeline.IdentityInstanceBuffer, 0, 9);
            var idBones = MTLPrimitiveGroup.IdentityBoneBuffers;
            enc.SetVertexBuffer(idBones != null ? idBones[FrameIndex] : Pipeline.IdentityInstanceBuffer, 0, 10);
        }

        // Render-quality 1-5:
        // baseline binding for the shadow atlas.
        // When MSL enables SHADOW_ENABLED, the fragment shader statically declares texture(5) and sampler(1),
        // so every non-depth-only pass needs valid bindings there.
        // Depth-only passes, where the atlas is currently being written, must skip this
        // because shadow variants have no fragment shader
        // and Metal forbids rebinding a texture as a sampling source while it is being written as an attachment.
        if (!depthOnly && RenderQuality.Current.ShadowsEnabled
            && Season.Rendering.FrameSchedule.ShadowMap is MTLRenderTarget shadowRt && shadowRt.DepthTexture != null)
        {
            enc.SetFragmentTexture(shadowRt.DepthTexture, 5);
            enc.SetFragmentSamplerState(Pipeline.ShadowSampler, 1);
        }

        // Render-quality 1-7:
        // baseline binding for the environment radiance cube.
        // The MSL fragment shader statically declares texture(6),
        // and the specular term samples it unconditionally before multiplying by step(0.5, envParams.w),
        // so a valid binding is always required.
        // Like the shadow atlas path, this is done here at pass level rather than per draw.
        // PBR textures have two binding points, Pipeline.DrawPrimitive and MTLPrimitiveGroup.DrawPrimitive,
        // but text, sprites, and other paths bypass both and still require a valid binding.
        // One pass-level bind covers all paths more efficiently.
        // When no environment texture is available, Bound falls back to the 1x1 black texture.
        // Depth-only passes skip this because shadow variants have no fragment shader.
        // The sampler reuses sampler(0) from Pipeline.SetPipelineState, which is linear plus ClampToEdge, without consuming another slot.
        if (!depthOnly)
            enc.SetFragmentTexture(MTLTextureCube.Bound.Image, MTLTextureCube.EnvCubeTextureSlot);

        // Contract clause 10 of 2-4:
        // baseline binding for the DDGI irradiance-probe atlas.
        // MSL fragment shaders always declare texture(7), following the same pattern as envCube.
        // The atlas for the current frame is resolved centrally by MTLPrimitiveGroup.SetLighting.
        // When it is null because the feature is disabled or not ready, it falls back to 1x1 White.
        // Real sampling remains gated by DDGI_ENABLED plus giParams2.z and giParams1.w.
        // Depth-only passes skip this because they have no fragment shader.
        if (!depthOnly)
            enc.SetFragmentTexture((MTLPrimitiveGroup.DdgiAtlasActive ?? White).Image, 7);

        // Step 3 of 2-4:
        // baseline binding for the DDGI depth-moment atlas.
        // MSL fragment shaders always declare texture(8), following the same pattern.
        // Null falls back to 1x1 White, and real Chebyshev sampling is gated at runtime by giParams2.y.
        // Depth-only passes skip it.
        if (!depthOnly)
            enc.SetFragmentTexture((MTLPrimitiveGroup.DdgiDepthActive ?? White).Image, 8);

        // Step C and E of 2-5:
        // baseline binding for cloud noise at texture(9) with wrap sampling
        // and the AP 3D LUT at texture(10).
        // MSL fragment shaders always declare both textures plus sampler(2) using Repeat.
        // Null falls back to 1x1 White or MTLTexture3D.DummyBlack.
        // Real sampling is gated at runtime by cloudParams0.w for layer count and apParams0.x for far distance in kilometers.
        // White is a dangerous fallback because fully white noise would drive density to maximum,
        // so the layer-count gate must remain strict.
        // The AP dummy texture is an additive identity and gating only avoids unnecessary sampling.
        // Depth-only passes skip this because shadow variants have no fragment shader.
        if (!depthOnly)
        {
            enc.SetFragmentTexture((MTLPrimitiveGroup.CloudNoiseActive ?? White).Image, 9);
            enc.SetFragmentTexture((MTLPrimitiveGroup.AerialLutActive ?? MTLTexture3D.DummyBlack).Image, 10);
            // 2-7 P0: the procedural sky's Sky-View LUT baseline is bound at texture(13), alongside envCube at texture(6).
            // MSL always declares this texture, so every Pass with a fragment stage must have a valid binding (the text/sprite paths also share the same PS).
            // White is a safe fallback: the write to skyParams0.w is predicated precisely on that LUT existing, while the StaticCube tier takes the else branch to sample envCube.
            enc.SetFragmentTexture((MTLPrimitiveGroup.SkyViewActive ?? White).Image, 13);
            enc.SetFragmentSamplerState(Pipeline.WrapSampler, 2);

            // Work package D: baseline binding of the three fog sampling resources.
            // MSL's fragment_main does not rely on macros and always declares FS buffer(4)/(5) and texture(11)/(12),
            // so every Pass with a fragment shader must have valid bindings, or Metal API Validation will report them missing.
            // What is written here is zero gating plus placeholder resources; the real volumes are only re-pushed per command by Pipeline.BindSceneFog in the transparent stage.
            Pipeline.BindSceneFog(enc);
        }
    }

    /// <summary>
    /// Switches viewport and scissor to a shadow-atlas quadrant for render-quality 1-5,
    /// equivalent to DX RSSetViewports and VK CmdSetViewport.
    /// Metal viewport Y points downward just like D3D, per class-header rule 4,
    /// so no Vulkan-style negative-height flip is needed.
    /// </summary>
    internal static void SetShadowViewport(int x, int y, int size)
    {
        GraphicsEncoder.SetViewport(new MTLViewport { OriginX = x, OriginY = y, Width = size, Height = size, ZNear = 0.0, ZFar = 1.0 });
        GraphicsEncoder.SetScissorRect(new MTLScissorRect { X = (nuint)x, Y = (nuint)y, Width = (nuint)size, Height = (nuint)size });
    }

    /// <summary>Closes the current pass by calling EndEncoding. On Metal, the pass is the encoder itself, and zero-explicit-barrier platforms need no layout finalization.</summary>
    internal static void EndPass()
    {
        var frame = FrameContexts[FrameIndex];
        try { frame.Encoder?.EndEncoding(); }
        finally
        {
            frame.Encoder?.Dispose();
            frame.Encoder = null;
            GraphicsEncoder = null!;
            ActivePassId = default;
            ActiveColorTarget = null;
            ActiveColorTexture = null;
            ActiveDepthTarget = null;
            ActiveDepthTexture = null;
            ActiveVelocityTexture = null;
            ActiveNormalTexture = null;
        }
    }

    /// <summary>The passes that render the 3D scene. All three ids draw the same scene and share the Msaa4x
    /// multisample pair (the segmented route uses SceneOpaque then SceneTransparent, the unsegmented one Scene).</summary>
    internal static bool IsMsaaScenePass(Season.Rendering.RenderPassId id)
        => id is Season.Rendering.RenderPassId.Scene or Season.Rendering.RenderPassId.SceneOpaque
            or Season.Rendering.RenderPassId.SceneTransparent;

    /// <summary>
    /// P2: copies one offscreen color target as-is into another target of the same shape, so water surface refraction can obtain a
    /// samplable background in the frame where "SceneColor is still a color attachment".
    ///
    /// Metal uses a blit encoder for a whole-texture-to-texture copy: no filtering loss, and no extra pipeline needed.
    /// It must be called outside a render Pass —— the same command buffer cannot have the render and blit encoders open at the same time,
    /// and the scheduler calls it right before the water surface Pass's BeginPass.
    /// </summary>
    internal static bool CopyRenderTarget(Season.Rendering.RenderTarget source, Season.Rendering.RenderTarget destination)
    {
        if (GraphicsEncoder != null) return false;
        var frame = FrameContexts[FrameIndex];
        var command = frame.CommandBuffer;
        if (command == null || command.Handle == IntPtr.Zero) return false;
        if (source is not MTLRenderTarget src || destination is not MTLRenderTarget dst) return false;
        if (ReferenceEquals(src, dst) || src.IsDepthOnly || dst.IsDepthOnly) return false;
        if (src.Desc.ColorFormat != dst.Desc.ColorFormat) return false;
        if (src.Width <= 0 || src.Width != dst.Width || src.Height != dst.Height) return false;
        var srcTexture = src.ColorTexture;
        var dstTexture = dst.ColorTexture;
        if (srcTexture == null || dstTexture == null || srcTexture.Handle == IntPtr.Zero || dstTexture.Handle == IntPtr.Zero)
            return false;

        using var blit = command.CreateBlitCommandEncoder(new MTLBlitPassDescriptor());
        if (blit == null) return false;
        blit.CopyFromTexture(srcTexture, 0, 0, new MTLOrigin(0, 0, 0),
            new MTLSize(src.Width, src.Height, 1),
            dstTexture, 0, 0, new MTLOrigin(0, 0, 0));
        blit.EndEncoding();
        return true;
    }

    /// <summary>Ends the frame at frame scope by Present plus Commit and then advancing FrameIndex. Pass closure is already handled by EndPass.</summary>
    internal static void AfterRender()
    {
        var frame = FrameContexts[FrameIndex];

        // Defensive fallback:
        // if the pass did not close normally, force EndEncoding here.
        // In the normal path EndPass has already cleared it.
        if (frame.Encoder != null) EndPass();
        _backbufferRpd = null;

        // GPU readback for CaptureApp:
        // insert the BlitEncoder before PresentDrawable.
        if (BaseApp.CaptureAppTcs != null)
        {
            // A capture failure must not escape into the MTKView Draw callback,
            // because an unhandled exception there crashes the app on the render thread
            // and leaves the CaptureApp await pending forever.
            // Log the failure and complete the pending request with null instead.
            try
            {
                CaptureBackBuffer(frame);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} CaptureBackBuffer {ex}");
                var tcs = BaseApp.CaptureAppTcs;
                BaseApp.CaptureAppTcs = null;
                tcs?.TrySetResult(null);
            }
        }

        var drawable = View.CurrentDrawable;
        if (drawable != null)
            frame.CommandBuffer!.PresentDrawable(drawable);

        var cmdBuffer = frame.CommandBuffer!;
        cmdBuffer.AddCompletedHandler(_ => _inFlight.Release());
        frame.FenceValue = GraphicsQueue.RegisterSignal(cmdBuffer);
        cmdBuffer.Commit();
        _frameOpen = false;

        // For CaptureApp, wait for GPU completion so the staging buffer becomes readable.
        if (_capturePending)
            WaitForCompletion(cmdBuffer);

        // Keep the actual submitted buffer for ring-slot waits and shutdown.
        GraphicsCommandBuffer = null!;

        FrameIndex = (FrameIndex + 1) % frameCount;

        // CaptureApp readback is complete: map the data and notify the caller.
        CompleteCapture();
    }

    internal static void WaitForCompletion(IMTLCommandBuffer command)
    {
        command.WaitUntilCompleted();
        if (command.Status == MTLCommandBufferStatus.Error)
            throw new InvalidOperationException($"Metal command buffer failed: {command.Error?.LocalizedDescription}");
    }

    internal static void WaitForFrameSlot()
    {
        // Called before Update can write this slot's shared buffers.
        // A completion-counter maximum is not proof that this exact submission completed.
        var frame = FrameContexts[FrameIndex];
        if (frame.CommandBuffer is not { } previous) return;
        WaitForCompletion(previous);
        previous.Dispose();
        frame.CommandBuffer = null;
    }

    internal static void AbortFrame()
    {
        if (!_frameOpen) return;
        _frameOpen = false;
        var frame = FrameContexts[FrameIndex];
        try { frame.Encoder?.EndEncoding(); }
        finally
        {
            frame.Encoder?.Dispose();
            frame.Encoder = null;
            frame.CommandBuffer?.Dispose();
            frame.CommandBuffer = null;
            GraphicsEncoder = null!;
            GraphicsCommandBuffer = null!;
            _backbufferRpd = null;
            _capturePending = false;
            ActivePassId = default;
            ActiveColorTarget = null;
            ActiveColorTexture = null;
            _inFlight.Release();
        }
    }

    internal static void WaitForIdle()
    {
        if (_frameOpen)
            throw new InvalidOperationException("Abort or submit the current frame before waiting for Metal idle.");
        if (GraphicsQueue == null) return;
        // A committed marker covers all earlier submissions, including uploads.
        using var marker = GraphicsQueue.CreateCommandBuffer();
        marker.Commit();
        WaitForCompletion(marker);
        if (FrameContexts != null)
        {
            foreach (var frame in FrameContexts)
                if (frame.CommandBuffer is { } command) WaitForCompletion(command);
            foreach (var frame in FrameContexts)
            {
                frame.CommandBuffer?.Dispose();
                frame.Reset();
            }
        }
        GraphicsCommandBuffer = null!;
        _captureStagingBuffer?.Dispose();
        _captureStagingBuffer = null;
        _capturePending = false;
    }

    /// <summary>Refreshes viewport and scissor after window-size changes. Equivalent to DX and VK Device.HandleResize.
    /// Returning true means the rebuild actually ran.
    /// Returning false means it was skipped because size was invalid, ResizeSemaphore timed out, or an exception occurred.
    /// In that case the caller must not continue into BaseApp.Resize, because ResizeCompute would rebuild compute-storage textures.
    /// The resize flag should stay set and be retried next frame instead.</summary>
    internal static bool HandleResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;

        bool acquired = false;
        try
        {
            acquired = BaseApp.ResizeSemaphore.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (ObjectDisposedException ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Metal] HandleResize: ResizeSemaphore disposed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [Metal] HandleResize: Wait threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (!acquired)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [Metal] HandleResize: ResizeSemaphore wait timed out (background loading?), skip resize this frame");
            return false;
        }
        try
        {
            Display?.Resize(width, height);
            return true;
        }
        finally
        {
            BaseApp.ResizeSemaphore.Release();
        }
    }

    internal static void Shutdown()
    {
        AbortFrame();
        WaitForIdle();
        if (FrameContexts != null)
        {
            for (int i = 0; i < FrameContexts.Length; i++)
            {
                var fb = FrameContexts[i].CommandBuffer;
                fb?.Dispose();
                FrameContexts[i].Reset();
            }
        }

        _captureStagingBuffer?.Dispose();
        _captureStagingBuffer = null;
        MsaaSceneTarget.Dispose();
        TextureUploadBatch?.Dispose();
        GraphicsQueue?.Dispose();

        Display = null!;
        ResourceManager = null!;
        TextureUploadBatch = null!;
        GraphicsQueue = null!;
        FrameContexts = null!;
    }
}
