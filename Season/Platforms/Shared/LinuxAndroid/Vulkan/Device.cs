// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Rect2D = Silk.NET.Vulkan.Rect2D;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Vulkan device and frame-loop hub:
/// bootstrap for Instance, Surface, PhysicalDevice, LogicalDevice, and queues,
/// plus frame-level scheduling through BeforeRender and AfterRender,
/// plus pass orchestration through BeginPass, EndPass, CreateRenderTarget, and BlitToBackbuffer,
/// all driven by the shared FrameSchedule layer.
/// It maps one to one to DirectX/Device.cs.
///
/// Vulkan-specific rules, fixed in step 4 of 1-1, with cross-platform contracts in shared RenderPass.cs and IGraphics:
/// - no barriers are allowed inside a pass body.
///   Image-layout transitions are forbidden inside a render-pass instance.
///   Desktop drivers may tolerate this, but it breaks tile rendering on Android tilers.
///   Runtime transition requests are therefore deferred through the InRenderPass flag plus _pendingTextureTransitions
///   and drained during the out-of-pass stage of the next BeforeRender.
/// - attachment layout transitions use zero explicit barriers.
///   Sampling and present-state transitions are handled entirely by each RenderPass through initialLayout, finalLayout,
///   and subpass dependencies, including the Display backbuffer render pass and VKRenderTarget offscreen render passes.
/// - PSO and RenderPass compatibility:
///   a VkRenderPass is baked into PSO creation and the PSO may only be used with compatible render passes,
///   meaning matching attachment formats, counts, and sample counts.
///   BackbufferCompatible offscreen RTs are compatible with the backbuffer render pass,
///   so Scene PSOs need no extra variants.
///   When RT format changes, for example 1-4 HDR, the corresponding PSOs must be rebuilt.
/// - Y flip:
///   all passes use a negative-height viewport, set by BeginPass from the target size.
///   Offscreen and backbuffer content layouts therefore match, and FinalBlit keeps identity mapping with no direction compensation.
/// - resource destruction:
///   runtime destruction must go through EnqueueDeferredRelease for retire-value-gated deferred release
///   (timeline semaphore on 1.2 devices, VkFence tracking on the 1.1 fallback),
///   because Android tilers must not destroy in-flight resources immediately.
///   During resize, resources may be destroyed and recreated immediately only after DeviceWaitIdle,
///   with MatchBackbufferSize members inside OffscreenTargets recreated in place.
/// </summary>
internal unsafe static class Device
{
    // ===== Core API =====
    internal static Vk Vk = Vk.GetApi();

    internal static Instance Instance;

    internal static KhrSurface KhrSurface = null!;

    internal static SurfaceKHR Surface;

    internal static PhysicalDevice PhysicalDevice;

    /// <summary>
    /// 2-6 clause 6: whether the picked physical device advertises the samplerAnisotropy feature. Unlike D3D12 and
    /// Metal, where anisotropic filtering is guaranteed by the API level, Vulkan makes it optional, and a sampler that
    /// sets AnisotropyEnable without the feature being enabled at device creation is a validation error rather than a
    /// silent downgrade. Populated by PickPhysicalDevice before CreateLogicalDevice reads it.
    /// </summary>
    internal static bool SupportsSamplerAnisotropy;

    /// <summary>
    /// 2-6 clause 6: limits.maxSamplerAnisotropy for the picked device, the per-device ceiling the requested count is
    /// clamped against. The specification only guarantees 16, and requesting above the reported limit is invalid, so
    /// the value is read from the device rather than assumed. Zero until PickPhysicalDevice runs; 1 when unsupported.
    /// </summary>
    internal static float MaxSamplerAnisotropy;

    /// <summary>
    /// Whether the picked device reports core Vulkan 1.2 (apiVersion >= VK_API_VERSION_1_2), which enables
    /// the timeline-semaphore synchronization path in CommandQueue. False selects the VkFence fallback so
    /// 1.1-only devices, old games and old hardware, stay usable; VK_KHR_timeline_semaphore is deliberately
    /// not probed because old Android drivers rarely expose it. Populated by PickPhysicalDevice before
    /// CreateLogicalDevice reads it.
    /// </summary>
    internal static bool TimelineSemaphoreEnabled;

    internal static Silk.NET.Vulkan.Device LogicalDevice;

    // ===== Queue families =====
    internal static uint GraphicsQueueFamily;

    internal static uint PresentQueueFamily;

    internal static uint TransferQueueFamily;

    internal static Silk.NET.Vulkan.Queue GraphicsQueue;

    internal static Silk.NET.Vulkan.Queue PresentQueue;

    internal static Silk.NET.Vulkan.Queue TransferQueue;

    // ===== Debug =====
    internal static ExtDebugUtils? DebugUtils;

    internal static DebugUtilsMessengerEXT DebugMessenger;

    static bool _validationEnabled;

    /// <summary>
    /// Whether the Vulkan validation layer actually activated for this instance
    /// (a requested-but-absent layer reports false). Read-only, for diagnostics.
    /// </summary>
    internal static bool ValidationEnabled => _validationEnabled;

    // ===== Common rendering parameters, aligned with DX =====
    internal static IntPtr WindowHandle;

    internal static uint frameCount = 3;

    internal static uint FrameIndex;

    internal static Vector4 BackgroundColor;

    // Like D3D12, LDR colors and FinalBlit's gamma output are already display encoded.
    // An sRGB attachment would encode them again and change legacy overlay blending.
    internal static Format BackBufferFormat = Format.B8G8R8A8Unorm;

    internal static Format DepthBufferFormat = Format.D32Sfloat;

    // -- HDR SceneColor, step A of 1-4 and mirrored with the DX Device --
    // LinuxApp and AndroidApp finalize this from RenderQuality before Pipeline.Init,
    // because it drives the render-pass format baked into the main PSO.
    // It must not change afterward.
    // false means the step-2 baseline, BackbufferCompatible, with one-step fallback.
    internal static bool HdrSceneColor;

    /// <summary>Actual color format of the Scene-pass render target. Both the render pass baked into the main PSO and the SceneColor RT are driven by this value.</summary>
    internal static Format SceneColorFormat => HdrSceneColor ? Format.R16G16B16A16Sfloat : BackBufferFormat;

    /// <summary>Exposure multiplier for the HDR chain. This is the Vulkan-side read point for shared RenderQuality.HdrExposure, consumed by step B through FinalBlit tonemap push constants and one-point injection in VKPrimitiveGroup.SetLighting.</summary>
    internal static float HdrExposure => RenderQuality.Current.HdrExposure;

    /// <summary>
    /// Linearize clear colors for the HDR chain:
    /// convert display-space background color approximately into linear space with pow 2.2.
    /// This is the inverse of the pow(1/2.2) encoding used by FinalBlit tonemap variants,
    /// so background appearance matches the LDR baseline. Alpha is unchanged.
    /// </summary>
    internal static Vector4 LinearizeClearColor(in Vector4 c) => new(
        MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f), c.W);

    // ===== Upper-layer resources, upload, and shared textures, instantiated by LinuxAndroidGraphics.Init =====
    internal static ResourceManager ResourceManager = null!;

    internal static DescriptorAllocator DescriptorAllocator = null!;

    internal static TextureUploadBatch TextureUploadBatch = null!;

    internal static CommandQueue GraphicsCommandQueue = null!;

    internal static CommandQueue TransferCommandQueue = null!;

    internal static Dictionary<string, Texture> DictionaryTexture = new();

    internal static Texture White = null!;

    /// <summary>Main CommandBuffer for the current frame, assigned by the frame loop after BeginFrame. Equivalent to DX Device.GraphicsCommandList.</summary>
    internal static CommandBuffer GraphicsCommandBuffer;

    // ===== Rendering infrastructure, the glue layer aligned with DX12 Device.CreateSwapChain, CreateDescriptorHeapsAndViews, and CreateGraphicsCommandLists =====
    internal static SwapChain SwapChain = null!;

    internal static Display Display = null!;

    internal static FrameContext[] FrameContexts = null!;

    /// <summary>Per-slot completion value of the frame, carried by the timeline semaphore on 1.2 devices and by a registered VkFence on the 1.1 fallback. GraphicsCommandQueue.WaitForFence(_fenceValues[FrameIndex]) reproduces the DX fenceValues[FrameIndex] pattern.</summary>
    static ulong[] _fenceValues = null!;

    /// <summary>Monotonic frame-completion counter. AfterRender increments it by 1 each time and writes the result into the current frame's _fenceValues.</summary>
    static ulong _nextFenceValue;

    // -- Deferred release, aligned with DX Graphics.EnqueueDeferredRelease and PumpDeferredReleases --
    // Buffers and descriptor sets that may still be referenced by in-flight command buffers must never be destroyed immediately.
    // Desktop drivers may tolerate it, but Android tiler GPUs can read freed pages and corrupt the whole frame.
    // Actual destruction must wait until the timeline fence passes the retire value captured at enqueue time.
    static readonly Queue<(ulong FenceValue, Action Release)> _deferredReleases = new();

    static readonly object _deferredReleaseLock = new();

    /// <summary>
    /// Global ImageView version seed:
    /// whenever any class, Texture or VKRenderTarget, creates a new ImageView,
    /// it takes a monotonic value from here as the identity of that view.
    /// Downstream caches, descriptor sets and framebuffers, use it to decide whether the underlying view changed.
    /// They must never compare View.Handle, because the handle is just a heap pointer and often stays equal after destroy plus recreate,
    /// which would silently miss the change.
    /// </summary>
    static ulong _viewVersionSeed;

    internal static ulong NextViewVersion() => Interlocked.Increment(ref _viewVersionSeed);

    /// <summary>Completion value that the currently recording frame will signal. All in-flight frames earlier than this one have smaller signal values, so waiting for it is sufficient for safety.</summary>
    internal static ulong GetCurrentRetireFenceValue() => _nextFenceValue + 1;

    /// <summary>
    /// Block until the frame that last used frame slot fi has completed on the GPU.
    /// Descriptor sets are per slot and rewritten in place (VKPrimitiveGroup.RewriteDescriptorSets, the sprite quad
    /// equivalent), including from the render thread inside a frame: a bone or prev-SSBO capacity change rewrites all
    /// slots while the frames of the last two slots are still pending. Rewriting a set that a pending command buffer
    /// references is VUID-vkUpdateDescriptorSets-None-03047, and the validation layer answers it by putting that command
    /// buffer in an invalid state, which then reports every following command as VUID-vkEndCommandBuffer-*.
    /// The wait is cheap for the slot being recorded: its fence value belongs to a frame three slots back and has long
    /// completed. For a loading thread it costs at most one in-flight frame.
    /// </summary>
    internal static void WaitForFrameSlot(int fi)
    {
        var values = _fenceValues;
        if (values == null || (uint)fi >= (uint)values.Length)
            return;

        ulong value = values[fi];
        if (value == 0)
            return;

        GraphicsCommandQueue.WaitForFence(value);
    }

    /// <summary>Enqueue a release action into the deferred-release queue.
    /// Thread-safe and callable from loading threads.
    /// fenceValue must come from <see cref="GetCurrentRetireFenceValue"/>.
    /// All in-flight frames earlier than that value signal smaller values, so release is executed only after the GPU timeline passes it.
    /// This uses the same mechanism as Windows Graphics.EnqueueDeferredRelease.
    /// The D3D12 side requires an explicit fenceValue, while this overload is the convenience entry point and both versions coexist.</summary>
    internal static void EnqueueDeferredRelease(Action release)
        => EnqueueDeferredRelease(GetCurrentRetireFenceValue(), release);

    /// <summary>Version with an explicit retire-fence value, shaped like D3D12 Graphics.EnqueueDeferredRelease.
    /// Callers should use this overload when they already know a higher retire value,
    /// for example when special resources must be delayed across multiple timeline cycles.
    /// For ordinary use, <see cref="EnqueueDeferredRelease(Action)"/> is enough.
    /// Null release actions are ignored silently.</summary>
    internal static void EnqueueDeferredRelease(ulong fenceValue, Action release)
    {
        if (release == null)
            return;

        lock (_deferredReleaseLock)
            _deferredReleases.Enqueue((fenceValue, release));
    }

    /// <summary>Execute all deferred releases whose fences have already been passed by the GPU.
    /// When force=true, execute everything after first ensuring the GPU is idle.</summary>
    internal static void PumpDeferredReleases(bool force = false)
    {
        ulong completed = 0;
        bool completedRead = false;

        while (true)
        {
            Action release;
            lock (_deferredReleaseLock)
            {
                if (_deferredReleases.Count == 0)
                    break;

                if (!force)
                {
                    if (!completedRead)
                    {
                        completed = GraphicsCommandQueue.GetCompletedValue();
                        completedRead = true;
                    }
                    if (_deferredReleases.Peek().FenceValue > completed)
                        break;
                }

                release = _deferredReleases.Dequeue().Release;
            }
            release();
        }
    }

    // -- Exclusive GPU access gate --
    // Vulkan requires external synchronization per object: two threads submitting on one VkQueue, or allocating from one
    // VkDescriptorPool or VkCommandPool, is undefined behavior, and the validation layer reports the overlap as
    // "THREADING ERROR : object of type ... is simultaneously used". The render thread therefore holds this gate for the
    // whole frame, and every GPU-touching entry point reachable from the background loading thread - uploads, descriptor
    // allocation and writes, surface rebuilds - takes it as well (see EnterGpuAccessScope).
    // It is a re-entrant monitor, so calls the render thread makes inside its own frame, FlushTextAtlas for example, are
    // free, and only a background thread actually waits, never for more than one frame.
    // Lock order: BaseApp.ResizeSemaphore is always taken before this gate. The render thread's only inner wait is the
    // bounded 200 ms ResizeSemaphore attempt in HandleResize, so the inverted order cannot deadlock, it can only defer
    // one resize tick while a load holds the semaphore.
    static readonly object _gpuAccessLock = new();

    static int _gpuAccessDepth;

    // Loop thread id, recorded by Init. App.Update runs on it outside the gate by design: VKInstancedMesh3D.Update
    // rewrites descriptor sets between frames, when no command buffer is recording, and the gate exists to keep other
    // threads out of that window rather than to fence the owner against itself.
    // Init is only the first claim. Android bootstraps Vulkan on the UI thread and afterwards runs the loop on its own
    // render thread, which takes the record over through MarkLoopThread; until it does, the UI thread keeps it, which
    // is what covers the resource creation BaseApp.Create performs at the end of the bootstrap.
    static int _ownerThreadId;

    /// <summary>
    /// Claims the calling thread as the frame-loop thread, the one allowed to touch GPU state outside the gate.
    /// Platforms whose loop runs on the bootstrap thread need nothing, because Init already recorded it. Android
    /// splits the two, so RenderLoopBody calls this before its first frame, and again whenever that thread is
    /// recreated after a park.
    /// </summary>
    internal static void MarkLoopThread() => _ownerThreadId = Environment.CurrentManagedThreadId;

    internal static void EnterGpuAccess()
    {
        Monitor.Enter(_gpuAccessLock);
        _gpuAccessDepth++;
    }

    internal static void ExitGpuAccess()
    {
        if (_gpuAccessDepth <= 0)
            throw new InvalidOperationException("ExitGpuAccess without a matching EnterGpuAccess");

        _gpuAccessDepth--;
        Monitor.Exit(_gpuAccessLock);
    }

    /// <summary>
    /// Release every nesting level the current thread holds, so a frame that aborts mid-way cannot leave the gate locked
    /// and wedge the background loading thread forever. Does nothing when another thread owns the gate.
    /// </summary>
    internal static void AbortGpuAccessForCurrentThread()
    {
        // IsEntered reports whether the current thread is the owner, which is exactly the question here.
        if (!Monitor.IsEntered(_gpuAccessLock))
            return;

        while (_gpuAccessDepth > 0)
        {
            _gpuAccessDepth--;
            Monitor.Exit(_gpuAccessLock);
        }
    }

    /// <summary>Scope form for `using var gpuAccess = Device.EnterGpuAccessScope();`.</summary>
    internal static GpuAccessScope EnterGpuAccessScope()
    {
        EnterGpuAccess();
        return default;
    }

    /// <summary>
    /// Guard for code that rewrites or submits through objects the render thread also owns: descriptor sets and pools,
    /// the per-slot constant buffers, the command queues. Such a call may come from the loop thread - inside a frame,
    /// or between frames during Update - and must otherwise hold the gate explicitly.
    /// A violation cannot be caught later: it corrupts a frame, or binds an ImageView a resize already destroyed,
    /// and surfaces as artifacts or a driver fault with no Vulkan message pointing at the real caller.
    /// The depth counter cannot answer this because it is global, so ask the monitor which thread owns it.
    /// Conditional("DEBUG") removes both the check and the call sites from Release.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void AssertGpuGateHeld(string what)
    {
        if (Monitor.IsEntered(_gpuAccessLock))
            return;

        // The loop thread is the one place where touching GPU state without the gate is correct: it runs Update and
        // the frame in sequence, so it is never racing itself. Only another thread reaching here is a defect.
        if (_ownerThreadId != 0 && Environment.CurrentManagedThreadId == _ownerThreadId)
            return;

        var message = $"GPU gate not held on a non-owner thread: {what}";
        Console.Error.WriteLine($"[Vulkan] {message}");
        Console.Error.Flush();
        Debug.Fail(message);
    }

    internal readonly struct GpuAccessScope : IDisposable
    {
        public void Dispose() => ExitGpuAccess();
    }

    static int _backbufferWidth;

    static int _backbufferHeight;

    // -- CaptureApp GPU readback --
    static BufferResource _captureStagingBuffer;
    static uint _captureWidth;
    static uint _captureHeight;
    static bool _capturePending;

    // Tracks whether any backbuffer pass has already written during this frame.
    // The first backbuffer pass uses the Clear render pass.
    // Later passes, such as Overlay, must use the Load render pass to preserve already presented contents. See Display.RenderPassLoad.
    static bool _backbufferWrittenThisFrame;

    static readonly string[] DeviceExtensions = ["VK_KHR_swapchain"];

    /// <summary>
    /// Contract 1-9: VK_EXT_memory_budget, the only Vulkan mechanism that reports per-heap usage. It is an optional device
    /// extension, so it is never requested unconditionally: enabling an extension the driver does not advertise makes
    /// vkCreateDevice fail, which would trade a diagnostic readout for a hard startup failure.
    /// </summary>
    const string MemoryBudgetExtension = "VK_EXT_memory_budget";

    /// <summary>
    /// Contract 1-9: whether the picked physical device advertises <see cref="MemoryBudgetExtension"/>. Populated by
    /// PickPhysicalDevice before CreateLogicalDevice reads it to decide whether to enable the extension, and read again by
    /// the usage query so an unenabled extension is never called. False means the backend reports "unknown", not zero.
    /// </summary>
    internal static bool MemoryBudgetSupported;

    static readonly string[] ValidationLayers = ["VK_LAYER_KHRONOS_validation"];

    /// <summary>
    /// Bootstrap Vulkan.
    /// </summary>
    /// <param name="window">Native window handle, stored only and not interpreted here.</param>
    /// <param name="debug">Whether to enable Validation Layer and DebugUtils.</param>
    /// <param name="surfaceExtensions">Instance extensions provided by the window system, such as the return value of SDL_Vulkan_GetInstanceExtensions.</param>
    /// <param name="createSurface">Factory from the window system that creates VkSurfaceKHR. The input is the instance handle and the output is the surface handle.</param>
    internal static void Init(IntPtr window, bool debug, string[] surfaceExtensions, Func<ulong, ulong> createSurface)
    {
        // Init runs on the loop thread, which is also where App.Update and the frame execute. Recording it here lets
        // AssertGpuGateHeld tell "the owner thread between frames" apart from "a background producer that skipped the gate".
        _ownerThreadId = Environment.CurrentManagedThreadId;

        WindowHandle = window;
        BackgroundColor = new Vector4(1f, 1f, 1f, 1f);

        _validationEnabled = debug && CheckValidationLayerSupport();

        CreateInstance(surfaceExtensions, _validationEnabled);

        if (_validationEnabled) SetupDebugMessenger();

        CreateSurface(createSurface);

        PickPhysicalDevice();

        CreateLogicalDevice(_validationEnabled);
    }

    static bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        Vk.EnumerateInstanceLayerProperties(ref layerCount, null);
        if (layerCount == 0) return false;

        var layers = new LayerProperties[layerCount];
        fixed (LayerProperties* p = layers)
            Vk.EnumerateInstanceLayerProperties(ref layerCount, p);

        foreach (var requested in ValidationLayers)
        {
            bool found = false;
            for (int i = 0; i < layerCount; i++)
            {
                fixed (LayerProperties* p = &layers[i])
                {
                    var name = Marshal.PtrToStringAnsi((IntPtr)p->LayerName);
                    if (name == requested) { found = true; break; }
                }
            }
            if (!found) return false;
        }
        return true;
    }

    static void CreateInstance(string[] surfaceExtensions, bool validation)
    {
        var appNamePtr = SilkMarshal.StringToPtr("Season");
        var engineNamePtr = SilkMarshal.StringToPtr("SeasonEngine");

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appNamePtr,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)engineNamePtr,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        var extensions = new List<string>(surfaceExtensions);
        if (validation) extensions.Add("VK_EXT_debug_utils");

        nint extPtr = SilkMarshal.StringArrayToPtr(extensions);
        nint layerPtr = validation ? SilkMarshal.StringArrayToPtr(ValidationLayers) : 0;

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extPtr,
            EnabledLayerCount = validation ? (uint)ValidationLayers.Length : 0,
            PpEnabledLayerNames = validation ? (byte**)layerPtr : null
        };

        var result = Vk.CreateInstance(in createInfo, null, out var instance);

        // Pre-1.2 loaders reject a higher instance request outright instead of negotiating it down,
        // so a VK_ERROR_INCOMPATIBLE_DRIVER falls back to 1.1. Nothing at the instance level needs
        // 1.2: the device-side CommandQueue fallback keeps 1.1 GPUs fully usable.
        if (result == Result.ErrorIncompatibleDriver)
        {
            appInfo.ApiVersion = Vk.Version11;
            result = Vk.CreateInstance(in createInfo, null, out instance);
        }

        SilkMarshal.Free(appNamePtr);
        SilkMarshal.Free(engineNamePtr);
        SilkMarshal.Free(extPtr);
        if (validation) SilkMarshal.Free(layerPtr);

        if (result != Result.Success)
            throw new Exception($"vkCreateInstance failed: {result}");

        Instance = instance;

        if (!Vk.TryGetInstanceExtension(instance, out KhrSurface surfaceExt))
            throw new Exception("VK_KHR_surface extension not available");
        KhrSurface = surfaceExt;
    }

    static void SetupDebugMessenger()
    {
        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils)) return;
        DebugUtils = debugUtils;

        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback)
        };

        debugUtils.CreateDebugUtilsMessenger(Instance, in createInfo, null, out var messenger);
        DebugMessenger = messenger;
    }

    static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* user)
    {
        var msg = Marshal.PtrToStringAnsi((IntPtr)data->PMessage);
        Debug.WriteLine($"[Vulkan] {severity}: {msg}");

        // Debug.WriteLine is compiled out in Release, and Linux desktop has no listener,
        // so validation errors would disappear completely and the program would seem to exit with no message.
        // Error and Warning therefore always go to stderr as well, independent of build configuration.
        if ((severity & (DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                       | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt)) != 0)
        {
            Console.Error.WriteLine($"[Vulkan] {severity}: {msg}");
            Console.Error.Flush();
        }
        return Vk.False;
    }

    static void CreateSurface(Func<ulong, ulong> createSurface)
    {
        ulong rawSurface = createSurface((ulong)Instance.Handle);
        if (rawSurface == 0)
            throw new Exception("Surface factory returned null surface");
        Surface = new SurfaceKHR(rawSurface);
    }

    static void PickPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
            throw new Exception("No Vulkan-capable GPU found");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Vk.EnumeratePhysicalDevices(Instance, ref count, p);

        PhysicalDevice picked = default;
        for (int i = 0; i < devices.Length; i++)
        {
            if (!IsDeviceSuitable(devices[i])) continue;

            Vk.GetPhysicalDeviceProperties(devices[i], out var props);
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                picked = devices[i];
                break;
            }
            if (picked.Handle == 0) picked = devices[i];
        }

        if (picked.Handle == 0)
            throw new Exception("No suitable Vulkan device");

        PhysicalDevice = picked;

        // 2-6 clause 6: cache the anisotropy feature bit and its device ceiling before the logical device is created,
        // because CreateLogicalDevice has to decide whether to request the feature and Pipeline has to decide what to
        // clamp against. Both read these fields rather than re-querying, so there is one answer per run.
        Vk.GetPhysicalDeviceFeatures(picked, out var pickedFeatures);
        Vk.GetPhysicalDeviceProperties(picked, out var pickedProps);
        SupportsSamplerAnisotropy = pickedFeatures.SamplerAnisotropy;
        MaxSamplerAnisotropy = SupportsSamplerAnisotropy ? pickedProps.Limits.MaxSamplerAnisotropy : 1f;

        // Contract 1-9: probed once here, for the same reason as the features above - both the device creation that has
        // to request the extension and the later usage query that has to decide whether calling it is legal read this
        // field instead of re-enumerating extensions per frame.
        MemoryBudgetSupported = HasDeviceExtension(picked, MemoryBudgetExtension);

        // Sync-path capability probe consumed by CreateLogicalDevice, CommandQueue, and ShaderCompiler:
        // only core 1.2 devices may enable the timeline-semaphore feature, everything else takes the
        // VkFence fallback. See the field doc for why the KHR extension is not probed.
        TimelineSemaphoreEnabled = pickedProps.ApiVersion >= Vk.Version12;
        DeviceServices.BaseApp?.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] device apiVersion=0x{pickedProps.ApiVersion:X} syncPath={(TimelineSemaphoreEnabled ? "timeline-semaphore" : "fence-fallback")}");
        if (!pickedFeatures.IndependentBlend)
        {
            RenderQuality.Current.MotionVectors = false;
            if (RenderQuality.Current.EffectiveAntiAliasing == AaMode.Taa)
                RenderQuality.Current.EffectiveAntiAliasing = HdrSceneColor ? AaMode.Fxaa : AaMode.Off;
            DeviceServices.BaseApp?.AddLog(LogType.Backend, "Vulkan independentBlend unavailable; velocity/TAA disabled.");
        }

        // Msaa4x capability probe: both planes of the private multisample pair must accept the sample count, and
        // the tier is still changeable here because the scene targets, the render passes and the main PSO family
        // are all created after Init. Falling back now is the only alternative to a hard vkCreateImage/vkCreateRenderPass
        // failure (or a device loss on a tiler) at the first scene pass.
        if (MsaaSampleCount > 1
            && (!SupportsSampleCount(picked, SceneColorFormat, ImageUsageFlags.ColorAttachmentBit, MsaaSampleCount)
                || !SupportsSampleCount(picked, DepthBufferFormat, ImageUsageFlags.DepthStencilAttachmentBit, MsaaSampleCount)))
        {
            uint requested = MsaaSampleCount;
            MsaaSampleCount = 1;
            if (RenderQuality.Current.EffectiveAntiAliasing == AaMode.Msaa4x)
                RenderQuality.Current.EffectiveAntiAliasing = HdrSceneColor ? AaMode.Fxaa : AaMode.Off;
            DeviceServices.BaseApp?.AddLog(LogType.Backend,
                $"Vulkan {requested}x MSAA is not supported by the scene formats; the Msaa4x tier falls back to {RenderQuality.Current.EffectiveAntiAliasing}.");
        }

        FindQueueFamilies(picked);
    }

    /// <summary>Whether the device accepts a 2D optimal-tiling image of the given format, usage and sample count.</summary>
    static bool SupportsSampleCount(PhysicalDevice device, Format format, ImageUsageFlags usage, uint samples)
    {
        var result = Vk.GetPhysicalDeviceImageFormatProperties(device, format, ImageType.Type2D,
            ImageTiling.Optimal, usage, ImageCreateFlags.None, out var props);
        return result == Result.Success && (props.SampleCounts & (SampleCountFlags)samples) != 0;
    }

    /// <summary>
    /// Total device-local GPU memory (MB), taken from the discrete GPU when one is
    /// present and otherwise from the best remaining adapter, plus whether a discrete
    /// GPU exists. Used by the AI feature to grade whether a model can run here.
    /// Never throws; reports 0/false when Vulkan is unavailable.
    /// </summary>
    internal static int GetGpuMemoryInfo(out bool hasDedicatedGpu)
    {
        hasDedicatedGpu = false;
        ulong bestDiscrete = 0;
        ulong bestOther = 0;

        try
        {
            uint count = 0;

            if (Vk.EnumeratePhysicalDevices(Instance, ref count, null) != Result.Success || count == 0)
            {
                return 0;
            }

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = devices)
                Vk.EnumeratePhysicalDevices(Instance, ref count, p);

            for (int i = 0; i < count; i++)
            {
                Vk.GetPhysicalDeviceProperties(devices[i], out var props);
                Vk.GetPhysicalDeviceMemoryProperties(devices[i], out var memProps);

                ulong deviceLocal = 0;

                for (uint h = 0; h < memProps.MemoryHeapCount; h++)
                {
                    if ((memProps.MemoryHeaps[(int)h].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                    {
                        deviceLocal += memProps.MemoryHeaps[(int)h].Size;
                    }
                }

                if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    hasDedicatedGpu = true;
                    if (deviceLocal > bestDiscrete) bestDiscrete = deviceLocal;
                }
                else if (deviceLocal > bestOther)
                {
                    bestOther = deviceLocal;
                }
            }
        }
        catch
        {
            // A diagnostic probe must never break the caller.
        }

        var best = hasDedicatedGpu ? bestDiscrete : bestOther;
        return (int)(best / (1024 * 1024));
    }

    /// <summary>
    /// Contract 1-9: live device-local usage and budget summed over the picked device's memory heaps, from
    /// <c>VK_EXT_memory_budget</c>. Unlike <see cref="GetGpuMemoryInfo"/>, which reports the ceiling once at startup for
    /// model grading, this is what the driver is accounting against right now, including allocations made outside our
    /// own resources.
    ///
    /// Only device-local heaps are summed, so a discrete GPU reports its VRAM rather than VRAM plus system RAM; on unified
    /// memory devices, where the single device-local heap is system memory, that heap is the GPU's working budget and is
    /// reported as-is. Never throws; returns false when the extension was not enabled or the query reports no budget.
    /// </summary>
    internal static bool TryQueryGpuMemoryUsage(out long usedBytes, out long totalBytes)
    {
        usedBytes = 0;
        totalBytes = 0;

        if (!MemoryBudgetSupported) return false;

        try
        {
            var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT
            {
                SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt
            };

            var memoryProperties = new PhysicalDeviceMemoryProperties2
            {
                SType = StructureType.PhysicalDeviceMemoryProperties2,
                PNext = &budget
            };

            Vk.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &memoryProperties);

            long used = 0;
            long total = 0;
            uint heapCount = memoryProperties.MemoryProperties.MemoryHeapCount;
            for (uint i = 0; i < heapCount; i++)
            {
                if ((memoryProperties.MemoryProperties.MemoryHeaps[(int)i].Flags & MemoryHeapFlags.DeviceLocalBit) == 0)
                    continue;
                used += (long)budget.HeapUsage[i];
                total += (long)budget.HeapBudget[i];
            }

            if (total <= 0) return false;

            usedBytes = used;
            totalBytes = total;
            return true;
        }
        catch
        {
            // A diagnostic readout must never break the frame that asked for it.
            return false;
        }
    }

    static bool IsDeviceSuitable(PhysicalDevice device)
    {
        // The swapchain extension must be supported.
        uint extCount = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, null);
        if (extCount == 0) return false;

        var props = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = props)
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, p);

        foreach (var requested in DeviceExtensions)
        {
            bool found = false;
            for (int i = 0; i < extCount; i++)
            {
                fixed (ExtensionProperties* ep = &props[i])
                {
                    var name = Marshal.PtrToStringAnsi((IntPtr)ep->ExtensionName);
                    if (name == requested) { found = true; break; }
                }
            }
            if (!found) return false;
        }

        // Both graphics and present queue families must exist.
        uint qCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref qCount, null);
        if (qCount == 0) return false;

        var qFams = new QueueFamilyProperties[qCount];
        fixed (QueueFamilyProperties* p = qFams)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref qCount, p);

        bool hasGraphics = false, hasPresent = false;
        for (uint i = 0; i < qCount; i++)
        {
            if ((qFams[i].QueueFlags & QueueFlags.GraphicsBit) != 0) hasGraphics = true;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 supported);
            if (supported) hasPresent = true;
            if (hasGraphics && hasPresent) break;
        }
        return hasGraphics && hasPresent;
    }

    /// <summary>
    /// Contract 1-9: whether the device advertises one named device extension. Used for optional extensions such as
    /// VK_EXT_memory_budget, which must be probed before vkCreateDevice because requesting an unsupported one fails
    /// outright rather than degrading.
    /// </summary>
    static bool HasDeviceExtension(PhysicalDevice device, string name)
    {
        uint extCount = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, null);
        if (extCount == 0) return false;

        var props = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = props)
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, p);

        for (int i = 0; i < extCount; i++)
        {
            fixed (ExtensionProperties* ep = &props[i])
            {
                if (Marshal.PtrToStringAnsi((IntPtr)ep->ExtensionName) == name) return true;
            }
        }
        return false;
    }

    static void FindQueueFamilies(PhysicalDevice device)
    {
        uint qCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref qCount, null);
        var qFams = new QueueFamilyProperties[qCount];
        fixed (QueueFamilyProperties* p = qFams)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref qCount, p);

        GraphicsQueueFamily = uint.MaxValue;
        PresentQueueFamily = uint.MaxValue;
        TransferQueueFamily = uint.MaxValue;

        for (uint i = 0; i < qCount; i++)
        {
            var flags = qFams[i].QueueFlags;

            if (GraphicsQueueFamily == uint.MaxValue
                && (flags & QueueFlags.GraphicsBit) != 0)
                GraphicsQueueFamily = i;

            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 supported);
            if (PresentQueueFamily == uint.MaxValue && supported)
                PresentQueueFamily = i;

            // Prefer a dedicated transfer queue family, one that supports transfer but not graphics.
            if (TransferQueueFamily == uint.MaxValue
                && (flags & QueueFlags.TransferBit) != 0
                && (flags & QueueFlags.GraphicsBit) == 0)
                TransferQueueFamily = i;
        }

        // Fallback: use the Graphics family when no dedicated Transfer family exists.
        if (TransferQueueFamily == uint.MaxValue)
            TransferQueueFamily = GraphicsQueueFamily;
    }

    static void CreateLogicalDevice(bool validation)
    {
        var families = new HashSet<uint> { GraphicsQueueFamily, PresentQueueFamily, TransferQueueFamily };
        var queueCreateInfos = new DeviceQueueCreateInfo[families.Count];
        float priority = 1f;

        int idx = 0;
        foreach (var fam in families)
        {
            queueCreateInfos[idx++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = fam,
                QueueCount = 1,
                PQueuePriorities = &priority
            };
        }

        // 2-6 clause 6: the feature is now requested only when the device advertises it. It was previously requested
        // unconditionally, which happens to work on every desktop and virtually every Android driver, but enabling an
        // unsupported feature makes vkCreateDevice fail outright - a hard startup failure in exchange for a filtering
        // refinement is the wrong trade, so the sampler degrades to isotropic instead.
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out var supportedFeatures);
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = SupportsSamplerAnisotropy,
            IndependentBlend = supportedFeatures.IndependentBlend
        };

        // Vulkan 1.2 feature, requested only when the device reports core 1.2: enabling a feature the
        // device does not advertise would fail vkCreateDevice on 1.1 hardware. Devices without it take
        // the VkFence fallback in CommandQueue, which keeps the same monotonic-value contract.
        var vk12Features = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            TimelineSemaphore = true
        };

        // Contract 1-9: the memory-budget extension is optional and therefore part of the list only when the picked
        // device advertises it. Everything else stays exactly the static set declared in DeviceExtensions.
        string[] deviceExtensions = MemoryBudgetSupported
            ? [.. DeviceExtensions, MemoryBudgetExtension]
            : DeviceExtensions;

        nint extPtr = SilkMarshal.StringArrayToPtr(deviceExtensions);
        nint layerPtr = validation ? SilkMarshal.StringArrayToPtr(ValidationLayers) : 0;

        Result result;
        Silk.NET.Vulkan.Device dev;

        fixed (DeviceQueueCreateInfo* qcip = queueCreateInfos)
        {
            DeviceCreateInfo createInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = TimelineSemaphoreEnabled ? &vk12Features : null,
                QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                PQueueCreateInfos = qcip,
                PEnabledFeatures = &features,
                EnabledExtensionCount = (uint)deviceExtensions.Length,
                PpEnabledExtensionNames = (byte**)extPtr,
                EnabledLayerCount = validation ? (uint)ValidationLayers.Length : 0,
                PpEnabledLayerNames = validation ? (byte**)layerPtr : null
            };

            result = Vk.CreateDevice(PhysicalDevice, in createInfo, null, out dev);
        }

        SilkMarshal.Free(extPtr);
        if (validation) SilkMarshal.Free(layerPtr);

        if (result != Result.Success)
            throw new Exception($"vkCreateDevice failed: {result}");

        LogicalDevice = dev;

        Vk.GetDeviceQueue(dev, GraphicsQueueFamily, 0, out var gq); GraphicsQueue = gq;
        Vk.GetDeviceQueue(dev, PresentQueueFamily, 0, out var pq); PresentQueue = pq;
        Vk.GetDeviceQueue(dev, TransferQueueFamily, 0, out var tq); TransferQueue = tq;
    }

    /// <summary>
    /// Create the SwapChain and upper-layer resource managers, equivalent to DX Device.CreateSwapChain.
    /// Ordering is strict:
    /// ResourceManager, DescriptorAllocator, TextureUploadBatch, GraphicsCommandQueue, and TransferCommandQueue
    /// are all initialized only after SwapChain.Create,
    /// so the later Display.Initialize can access a fully constructed device resource set.
    /// </summary>
    internal static void CreateSwapChain(int width, int height)
    {
        _backbufferWidth = width;
        _backbufferHeight = height;

        SwapChain = new SwapChain(
            Vk, Instance, PhysicalDevice, LogicalDevice,
            KhrSurface, Surface,
            preferredFrameCount: frameCount,
            preferredFormat: BackBufferFormat);
        SwapChain.Create(width, height, GraphicsQueueFamily, PresentQueueFamily);
        // Decouple in-flight frame count from swapchain image count.
        // Framebuffers are indexed by imageIndex, while per-frame resources are indexed by FrameIndex, and the two counts do not need to match.
        // Android drivers may return far more than 3 images.
        // If the in-flight frame count followed that value, the CPU could outrun the GPU too much,
        // widening the race window on per-frame buffers and increasing input latency.
        // Clamp the upper bound to 3, matching DX triple buffering.
        frameCount = Math.Min(SwapChain.FrameCount, 3u);
        BackBufferFormat = SwapChain.BackBufferFormat;
        // Override the requested size with the actual Extent chosen by SwapChain.
        // caps.CurrentExtent may differ from the requested width and height, for example when startup insets are not ready yet or the device is rotating.
        // The framebuffer created by Display.Initialize must match the swapchain image size,
        // or rendering becomes undefined and can even produce a full black screen.
        // HandleResize and Recreate already follow this rule.
        _backbufferWidth = (int)SwapChain.Extent.Width;
        _backbufferHeight = (int)SwapChain.Extent.Height;
        RecreateRenderFinishedSemaphores();
        DeviceServices.BaseApp?.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] SwapChain created images={SwapChain.FrameCount} extent={SwapChain.Extent.Width}x{SwapChain.Extent.Height} requested={width}x{height} presentMode={SwapChain.PresentMode} format={SwapChain.BackBufferFormat}");

        // Resources, descriptors, upload batch, and queues.
        ResourceManager = new ResourceManager(Vk, PhysicalDevice, LogicalDevice);
        DescriptorAllocator = new DescriptorAllocator(Vk, LogicalDevice);
        TextureUploadBatch = new TextureUploadBatch(Vk, LogicalDevice, TransferQueueFamily);
        GraphicsCommandQueue = new CommandQueue(Vk, LogicalDevice, GraphicsQueue, GraphicsQueueFamily);
        TransferCommandQueue = new CommandQueue(Vk, LogicalDevice, TransferQueue, TransferQueueFamily);
    }

    /// <summary>
    /// RenderFinished semaphores, waited by present, must be allocated per swapchain image rather than per in-flight frame slot.
    /// Ring fences guarantee only that GPU rendering is complete, not that the present engine has already consumed the wait on that semaphore.
    /// Under Android FIFO, present may queue up across multiple vsyncs.
    /// Reusing only 3 binary semaphores by frame slot could therefore hit a semaphore whose previous present wait is still pending,
    /// which the spec forbids and treats as undefined behavior, leading to full-screen flicker or corruption.
    /// When semaphores are allocated per image, acquire returning an image proves that its previous present is already complete,
    /// so the corresponding semaphore is safe to reuse.
    /// After swapchain recreation the image count may change and old semaphores may still carry a signaled state,
    /// so they must be recreated together with the swapchain.
    /// </summary>
    static void RecreateRenderFinishedSemaphores()
    {
        foreach (var s in _renderFinishedPerImage)
            if (s.Handle != 0)
                Vk.DestroySemaphore(LogicalDevice, s, null);

        _renderFinishedPerImage = new Silk.NET.Vulkan.Semaphore[SwapChain.FrameCount];
        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < _renderFinishedPerImage.Length; i++)
        {
            if (Vk.CreateSemaphore(LogicalDevice, in semInfo, null, out _renderFinishedPerImage[i]) != Result.Success)
                throw new Exception("vkCreateSemaphore (renderFinished per image) failed");
        }
    }

    static Silk.NET.Vulkan.Semaphore[] _renderFinishedPerImage = [];

    /// <summary>Whether the current graphics command buffer is inside a render pass, the BeginPass to EndPass interval.
    /// Since step 1, every frame can contain multiple passes driven by FrameSchedule.
    /// Vulkan forbids recording image-layout transition barriers inside a render pass, except self-dependencies.
    /// Desktop drivers may tolerate this, but Android tilers such as Adreno and Mali can break tile rendering and produce black screens or artifacts.
    /// Texture.EnsureReadyForRendering therefore defers transitions to the out-of-pass stage of the next BeforeRender based on this flag.</summary>
    internal static bool InRenderPass;

    /// <summary>Id of the current pass, written by BeginPass and cleared by EndPass.
    /// OutlineMask routing in SetPipeline depends on it, mirroring DX DirectX.Device.ActivePassId.
    /// It is read only inside the pass body with no concurrency.</summary>
    internal static Season.Rendering.RenderPassId ActivePassId;

    /// <summary>Used by standalone full-screen pipelines to verify the actual output attachment; valid only inside the current pass.</summary>
    internal static VKRenderTarget? ActiveColorTarget;

    /// <summary>The explicit depth RT actually bound as the depth attachment in this pass; null when there is no explicit depth, with the same semantics as DX Device.ActiveDepthTarget.
    /// Side pipelines use it to confirm that the depth semantics seen during preparation have not changed.</summary>
    internal static VKRenderTarget? ActiveDepthTarget;

    /// <summary>The velocity RT actually bound as color attachment 1 in this pass; null in the single-target shape.
    /// Side pipelines that record their own draws inside the pass must bake a PSO matching the attachment count, and the PassDesc they hold during preparation is not enough to infer this:
    /// when explicit depth is absent the three-target branch is never taken, so the velocity slot does not exist.</summary>
    internal static VKRenderTarget? ActiveVelocityTarget;

    /// <summary>A0.2: the normal RT actually bound as color attachment 2 in this pass; it can only be non-null when velocity is also an attachment. Side pipelines use it to bake the four-attachment PSO.</summary>
    internal static VKRenderTarget? ActiveNormalTarget;

    /// <summary>
    /// Sample count of the Msaa4x tier's backend-private multisample pair (1 = tier off), finalized by
    /// LinuxApp/AndroidApp before <see cref="Init"/> because the main PSO family and the scene render pass both bake
    /// it, exactly like Display.MsaaSampleCount on D3D12 and Device.MsaaSampleCount on Metal.
    /// The backbuffer and every target the shared layer can see stay single-sampled: only the three scene pass ids
    /// render into the multisampled pair and resolve into the shared SceneColor at the end of each pass.
    /// </summary>
    internal static uint MsaaSampleCount = 1;

    /// <summary>The scene pass ids that the Msaa4x tier renders into the backend-private multisample pair,
    /// mirroring DX Device.IsMsaaScenePass and Metal Device.IsMsaaScenePass.</summary>
    internal static bool IsMsaaScenePass(Season.Rendering.RenderPassId id)
        => id is Season.Rendering.RenderPassId.Scene or Season.Rendering.RenderPassId.SceneOpaque
            or Season.Rendering.RenderPassId.SceneTransparent;

    // Pending texture layout transitions.
    // When requested inside a render pass they are queued here and drained outside passes during the next frame.
    // Accessed only by the render thread.
    static readonly List<Texture> _pendingTextureTransitions = new();

    // How many deferred-transition diagnostics have been written. Deferring is a fallback, not a steady state,
    // so the first few are worth surfacing and the rest would only flood the log every frame.
    static int _deferTransitionWarnings;

    internal static void DeferTextureTransition(Texture tex)
    {
        if (!_pendingTextureTransitions.Contains(tex))
        {
            _pendingTextureTransitions.Add(tex);

            // Every producer that can be reached from a draw - TextureUploadBatch and Texture.UploadPixels - now
            // leaves its image in ShaderReadOnlyOptimal before publishing Ready, so arriving here means some other
            // path (a reused render target, an intermediate left in General) skipped its own transition. The pass
            // that hit this keeps sampling the old layout until the queue drains in the next BeforeRender, which is
            // exactly VUID-vkCmdDraw-None-09600 on a desktop driver and visible corruption on a tiler.
            // Release builds still have to see it: Debug.WriteLine alone is compiled out.
            if (_deferTransitionWarnings < 8)
            {
                _deferTransitionWarnings++;
                Console.Error.WriteLine(
                    $"[Vulkan] deferred texture layout transition requested inside a render pass (current layout {tex.CurrentLayout}); " +
                    "the pass will sample this image with its previous layout");
                Console.Error.Flush();
            }
        }
    }

    internal static void CancelTextureTransition(Texture tex)
    {
        _pendingTextureTransitions.Remove(tex);
    }

    // Track acquire and present result states.
    // Log only when the state changes to avoid flooding while Suboptimal keeps repeating.
    static Result _lastAcquireResult = Result.Success;

    static Result _lastPresentResult = Result.Success;

    /// <summary>
    /// Create Display, including Depth, RenderPass, Framebuffers, and Viewport/Scissor. Equivalent to DX Device.CreateDescriptorHeapsAndViews.
    /// </summary>
    internal static void CreateDescriptorHeapsAndViews()
    {
        Display = new Display(Vk, PhysicalDevice, LogicalDevice, BackBufferFormat, DepthBufferFormat);
        Display.SetClearColor(BackgroundColor);
        Display.Initialize(_backbufferWidth, _backbufferHeight, SwapChain.ImageViews);
    }

    /// <summary>
    /// Create per-frame FrameContext objects, CommandPool, CommandBuffer, and semaphores, plus a 1x1 White placeholder texture. Equivalent to DX Device.CreateGraphicsCommandLists.
    /// </summary>
    internal static void CreateGraphicsCommandLists()
    {
        FrameContexts = new FrameContext[frameCount];
        _fenceValues = new ulong[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            FrameContexts[i] = new FrameContext(Vk, LogicalDevice);
            FrameContexts[i].Initialize(GraphicsQueueFamily);
            _fenceValues[i] = 0;
        }
        FrameIndex = 0;
        _nextFenceValue = 0;

        // White texture:
        // Texture("White", null) creates a 1x1 white texture.
        // Pre-register it in DictionaryTexture so repeated GetOrCreate calls hit the same entry.
        if (White == null)
        {
            White = new Texture("White", null);
            DictionaryTexture["White"] = White;
        }

        // Upload White synchronously.
        // TextureUploadBatch.Execute internally performs transfer command-buffer recording, Submit, and WaitIdle.
        TextureUploadBatch.Execute();
    }

    // -- CaptureApp GPU readback implementation --

    /// <summary>
    /// Called after CmdEndRenderPass and before frame.End.
    /// Copies the current swapchain image into a host-visible staging buffer.
    /// </summary>
    internal static void CaptureBackBuffer()
    {
        if (!SwapChain.SupportsReadback)
        {
            BaseApp.CaptureAppTcs?.TrySetException(new NotSupportedException("Surface does not support transfer-source readback."));
            BaseApp.CaptureAppTcs = null;
            return;
        }
        var backbuffer = SwapChain.Images[SwapChain.CurrentImageIndex];
        if (backbuffer.Handle == 0) return;

        _captureWidth = SwapChain.Extent.Width;
        _captureHeight = SwapChain.Extent.Height;

        ulong totalBytes = (ulong)_captureWidth * _captureHeight * 4;
        if (_captureStagingBuffer.Buffer.Handle == 0 || _captureStagingBuffer.Size < totalBytes)
        {
            if (_captureStagingBuffer.Buffer.Handle != 0)
                ResourceManager.DestroyBuffer(_captureStagingBuffer);
            _captureStagingBuffer = ResourceManager.CreateBuffer(
                totalBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }

        // 1) Pipeline barrier: PresentSrcKHR → TransferSrcOptimal
        var preBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryReadBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = backbuffer,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        Vk.CmdPipelineBarrier(
            GraphicsCommandBuffer,
            PipelineStageFlags.BottomOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0, null,
            0, null,
            1, in preBarrier);

        // 2) vkCmdCopyImageToBuffer
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(_captureWidth, _captureHeight, 1)
        };

        Vk.CmdCopyImageToBuffer(
            GraphicsCommandBuffer,
            backbuffer, ImageLayout.TransferSrcOptimal,
            _captureStagingBuffer.Buffer,
            1, in region);

        // 3) Pipeline barrier: TransferSrcOptimal → PresentSrcKhr
        var postBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.MemoryReadBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = backbuffer,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        Vk.CmdPipelineBarrier(
            GraphicsCommandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.BottomOfPipeBit,
            0,
            0, null,
            0, null,
            1, in postBarrier);

        _capturePending = true;
    }

    /// <summary>
    /// Called after Present.
    /// Wait for GPU completion, map the staging buffer, then notify the CaptureApp caller.
    /// </summary>
    internal static void CompleteCapture()
    {
        if (!_capturePending || _captureStagingBuffer.Buffer.Handle == 0) return;
        _capturePending = false;

        try
        {
            Vk.DeviceWaitIdle(LogicalDevice);

            void* mappedData;
            CheckResult(Vk.MapMemory(LogicalDevice, _captureStagingBuffer.Memory, 0, _captureStagingBuffer.Size, 0, &mappedData));

            int w = (int)_captureWidth;
            int h = (int)_captureHeight;
            byte[] pixels = new byte[w * h * 4];

            // Surface format can be BGRA on Linux or RGBA on Android.
            bool bgra = BackBufferFormat is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
            fixed (byte* pDst = pixels)
            {
                byte* pSrc = (byte*)mappedData;
                for (int i = 0; i < w * h; i++)
                {
                    pDst[i * 4 + 0] = pSrc[i * 4 + (bgra ? 2 : 0)];
                    pDst[i * 4 + 1] = pSrc[i * 4 + 1]; // G ← G
                    pDst[i * 4 + 2] = pSrc[i * 4 + (bgra ? 0 : 2)];
                    pDst[i * 4 + 3] = pSrc[i * 4 + 3]; // A ← A
                }
            }

            Vk.UnmapMemory(LogicalDevice, _captureStagingBuffer.Memory);

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

    // -- GPU event labels, for RenderDoc and AGI capture layering, step 0 --
    // Uses CmdBegin and EndDebugUtilsLabel from VK_EXT_debug_utils.
    // This is a no-op when validation is not enabled and DebugUtils is null.
    // Labels are pre-baked as NUL-terminated UTF8 to avoid per-frame allocation, and their order matches the RenderPassId enum.
    static readonly byte[][] _passLabels =
    [
        "Shadow\0"u8.ToArray(),
        "Scene\0"u8.ToArray(),
        "Post\0"u8.ToArray(),
        "OutlineMask\0"u8.ToArray(),
        "FinalBlit\0"u8.ToArray(),
        "Overlay\0"u8.ToArray(),
        "SceneOpaque\0"u8.ToArray(),
        "FogComposite\0"u8.ToArray(),
        "SceneTransparent\0"u8.ToArray(),
        "WeatherComposite\0"u8.ToArray(),
        "SceneDecal\0"u8.ToArray(),
        "SceneWater\0"u8.ToArray(),
        "SsrComposite\0"u8.ToArray(),
        "SceneWaterfall\0"u8.ToArray(),
    ];

    internal static void PushDebugGroup(byte[] utf8LabelZ)
    {
        if (DebugUtils == null) return;
        fixed (byte* pLabel = utf8LabelZ)
        {
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = pLabel
            };
            DebugUtils.CmdBeginDebugUtilsLabel(GraphicsCommandBuffer, in label);
        }
    }

    internal static void PopDebugGroup()
    {
        DebugUtils?.CmdEndDebugUtilsLabel(GraphicsCommandBuffer);
    }

    /// <summary>
    /// Begin the frame:
    /// acquire the next swapchain image, reset the command buffer, and drain deferred texture layout transitions outside passes.
    /// Render-pass Begin and End have already been moved down into BeginPass and EndPass, step 1, driven by FrameSchedule.
    /// The fence for the same slot was already waited at the end of the previous frame in AfterRender, aligned with DX MoveToNextFrame,
    /// so CPU writes to per-frame buffers during this frame's Update phase never race with GPU reads.
    /// Equivalent to DX Device.BeforeRender.
    /// Returning false means the swapchain is still OutOfDate, for example during interactive dragging, minimization, or ResizeSemaphore timeout,
    /// or that the surface was lost as the app moves to the background.
    /// In that case the whole frame must be skipped, with no Draw and no AfterRender, and the next loop retries using the latest size.
    /// </summary>
    internal static bool BeforeRender()
    {
        // Hold exclusive GPU access for the whole frame. Everything the frame records - descriptor sets it binds,
        // buffers and images it references, the command pool it resets - is then guaranteed untouched by the background
        // loading thread until AfterRender completes.
        EnterGpuAccess();

        bool begun;
        try
        {
            begun = BeforeRenderCore();
        }
        catch
        {
            ExitGpuAccess();
            throw;
        }

        // A skipped frame never reaches AfterRender, so the gate is released here.
        if (!begun)
            ExitGpuAccess();

        return begun;
    }

    static bool BeforeRenderCore()
    {
        var frame = FrameContexts[FrameIndex];

        // 1) acquire swapchain image
        var acquireResult = SwapChain.AcquireNextImage(frame.ImageAvailable, out uint imageIndex);
        if (acquireResult != _lastAcquireResult)
        {
            // Log only when the state changes.
            // Suboptimal may be returned every frame for a long time, so avoid flooding.
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] Acquire result changed: {_lastAcquireResult} -> {acquireResult}");
            _lastAcquireResult = acquireResult;
        }
        if (acquireResult == Result.ErrorOutOfDateKhr)
        {
            // This means the swapchain must be recreated.
            // The caller, LinuxApp, should invoke HandleResize from SDL_EVENT_WINDOW_RESIZED.
            HandleResize(_backbufferWidth, _backbufferHeight);
            // After HandleResize, the current slot's FrameContext must be fetched again,
            // or the command buffer used by BeforeRender begin would no longer match the one ended and submitted by AfterRender.
            frame = FrameContexts[FrameIndex];
            acquireResult = SwapChain.AcquireNextImage(frame.ImageAvailable, out imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                // The surface changed again during interactive dragging,
                // or HandleResize skipped actual recreation because the window was minimized, w or h <= 0,
                // or ResizeSemaphore timed out.
                // OutOfDate is recoverable by spec and may appear repeatedly, so throwing here would be wrong.
                // Skip this frame and retry next frame.
                // When acquire fails, ImageAvailable is not signaled, no command buffer has been begun yet,
                // and FrameIndex has not advanced, so returning directly does not corrupt frame state.
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] Acquire still OutOfDate after resize, skip frame");
                return false;
            }
            if (acquireResult == Result.ErrorSurfaceLostKhr)
            {
                // The surface died while the swapchain was being rebuilt, for example because the
                // app was backgrounded again during the resize. ImageAvailable is not signaled, so
                // the frame must be skipped; OnSurfaceLost and the next SurfaceCreated converge.
                DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] Acquire SurfaceLost after resize, skip frame");
                return false;
            }
            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
                throw new VulkanCallException("vkAcquireNextImageKHR / resize", acquireResult);
        }
        else if (acquireResult == Result.ErrorSurfaceLostKhr)
        {
            // The surface is being destroyed, typically because Android tears it down as the app
            // moves to the background. ImageAvailable is not signaled, so the frame must be skipped:
            // AfterRender would otherwise submit a wait on an unsignaled semaphore and can hang the
            // queue. OnSurfaceLost releases the swapchain and the next SurfaceCreated soft-restarts
            // it; the result change itself is already logged above.
            return false;
        }

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            throw new VulkanCallException("vkAcquireNextImageKHR", acquireResult);

        frame.SetRenderTarget(SwapChain.Images[imageIndex], SwapChain.ImageViews[imageIndex], Display.Framebuffers[imageIndex]);

        // New frame begins:
        // reset the backbuffer-written flag so the first pass clears and later passes load.
        _backbufferWrittenThisFrame = false;

        // 3B: the frame-start refresh of bindings 16-20/25 is gone. Those six view/frame-level lighting inputs now
        // live in the per-view globals set 3, which Pipeline.PrepareViewGlobals writes for the current frame slot
        // (that slot's previous submission is already retired, so no in-flight command buffer is ever rewritten).

        // 3) Reset cmd pool + begin cmd buffer
        frame.Reset();
        GraphicsCommandBuffer = frame.CommandList;
        IsRecording = true;

        // 3.5) Drain deferred texture layout transitions.
        // This must happen outside passes before any CmdBeginRenderPass.
        if (_pendingTextureTransitions.Count > 0)
        {
            foreach (var tex in _pendingTextureTransitions)
                tex.EnsureReadyForRendering(GraphicsCommandBuffer);
            _pendingTextureTransitions.Clear();
        }

        return true;
    }

    // -- Offscreen RenderTarget, step 2 and 3 --

    /// <summary>Registry of created offscreen RTs. During resize, members with MatchBackbufferSize are recreated in place. Accessed only by the render thread.</summary>
    internal static readonly List<VKRenderTarget> OffscreenTargets = new();

    // Each attachment combination and Load/Store intent has its own cache; switching the fog-composite target within a frame must not destroy the previous stage's framebuffer.
    // RTs proactively invalidate the cache before Resize/Dispose rather than relying on reusable native view handles.
    readonly record struct ScenePassKey(VKRenderTarget Color, VKRenderTarget? Depth,
        VKRenderTarget? Velocity, VKRenderTarget? Normal, bool ClearColor, bool ClearDepth, bool StoreDepth);
    static readonly Dictionary<ScenePassKey, (RenderPass Pass, Framebuffer Framebuffer)> _sceneTargetPasses = new();

    // -- Phase 4: Outline2D mask pass, with mask RT color=BackbufferCompatible plus shared SceneDepth.
    //    The render pass is created lazily once because its format is fixed and also serves as the anchor for baking mask PSOs.
    //    The framebuffer combines maskRT.ColorView and depthRT.DepthView and is lazily rebuilt when views change after resize.
    //    The identity of the depth RT is cached as well because the runtime may switch shape.
    //    Accessed only by the render thread. --
    static RenderPass _outlineMaskRenderPass;

    static Framebuffer _outlineMaskFramebuffer;

    /// <summary>Cached color-view version for the mask framebuffer. See ColorViewVersion and Device.NextViewVersion.</summary>
    static ulong _outlineMaskColorVersion;

    /// <summary>Cached depth-RT reference for the mask framebuffer. Null means not cached yet. Framebuffer rebuilds compare by reference equality.</summary>
    static VKRenderTarget? _outlineMaskDepthRT;

    /// <summary>Cached depth-view version for the mask framebuffer.</summary>
    static ulong _outlineMaskDepthVersion;

    // -- D1 screen-space decal pass.
    //    The render pass is determined solely by "whether a normal attachment is present", giving two shapes total; it is created once and reused permanently, and also serves as the baking anchor for decal PSOs;
    //    the framebuffer binds the current frame's SceneColor (and optional SceneNormal) and is rebuilt when the view version changes.
    //    As with OutlineMask, RT identity is compared by reference equality because a window rebuild replaces the native view.
    //    Accessed only by the render thread. --
    static RenderPass _decalRenderPass;

    static RenderPass _decalRenderPassNormal;

    static Framebuffer _decalFramebuffer;

    static VKRenderTarget? _decalColorRT;

    static VKRenderTarget? _decalNormalRT;

    static ulong _decalColorVersion;

    static ulong _decalNormalVersion;

    /// <summary>
    /// Gets the decal pass render pass. The backend anchors on it when baking PSOs, and <see cref="BeginPass"/> uses the same object to build the framebuffer;
    /// the two shapes are cached separately and are not destroyed after being returned (the same anchor contract as OutlineMask).
    /// </summary>
    internal static RenderPass GetDecalRenderPass(bool withNormal)
    {
        if (withNormal)
        {
            if (_decalRenderPassNormal.Handle == 0)
                _decalRenderPassNormal = VKRenderTarget.CreateDecalRenderPassForFormat(Format.R16G16B16A16Sfloat, true);
            return _decalRenderPassNormal;
        }
        if (_decalRenderPass.Handle == 0)
            _decalRenderPass = VKRenderTarget.CreateDecalRenderPassForFormat(Format.R16G16B16A16Sfloat, false);
        return _decalRenderPass;
    }

    /// <summary>
    /// Prepares the decal pass render pass and framebuffer. The color attachment is the current frame's SceneColor,
    /// and the normal attachment is determined by whether roughness is written this frame — it is color attachment 1, not a sampling source.
    /// </summary>
    static (RenderPass, Framebuffer) EnsureDecalPass(VKRenderTarget color, VKRenderTarget? normal)
    {
        if (color.ColorView.Handle == 0 || color.Width == 0 || color.Height == 0
            || (normal != null && (normal.ColorView.Handle == 0
                || normal.Width != color.Width || normal.Height != color.Height
                || normal.ColorFormat != Format.R16G16B16A16Sfloat)))
            throw new InvalidOperationException("The decal pass attachments are invalid or the size/format does not match.");

        bool withNormal = normal != null;
        var renderPass = GetDecalRenderPass(withNormal);
        ulong normalVersion = normal?.ColorViewVersion ?? 0;
        if (_decalFramebuffer.Handle == 0 || !ReferenceEquals(_decalColorRT, color)
            || !ReferenceEquals(_decalNormalRT, normal)
            || _decalColorVersion != color.ColorViewVersion || _decalNormalVersion != normalVersion)
        {
            RetireFramebuffer(_decalFramebuffer);

            var attachments = stackalloc Silk.NET.Vulkan.ImageView[2] { color.ColorView, normal?.ColorView ?? color.ColorView };
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = withNormal ? 2u : 1u,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1
            };
            if (Vk.CreateFramebuffer(LogicalDevice, in fbInfo, null, out var fb) != Result.Success)
                throw new Exception("vkCreateFramebuffer (decal) failed");
            _decalFramebuffer = fb;
            _decalColorRT = color;
            _decalNormalRT = normal;
            _decalColorVersion = color.ColorViewVersion;
            _decalNormalVersion = normalVersion;
        }
        return (renderPass, _decalFramebuffer);
    }

    /// <summary>
    /// Create an offscreen RenderTarget.
    /// Two shapes are supported, step 3 and aligned with DX Device.CreateRenderTarget:
    /// - color-only, BackbufferCompatible or Rgba16Float, for Scene and Post output, with matching depth provided internally on Vulkan
    /// - depth-only, D32Float shadow map, used both as an attachment and as a sampled texture with no typeless intermediate
    /// A color plus private-depth combination currently has no consumer and is not supported.
    /// Size comes either from MatchBackbufferSize or from fixed Width and Height.
    /// </summary>
    internal static Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc)
    {
        bool hasColor = desc.ColorFormat != Season.Rendering.RtFormat.None;
        bool hasDepth = desc.DepthFormat != Season.Rendering.RtFormat.None;

        if (hasColor == hasDepth)
            throw new NotSupportedException("[CreateRenderTarget] Only color-only or depth-only shapes are supported. The color plus private-depth combination currently has no consumer.");
        if (hasDepth && desc.DepthFormat != Season.Rendering.RtFormat.D32Float)
            throw new NotSupportedException($"[CreateRenderTarget] Only D32Float is supported as the depth format, got {desc.DepthFormat}.");
        if (desc.SampleCount > 1)
            throw new NotSupportedException("VK offscreen RT does not support MSAA. The Msaa4x tier keeps its multisampled pair private to the scene target (see VKRenderTarget.EnsureMsaaScenePass); every target the shared layer sees is single-sampled.");

        uint width = desc.MatchBackbufferSize ? (uint)_backbufferWidth : desc.Width;
        uint height = desc.MatchBackbufferSize ? (uint)_backbufferHeight : desc.Height;

        var rt = new VKRenderTarget(desc, width, height);
        OffscreenTargets.Add(rt);
        return rt;
    }

    /// <summary>
    /// Sample an offscreen RT to screen inside the FinalBlit pass using a full-screen triangle.
    /// Equivalent to DX Device.BlitToBackbuffer.
    /// Sampling-state transitions are already handled by the offscreen render pass through finalLayout and subpass dependencies, so this path uses zero explicit barriers.
    /// When source size does not match the backbuffer, for example fractional-resolution Post output, it automatically switches to linear upsampling.
    /// When the source is an HDR RT, Rgba16Float, it automatically switches to the tonemap variant, step B of 1-4, exposure push constant plus ACES plus gamma.
    /// In step D of 2-1, when bloomTex is non-null and ready, and tonemap is required, it switches to the tonemap-plus-bloom variant.
    /// Dispatch has already transitioned it into sampling state, so this path still uses zero barriers.
    /// When resolve is not Copy, where the source is the LDR PostColor output of the Post uber pass and luma is stored in alpha,
    /// it switches to the matching resolve present path instead, FXAA for the 2-1 tier and RCAS for clause 17 of 2-3,
    /// with texel size pushed every frame and no resize-time rebuild needed.
    /// This is mutually exclusive with tonemap and bloom because composition already finished in Post.
    /// Under contract clause 12 of 2-3, when sceneTex is non-null and ready, the TAA resolve output becomes the scene source instead.
    /// Variant selection is still decided from srcRT description.
    /// That storage texture matches SceneColor in size and rgba16float format, and TaaEffect already self-bypasses instead of publishing on size mismatch.
    /// This mirrors DX Device.BlitToBackbuffer.
    /// </summary>
    internal static void BlitToBackbuffer(Season.Rendering.RenderTarget source, Texture? bloomTex = null,
        Season.Rendering.PostResolve resolve = Season.Rendering.PostResolve.Copy, Texture? aoTex = null,
        Texture? sceneTex = null, VKRenderTarget? outlineMask = null, float outlineWidth = 0f)
    {
        var rt = (VKRenderTarget)source;

        if (resolve != Season.Rendering.PostResolve.Copy)
        {
            if (resolve == Season.Rendering.PostResolve.Rcas)
                BlitPipeline.RecordRcas(GraphicsCommandBuffer, rt.SampleDescriptorSet, 1f / rt.Width, 1f / rt.Height,
                    RenderQuality.Current.TaaSharpness);
            else
                BlitPipeline.RecordFxaa(GraphicsCommandBuffer, rt.SampleDescriptorSet, 1f / rt.Width, 1f / rt.Height);
            if (outlineMask != null)
                BlitPipeline.RecordOutlineComposite(GraphicsCommandBuffer, outlineMask.SampleDescriptorSet,
                    1f / outlineMask.Width, 1f / outlineMask.Height, outlineWidth);
            return;
        }

        bool linear = rt.Width != SwapChain.Extent.Width || rt.Height != SwapChain.Extent.Height;
        bool tonemap = rt.Desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float;
        bool bloom = tonemap && bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
        bool ao = tonemap && aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
        var sceneSet = sceneTex != null && System.Threading.Volatile.Read(ref sceneTex.Ready)
            ? BlitPipeline.GetSceneDescriptor(sceneTex)
            : rt.SampleDescriptorSet;
        BlitPipeline.Record(GraphicsCommandBuffer, sceneSet, linear, tonemap,
            bloom ? BlitPipeline.GetBloomDescriptor(bloomTex!) : default, bloom,
            ao ? BlitPipeline.GetAoDescriptor(aoTex!) : default, ao);
        // Phase 4:
        // append Outline2D composition after scene blit inside the same pass, blending to screen with SrcAlpha.
        if (outlineMask != null)
            BlitPipeline.RecordOutlineComposite(GraphicsCommandBuffer, outlineMask.SampleDescriptorSet,
                1f / outlineMask.Width, 1f / outlineMask.Height, outlineWidth);
    }

    /// <summary>
    /// Step D of 2-1: Post-pass body invoked by FrameSchedule.RenderPost.
    /// Uber composition tonemaps the source SceneColor, with optional bloom, into the currently bound LDR PostColor and bakes luma into alpha so FXAA can skip recomputation.
    /// Input sampling state has already been closed by Scene render-pass finalLayout and dispatch post-state, so this path also uses zero barriers, equivalent to DX Device.RenderPostUber.
    /// In step C of 2-2, when aoTex is non-null and ready, it additionally uses the uber AO variant with the same composition formula as BlitToBackbuffer.
    /// Under contract clause 12 of 2-3, sceneTex replaces the scene source at the same point.
    /// </summary>
    internal static void RenderPostUber(Season.Rendering.RenderTarget sceneColor, Texture? bloomTex = null, Texture? aoTex = null,
        Texture? sceneTex = null)
    {
        var rt = (VKRenderTarget)sceneColor;
        bool bloom = bloomTex != null && System.Threading.Volatile.Read(ref bloomTex.Ready);
        bool ao = aoTex != null && System.Threading.Volatile.Read(ref aoTex.Ready);
        var sceneSet = sceneTex != null && System.Threading.Volatile.Read(ref sceneTex.Ready)
            ? BlitPipeline.GetSceneDescriptor(sceneTex)
            : rt.SampleDescriptorSet;
        BlitPipeline.RecordUber(GraphicsCommandBuffer, sceneSet,
            bloom ? BlitPipeline.GetBloomDescriptor(bloomTex!) : default, bloom,
            ao ? BlitPipeline.GetAoDescriptor(aoTex!) : default, ao);
    }

    /// <summary>
    /// Begin a pass:
    /// resolve the target, either an offscreen VKRenderTarget, color or depth-only, or the backbuffer,
    /// then call CmdBeginRenderPass with clear values from desc,
    /// then set viewport and scissor from the target size.
    /// Equivalent to DX Device.BeginPass.
    /// Layout transitions are handled by render-pass initialLayout and finalLayout,
    /// and barriers remain forbidden inside the pass body, matching the shared PassDesc contract and VK being the strictest backend.
    /// Note:
    /// RenderPass load and store ops are already baked by pass shape,
    /// Scene and Shadow always clear,
    /// shadow depth stores,
    /// scene depth is DontCare.
    /// desc.ClearXxxEnable and StoreDepth do not participate in dynamic selection.
    /// Current slot declarations already match baked behavior.
    /// FinalBlit and Post may declare no clear, but they fully cover the target immediately after,
    /// and Clear is the optimal load op on tilers anyway, so behavior stays correct with no extra bandwidth cost.
    /// </summary>
    internal static void BeginPass(in Season.Rendering.PassDesc desc)
    {
        ActivePassId = desc.Id;
        PushDebugGroup(_passLabels[(int)desc.Id]);

        // Msaa4x invariant: the multisampled pair is backend-private, so a scene pass can never carry an explicit
        // depth / velocity / normal attachment (those are all single-sampled shared targets). Initialization enforces
        // this by dropping every feature that would create one; failing loudly here is better than silently baking
        // an incompatible render pass for the Msaa4x PSO family.
        if (MsaaSampleCount > 1 && IsMsaaScenePass(desc.Id)
            && (desc.DepthTarget != null || desc.VelocityTarget != null || desc.NormalTarget != null))
            throw new InvalidOperationException(
                $"[BeginPass] Msaa4x cannot attach an explicit depth/velocity/normal target to {desc.Id}; initialization must have dropped MotionVectors, AO, Decals, SSR and volumetric fog.");

        // Target resolution:
        // the triple-target path, SceneColor plus SceneVelocity plus explicit SceneDepth for 2-3, uses its dedicated render pass and framebuffer.
        // the dual-target path, SceneColor plus explicit SceneDepth for 2-2, uses its dedicated render pass and framebuffer.
        // a single offscreen RT, preferring the color slot and otherwise the depth-only shadow slot, uses its own render pass and framebuffer.
        // the backbuffer path, including FinalBlit, uses the Display render pass plus the current frame framebuffer.
        RenderPass renderPass;
        Framebuffer framebuffer;
        Extent2D extent;
        uint attachmentCount;
        bool hasVelocity = desc.VelocityTarget is VKRenderTarget;
        var target = (desc.ColorTarget ?? desc.DepthTarget) as VKRenderTarget;
        // These two fields record what "actually became an attachment", not what PassDesc requested:
        // below, only the three-target branch binds velocity as color attachment 1.
        ActiveDepthTarget = null;
        ActiveVelocityTarget = null;
        ActiveNormalTarget = null;
        var normalTarget = hasVelocity ? desc.NormalTarget as VKRenderTarget : null;
        if (desc.Id == Season.Rendering.RenderPassId.OutlineMask)
        {
            // Phase 4:
            // the Outline2D mask pass uses a dedicated render pass and framebuffer when mask RT shares SceneDepth in the AO tier.
            // Without shared depth, meaning SceneColor is using a private-depth shape, it falls back to the maskRT's own render pass and framebuffer.
            // That path clears depth to 1.0 and then LessEqual always passes, which means no occlusion,
            // matching the degraded semantics of DX MSAA4x.
            var maskRT = (VKRenderTarget)desc.ColorTarget!;
            var depthRT = desc.DepthTarget as VKRenderTarget;
            if (depthRT != null)
            {
                renderPass = EnsureOutlineMaskPass(maskRT, depthRT);
                framebuffer = _outlineMaskFramebuffer;
                ActiveDepthTarget = depthRT;
            }
            else
            {
                // The degraded path must still ensure that the mask PSO is baked.
                // Previously only the shared-depth path baked the mask PSO through EnsureOutlineMaskPass.
                // Missing that bake here would make SetPipeline bind an empty PSO with Handle==0.
                // Drivers would ignore it and leave the previous regular PSO active,
                // producing undefined behavior where a fully shaded fragment shader writes into the mask.
                // This is unrelated to the white-outline root cause, which came from the FS push-constant range base in Pipeline.CreatePipelineLayout,
                // but it is another correctness hole that also had to be fixed.
                // maskRT.RenderPass keeps the same format and is preserved across Recreate,
                // so the bake anchor remains stable.
                // The existence of SceneDepth is fixed for a session at init time from the AO and MotionVectors tier,
                // so the two shapes never mix at runtime.
                Pipeline.EnsureOutlineMaskPipelines(maskRT.RenderPass);
                renderPass = maskRT.RenderPass;
                framebuffer = maskRT.Framebuffer;
            }
            extent = new Extent2D(maskRT.Width, maskRT.Height);
            attachmentCount = 2u;
        }
        else if (desc.ColorTarget is VKRenderTarget compositeRT
            && desc.Id is Season.Rendering.RenderPassId.FogComposite or Season.Rendering.RenderPassId.WeatherComposite)
        {
            (renderPass, framebuffer) = EnsureSceneTargetPass(compositeRT, null, null, null, true, false, false);
            extent = new Extent2D(compositeRT.Width, compositeRT.Height);
            attachmentCount = 1;
        }
        else if (desc.Id == Season.Rendering.RenderPassId.SceneDecal && desc.ColorTarget is VKRenderTarget decalColor)
        {
            // D1: the decal pass has no depth attachment (depth is a sampling source) and does not clear color — it multiplies/adds into the result of the previous scene pass.
            // s9 only binds SceneNormal as color attachment 1 when it actually writes roughness; this is the only attachment scheme in the whole project
            // where "the normal appears but does not appear together with velocity"; the decal does not write velocity, nor does it touch the values written during the opaque stage.
            var decalNormal = desc.NormalTarget as VKRenderTarget;
            (renderPass, framebuffer) = EnsureDecalPass(decalColor, decalNormal);
            extent = new Extent2D(decalColor.Width, decalColor.Height);
            attachmentCount = decalNormal != null ? 2u : 1u;
            ActiveNormalTarget = decalNormal;
        }
        else if (desc.Id is Season.Rendering.RenderPassId.SceneWater or Season.Rendering.RenderPassId.SceneWaterfall
            && desc.ColorTarget is VKRenderTarget waterColor)
        {
            // P2: the water surface pass is shaped the same as the decal — no depth attachment (depth is a sampling source) and no color clear.
            // The only thing bound to the color attachment is a refraction snapshot, which is a sampling source already copied outside the pass.
            // P1 water curtain: exactly the same attachment shape, only without the refraction snapshot — so it reuses the very
            // same render pass object, which is also what its PSO was baked against.
            (renderPass, framebuffer) = EnsureWaterPass(waterColor);
            extent = new Extent2D(waterColor.Width, waterColor.Height);
            attachmentCount = 1u;
        }
        else if (desc.Id == Season.Rendering.RenderPassId.SsrComposite && desc.ColorTarget is VKRenderTarget ssrColor)
        {
            // P0: SSR composite is shaped the same as the water surface — single color attachment, Load, no depth. The incremental texture is a sampling source,
            // and the color attachment is SceneColor itself, so we absolutely must not clear here.
            (renderPass, framebuffer) = EnsureSsrCompositePass(ssrColor);
            extent = new Extent2D(ssrColor.Width, ssrColor.Height);
            attachmentCount = 1u;
        }
        else if (MsaaSampleCount > 1 && IsMsaaScenePass(desc.Id)
            && desc.ColorTarget is VKRenderTarget msaaColor
            && desc.DepthTarget == null && desc.VelocityTarget == null && desc.NormalTarget == null)
        {
            // Msaa4x scene tier: Scene / SceneOpaque / SceneTransparent render into the target's private
            // multisampled colour + depth pair and resolve into its single-sampled colour at the end of the pass.
            // Initialization makes the shape unambiguous by construction: MotionVectors (and the normals that
            // ride on it), AO, Decals, SSR and volumetric fog are all mutually exclusive with Msaa4x, so the
            // explicit depth / velocity / normal slots are always absent, and SceneDepth is never created.
            // The clear/load split mirrors the 1x path: the opaque stage (or the unsegmented Scene pass) starts
            // a new multisampled image, the transparent stage loads what it left behind in the same image.
            if (!msaaColor.EnsureMsaaScenePass(desc.ClearColorEnable, desc.ClearDepthEnable, out renderPass, out framebuffer))
                throw new InvalidOperationException(
                    $"[BeginPass] Msaa4x is active but the scene target cannot host a multisampled pass (hasColor={msaaColor.HasColor}, {msaaColor.Width}x{msaaColor.Height}).");
            extent = new Extent2D(msaaColor.Width, msaaColor.Height);
            // Only the multisampled colour and depth take clear values; the resolve slot is written by the resolve
            // itself and its load/store ops are ignored, so the clear-value count stays at two.
            attachmentCount = 2u;
        }
        else if (desc.ColorTarget is VKRenderTarget colorRT && desc.DepthTarget is VKRenderTarget depthRT)
        {
            (renderPass, framebuffer) = EnsureSceneTargetPass(colorRT, depthRT,
                desc.VelocityTarget as VKRenderTarget, normalTarget,
                desc.ClearColorEnable, desc.ClearDepthEnable, desc.StoreDepth);
            extent = new Extent2D(colorRT.Width, colorRT.Height);
            attachmentCount = hasVelocity ? (normalTarget != null ? 4u : 3u) : 2u;
            ActiveDepthTarget = depthRT;
            ActiveVelocityTarget = desc.VelocityTarget as VKRenderTarget;
            ActiveNormalTarget = normalTarget;
        }
        else if (target != null)
        {
            renderPass = target.RenderPass;
            framebuffer = target.Framebuffer;
            extent = new Extent2D(target.Width, target.Height);
            attachmentCount = 2u; // color + depth
            // When color is absent, target is that depth image (Shadow), which really is this pass's depth attachment;
            // otherwise the RT's own implicit depth is used, and there is no explicit depth RT to record.
            if (desc.ColorTarget == null) ActiveDepthTarget = target;
        }
        else
        {
            renderPass = _backbufferWrittenThisFrame ? Display.RenderPassLoad : Display.RenderPass;
            _backbufferWrittenThisFrame = true;
            framebuffer = FrameContexts[FrameIndex].Framebuffer;
            extent = SwapChain.Extent;
            attachmentCount = 2u; // color + depth
        }

        // Clear color, clear depth, and clear velocity, matching the LoadOp.Clear baked into the render pass.
        // A depth-only render pass has only one attachment, and attachment 0 is depth.
        // In step A of 1-4, clear colors for HDR targets, Rgba16Float, are linearized.
        // This is the inverse of the pow(1/2.2) used by FinalBlit tonemap variants,
        // so background appearance matches the LDR baseline, while the LDR path passes through unchanged.
        bool depthOnly = target != null && !target.HasColor;
        var cc = (target != null && target.Desc.ColorFormat == Season.Rendering.RtFormat.Rgba16Float)
            ? LinearizeClearColor(desc.ClearColor)
            : desc.ClearColor;
        // 2-3:
        // the triple-target render pass has 3 attachments, color, velocity, and depth, so it needs 3 clear values.
        // Velocity is always cleared to zero, meaning pixels not covered by geometry are treated as a static background.
        // A0.2: the four-target shape inserts the normal attachment at index 2, also cleared to zero.
        var clearValues = stackalloc ClearValue[4];
        uint clearCount;
        if (desc.Id is Season.Rendering.RenderPassId.SceneDecal or Season.Rendering.RenderPassId.SceneWater
            or Season.Rendering.RenderPassId.SsrComposite or Season.Rendering.RenderPassId.SceneWaterfall)
        {
            // These passes' attachments are all Load, so no ClearValue is needed at all.
            clearCount = 0u;
        }
        else if (depthOnly)
        {
            clearValues[0] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
            clearCount = 1u;
        }
        else if (hasVelocity)
        {
            clearValues[0] = new ClearValue { Color = new ClearColorValue(cc.X, cc.Y, cc.Z, cc.W) };
            clearValues[1] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
            uint depthIndex = 2u;
            if (attachmentCount == 4u)
                clearValues[depthIndex++] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
            clearValues[depthIndex] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
            clearCount = depthIndex + 1;
        }
        else
        {
            clearValues[0] = new ClearValue { Color = new ClearColorValue(cc.X, cc.Y, cc.Z, cc.W) };
            clearValues[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
            clearCount = attachmentCount;
        }
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(default, extent),
            ClearValueCount = clearCount,
            PClearValues = clearValues
        };
        Vk.CmdBeginRenderPass(GraphicsCommandBuffer, in rpBegin, SubpassContents.Inline);
        InRenderPass = true;
        ActiveColorTarget = desc.ColorTarget as VKRenderTarget;

        // Set viewport and scissor from target size, using negative-height Y flip under the same convention as Display.UpdateViewportAndScissor.
        var vp = new Silk.NET.Vulkan.Viewport
        {
            X = 0,
            Y = extent.Height,
            Width = extent.Width,
            Height = -(float)extent.Height,
            MinDepth = 0f,
            MaxDepth = 1f
        };
        Vk.CmdSetViewport(GraphicsCommandBuffer, 0, 1, in vp);
        var sc = new Rect2D(default, extent);
        Vk.CmdSetScissor(GraphicsCommandBuffer, 0, 1, in sc);
    }

    static (RenderPass, Framebuffer) EnsureSceneTargetPass(VKRenderTarget color, VKRenderTarget? depth,
        VKRenderTarget? velocity, VKRenderTarget? normal, bool clearColor, bool clearDepth, bool storeDepth)
    {
        if (color.ColorView.Handle == 0 || color.Width == 0 || color.Height == 0
            || (depth != null && (depth.DepthView.Handle == 0 || depth.Width != color.Width || depth.Height != color.Height))
            || (velocity != null && (depth == null || velocity.ColorView.Handle == 0
                || velocity.Width != color.Width || velocity.Height != color.Height
                || velocity.ColorFormat != Format.R16G16Sfloat))
            || (normal != null && (velocity == null || normal.ColorView.Handle == 0
                || normal.Width != color.Width || normal.Height != color.Height
                || normal.ColorFormat != Format.R16G16B16A16Sfloat)))
            throw new InvalidOperationException("The scene attachments are invalid or the size/format does not match.");

        var key = new ScenePassKey(color, depth, velocity, normal, clearColor, clearDepth, storeDepth);
        if (_sceneTargetPasses.TryGetValue(key, out var cached)) return cached;
        var rp = depth == null
            ? VKRenderTarget.CreateColorRenderPassForFormat(color.ColorFormat, colorOnly: true)
            : velocity != null
                ? VKRenderTarget.CreateVelocityRenderPassForFormat(color.ColorFormat, clearColor, clearDepth, storeDepth,
                    withNormal: normal != null)
                : VKRenderTarget.CreateDualTargetRenderPassForFormat(color.ColorFormat, clearColor, clearDepth, storeDepth);
        Framebuffer framebuffer = default;
        try
        {
            var attachments = stackalloc Silk.NET.Vulkan.ImageView[4];
            attachments[0] = color.ColorView;
            uint count = 1;
            if (velocity != null) attachments[count++] = velocity.ColorView;
            if (normal != null) attachments[count++] = normal.ColorView;
            if (depth != null) attachments[count++] = depth.DepthView;
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = rp,
                AttachmentCount = count,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1
            };
            if (Vk.CreateFramebuffer(LogicalDevice, in info, null, out framebuffer) != Result.Success)
                throw new Exception("vkCreateFramebuffer (scene attachments) failed");
            _sceneTargetPasses.Add(key, (rp, framebuffer));
            return (rp, framebuffer);
        }
        catch
        {
            if (framebuffer.Handle != 0) Vk.DestroyFramebuffer(LogicalDevice, framebuffer, null);
            Vk.DestroyRenderPass(LogicalDevice, rp, null);
            throw;
        }
    }

    /// <summary>
    /// P2 water surface pass. The render pass has only one shape (single color attachment) and is baked once and reused permanently;
    /// the framebuffer binds the current frame's SceneColor and is rebuilt when the view version changes.
    /// It is cached separately from the decal to avoid the two tearing down and rebuilding each other's framebuffer when alternately attaching/not attaching the normal attachment.
    /// Accessed only by the render thread.
    /// </summary>
    static RenderPass _waterRenderPass;

    static Framebuffer _waterFramebuffer;

    static VKRenderTarget? _waterColorRT;

    static ulong _waterColorVersion;

    /// <summary>
    /// Gets the water surface pass render pass. The backend anchors on it when baking PSOs, and <see cref="BeginPass"/> uses the same object to build the framebuffer;
    /// it is not destroyed after being returned (the same anchor contract as the decal and OutlineMask).
    /// </summary>
    internal static RenderPass GetWaterRenderPass()
    {
        if (_waterRenderPass.Handle == 0)
            _waterRenderPass = VKRenderTarget.CreateWaterRenderPassForFormat(Format.R16G16B16A16Sfloat);
        return _waterRenderPass;
    }

    /// <summary>
    /// Prepares the water surface pass render pass and framebuffer. The color attachment is the current frame's SceneColor:
    /// there is no depth attachment (both depth and the refraction snapshot are sampling sources), and no color clear.
    /// </summary>
    static (RenderPass, Framebuffer) EnsureWaterPass(VKRenderTarget color)
    {
        if (color.ColorView.Handle == 0 || color.Width == 0 || color.Height == 0)
            throw new InvalidOperationException("The water surface pass attachments are invalid.");

        var renderPass = GetWaterRenderPass();
        if (_waterFramebuffer.Handle == 0 || !ReferenceEquals(_waterColorRT, color)
            || _waterColorVersion != color.ColorViewVersion)
        {
            RetireFramebuffer(_waterFramebuffer);

            var attachments = stackalloc Silk.NET.Vulkan.ImageView[1] { color.ColorView };
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1
            };
            if (Vk.CreateFramebuffer(LogicalDevice, in fbInfo, null, out var fb) != Result.Success)
                throw new Exception("vkCreateFramebuffer (water) failed");
            _waterFramebuffer = fb;
            _waterColorRT = color;
            _waterColorVersion = color.ColorViewVersion;
        }
        return (renderPass, _waterFramebuffer);
    }

    /// <summary>
    /// P0 SSR composite pass render pass (single color attachment, Load, no depth). It is anchored on when baking PSOs,
    /// and <see cref="BeginPass"/> uses the same object to build the framebuffer, the same anchor contract as the water surface, decal, and OutlineMask.
    /// </summary>
    static RenderPass _ssrCompositeRenderPass;

    static Framebuffer _ssrCompositeFramebuffer;

    static VKRenderTarget? _ssrCompositeColorRT;

    static ulong _ssrCompositeColorVersion;

    internal static RenderPass GetSsrCompositeRenderPass()
    {
        if (_ssrCompositeRenderPass.Handle == 0)
            _ssrCompositeRenderPass = VKRenderTarget.CreateSsrCompositeRenderPassForFormat(Format.R16G16B16A16Sfloat);
        return _ssrCompositeRenderPass;
    }

    /// <summary>
    /// Prepares the SSR composite render pass and framebuffer. The color attachment is the current frame's SceneColor:
    /// there is no depth attachment and no color clear — the reflection increment is added on top, so clearing would leave nothing behind.
    /// It is cached separately from the water surface to avoid two passes that both bind SceneColor tearing down each other's framebuffer every frame.
    /// </summary>
    static (RenderPass, Framebuffer) EnsureSsrCompositePass(VKRenderTarget color)
    {
        if (color.ColorView.Handle == 0 || color.Width == 0 || color.Height == 0)
            throw new InvalidOperationException("The SSR composite pass attachments are invalid.");

        var renderPass = GetSsrCompositeRenderPass();
        if (_ssrCompositeFramebuffer.Handle == 0 || !ReferenceEquals(_ssrCompositeColorRT, color)
            || _ssrCompositeColorVersion != color.ColorViewVersion)
        {
            RetireFramebuffer(_ssrCompositeFramebuffer);

            var attachments = stackalloc Silk.NET.Vulkan.ImageView[1] { color.ColorView };
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1
            };
            if (Vk.CreateFramebuffer(LogicalDevice, in fbInfo, null, out var fb) != Result.Success)
                throw new Exception("vkCreateFramebuffer (ssr composite) failed");
            _ssrCompositeFramebuffer = fb;
            _ssrCompositeColorRT = color;
            _ssrCompositeColorVersion = color.ColorViewVersion;
        }
        return (renderPass, _ssrCompositeFramebuffer);
    }

    /// <summary>
    /// P2: copies an offscreen color target as-is into another target of the same shape, so that water refraction can obtain a samplable background
    /// in the frame where "SceneColor is still a color attachment".
    ///
    /// Vulkan has no one-liner equivalent of CopyResource, but <c>vkCmdCopyImage</c> on the same format and size
    /// likewise bypasses filtering and needs no extra pipeline; both sides are 1 layer and 1 mip, so the region is the whole image.
    ///
    /// The destination always declares <c>Undefined</c> as its old layout: it is used only as a copy destination and a sampling source, never as an attachment,
    /// and its content is fully overwritten every frame, so "old content is discardable" is precisely the truth. This also saves a
    /// layout record table that would become invalid after renaming — the source stage still fully covers the previous frame's sampling read and the previous pass's color write (WAR/WAW),
    /// so this is not a lazy transition that "skips synchronization".
    /// </summary>
    internal static bool CopyRenderTarget(Season.Rendering.RenderTarget source, Season.Rendering.RenderTarget destination)
    {
        if (InRenderPass || GraphicsCommandBuffer.Handle == 0) return false;
        if (source is not VKRenderTarget src || destination is not VKRenderTarget dst) return false;
        if (ReferenceEquals(src, dst) || !src.HasColor || !dst.HasColor) return false;
        if (src.ColorFormat != dst.ColorFormat) return false;
        if (src.Width == 0 || src.Width != dst.Width || src.Height != dst.Height) return false;
        if (src.ColorImage.Handle == 0 || dst.ColorImage.Handle == 0) return false;

        var command = GraphicsCommandBuffer;
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1
        };

        // The source is the ShaderReadOnlyOptimal published by the previous pass, and after the copy it must be sent back along the same route — the water surface render pass
        // uses this layout as its initialLayout, so missing this return would report a layout mismatch in the validation layer.
        var barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = src.ColorImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.TransferReadBit
        };
        barriers[1] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = dst.ColorImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        Vk.CmdPipelineBarrier(command,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 2, barriers);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            SrcOffset = default,
            DstOffset = default,
            Extent = new Extent3D(src.Width, src.Height, 1)
        };
        Vk.CmdCopyImage(command, src.ColorImage, ImageLayout.TransferSrcOptimal,
            dst.ColorImage, ImageLayout.TransferDstOptimal, 1, in region);

        // The source returns to the color-attachment pre-state of the water surface; the destination transitions to the sampling layout and is read as the refraction source immediately after.
        barriers[0].OldLayout = ImageLayout.TransferSrcOptimal;
        barriers[0].NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barriers[0].SrcAccessMask = AccessFlags.TransferReadBit;
        barriers[0].DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit;
        barriers[1].OldLayout = ImageLayout.TransferDstOptimal;
        barriers[1].NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barriers[1].SrcAccessMask = AccessFlags.TransferWriteBit;
        barriers[1].DstAccessMask = AccessFlags.ShaderReadBit;
        Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            0, 0, null, 0, null, 2, barriers);
        return true;
    }

    /// <summary>
    /// Retires the framebuffer handle that a single-slot pass cache is about to replace. Never destroys immediately:
    /// one frame binds more than one scene view, and the decal, water, SSR and mask caches hold a single slot each, so the
    /// framebuffer replaced here is very likely the one bound by a pass recorded earlier in the command buffer that is
    /// still recording. Destroying it at that point puts the whole buffer into the invalid state
    /// (VUID-vkCmdBeginRenderPass-commandBuffer-recording: "objects bound to the command buffer were invalidated") and
    /// every later command of that frame is reported as well.
    /// Callers only swap the cached handle, so the new render pass and framebuffer take effect for the rest of the frame,
    /// and this destroy lands once the timeline fence passes the retire value captured now — the same contract as
    /// <see cref="VKRenderTarget.Dispose"/>.
    /// </summary>
    static void RetireFramebuffer(Framebuffer framebuffer)
    {
        if (framebuffer.Handle == 0)
            return;

        var vk = Vk;
        var device = LogicalDevice;
        EnqueueDeferredRelease(() => vk.DestroyFramebuffer(device, framebuffer, null));
    }

    /// <summary>RT destruction retires by fence; only the GPU-idle point of Resize allows immediate cleanup.</summary>
    internal static void ReleaseSceneTargetPasses(VKRenderTarget target, bool immediate = false)
    {
        // The decal pass framebuffer directly references the RT's view, so it must be released before rebuilding; otherwise it would leave a dangling attachment.
        if (ReferenceEquals(_decalColorRT, target) || ReferenceEquals(_decalNormalRT, target))
        {
            var decalFramebuffer = _decalFramebuffer;
            _decalFramebuffer = default;
            _decalColorRT = null;
            _decalNormalRT = null;
            _decalColorVersion = 0;
            _decalNormalVersion = 0;
            if (decalFramebuffer.Handle != 0)
            {
                var vk = Vk;
                var device = LogicalDevice;
                void ReleaseDecal() => vk.DestroyFramebuffer(device, decalFramebuffer, null);
                if (immediate) ReleaseDecal();
                else EnqueueDeferredRelease(ReleaseDecal);
            }
        }

        // The water surface pass framebuffer likewise references the RT's view, for the same reason as above.
        if (ReferenceEquals(_waterColorRT, target))
        {
            var waterFramebuffer = _waterFramebuffer;
            _waterFramebuffer = default;
            _waterColorRT = null;
            _waterColorVersion = 0;
            if (waterFramebuffer.Handle != 0)
            {
                var vk = Vk;
                var device = LogicalDevice;
                void ReleaseWater() => vk.DestroyFramebuffer(device, waterFramebuffer, null);
                if (immediate) ReleaseWater();
                else EnqueueDeferredRelease(ReleaseWater);
            }
        }
        foreach (var entry in _sceneTargetPasses.ToArray())
        {
            var key = entry.Key;
            if (!ReferenceEquals(key.Color, target) && !ReferenceEquals(key.Depth, target)
                && !ReferenceEquals(key.Velocity, target) && !ReferenceEquals(key.Normal, target)) continue;
            _sceneTargetPasses.Remove(key);
            var (rp, fb) = entry.Value;
            var vk = Vk;
            var device = LogicalDevice;
            void Release()
            {
                vk.DestroyFramebuffer(device, fb, null);
                vk.DestroyRenderPass(device, rp, null);
            }
            if (immediate) Release();
            else EnqueueDeferredRelease(Release);
        }
    }

    /// <summary>
    /// Phase 4:
    /// lazily prepare the render pass and framebuffer for the Outline2D mask pass.
    /// The render pass is created once and reused forever because attachment formats are fixed,
    /// color BackbufferCompatible plus depth DepthBufferFormat,
    /// and it also serves as the anchor for baking mask PSOs, which keeps Pipeline idempotent.
    /// The framebuffer is rebuilt whenever the views of mask RT or depth RT change during in-place resize recreation,
    /// assuming DeviceWaitIdle has already been performed, as described in the 2-2 note.
    /// In AO tiers, SceneDepth is a depth-only RT.
    /// The Scene pass leaves it in ShaderReadOnlyOptimal, while this render pass expects depth initialLayout to be an attachment state,
    /// so an explicit barrier must switch it back before CmdBeginRenderPass.
    /// That recording happens outside a pass, which is valid.
    /// </summary>
    static RenderPass EnsureOutlineMaskPass(VKRenderTarget maskRT, VKRenderTarget depthRT)
    {
        if (_outlineMaskRenderPass.Handle == 0)
        {
            _outlineMaskRenderPass = VKRenderTarget.CreateOutlineMaskRenderPassForFormat(maskRT.ColorFormat);
            Pipeline.EnsureOutlineMaskPipelines(_outlineMaskRenderPass);
        }

        if (_outlineMaskFramebuffer.Handle == 0 ||
            _outlineMaskColorVersion != maskRT.ColorViewVersion ||
            _outlineMaskDepthRT != depthRT ||
            _outlineMaskDepthVersion != depthRT.DepthViewVersion)
        {
            RetireFramebuffer(_outlineMaskFramebuffer);

            var attachments = stackalloc Silk.NET.Vulkan.ImageView[2] { maskRT.ColorView, depthRT.DepthView };
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _outlineMaskRenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = maskRT.Width,
                Height = maskRT.Height,
                Layers = 1
            };
            if (Vk.CreateFramebuffer(LogicalDevice, in fbInfo, null, out var fb) != Result.Success)
                throw new Exception("vkCreateFramebuffer (outline mask) failed");
            _outlineMaskFramebuffer = fb;
            _outlineMaskColorVersion = maskRT.ColorViewVersion;
            _outlineMaskDepthRT = depthRT;
            _outlineMaskDepthVersion = depthRT.DepthViewVersion;
        }

        // AO tier:
        // SceneDepth is a depth-only RT with no color attachment.
        // After the Scene pass it is in sampling state, so switch it back to attachment state for testing in this pass.
        if (!depthRT.HasColor)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = depthRT.DepthImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
            };
            Vk.CmdPipelineBarrier(
                GraphicsCommandBuffer,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                0, 0, null, 0, null, 1, in barrier);
        }

        return _outlineMaskRenderPass;
    }

    /// <summary>
    /// End the current pass by calling CmdEndRenderPass.
    /// Final attachment-state transitions are completed automatically through render-pass finalLayout.
    /// Equivalent to DX Device.EndPass.
    /// </summary>
    internal static void EndPass()
    {
        Vk.CmdEndRenderPass(GraphicsCommandBuffer);
        InRenderPass = false;
        ActivePassId = default;
        ActiveColorTarget = null;
        ActiveDepthTarget = null;
        ActiveVelocityTarget = null;
        ActiveNormalTarget = null;
        PopDebugGroup();
    }

    /// <summary>
    /// In 1-5, set viewport and scissor for one quadrant tile inside the shadow atlas, equivalent to DX SetShadowViewport.
    /// Uses negative-height Y flip under the same convention as BeginPass and Display.UpdateViewportAndScissor:
    /// Y starts at the bottom of the tile, y plus size, height is negative, and scissor remains a positive rectangle.
    /// </summary>
    internal static void SetShadowViewport(int x, int y, int size)
    {
        var vp = new Silk.NET.Vulkan.Viewport
        {
            X = x,
            Y = y + size,
            Width = size,
            Height = -size,
            MinDepth = 0f,
            MaxDepth = 1f
        };
        Vk.CmdSetViewport(GraphicsCommandBuffer, 0, 1, in vp);
        var sc = new Rect2D(new Offset2D(x, y), new Extent2D((uint)size, (uint)size));
        Vk.CmdSetScissor(GraphicsCommandBuffer, 0, 1, in sc);
    }

    /// <summary>Diagnostics:
    /// when SEASON_TRACE_ALL=1, write key native-call checkpoints directly to stdout.
    /// A native crash terminates the process immediately, and AddLog, which is only an in-memory Logs list, cannot preserve the scene after that.</summary>
    internal static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("SEASON_TRACE_ALL") == "1";

    static void Trace(string stage)
    {
        if (!TraceEnabled) return;
        Console.WriteLine($"[VKTRACE] {stage}");
        Console.Out.Flush();
    }

    /// <summary>
    /// End the frame:
    /// end the command buffer, submit it, waiting on ImageAvailable at ColorAttachmentOutput and signaling RenderFinished plus the timeline, then present.
    /// All passes have already been begun and ended by FrameSchedule before this point.
    /// The code is outside any pass here, so barriers in CaptureBackBuffer are valid.
    /// Equivalent to DX Device.AfterRender.
    /// </summary>
    internal static void AfterRender()
    {
        try
        {
            AfterRenderCore();
        }
        finally
        {
            // Releases the gate BeforeRender took, whether the frame completed or threw.
            ExitGpuAccess();
        }
    }

    /// <summary>
    /// True only between vkBeginCommandBuffer and vkEndCommandBuffer of the frame command buffer.
    /// Outside that window GraphicsCommandBuffer.Handle is still non-zero, so a handle check alone lets a caller
    /// record into an already-ended command buffer, which is undefined behaviour and leaves the padding of the
    /// driver's command stream in an unspecified state.
    /// </summary>
    internal static bool IsRecording;

    static void AfterRenderCore()
    {
        var frame = FrameContexts[FrameIndex];
        bool capturing = BaseApp.CaptureAppTcs != null;

        // Insert CaptureApp GPU readback after the last pass EndPass and before frame.End.
        if (capturing)
        {
            CaptureBackBuffer();
        }

        Trace("frame.End");
        frame.End();
        IsRecording = false;

        // Submit and signal both the binary RenderFinished semaphore for present and the completion value
        // for the next CPU wait on the same frame slot: through the timeline semaphore on 1.2 devices,
        // or through a registered VkFence on the 1.1 fallback. The completion-value contract is identical.
        _nextFenceValue++;
        ulong signalTimeline = _nextFenceValue;

        var imgAvail = frame.ImageAvailable;
        // RenderFinished is indexed by swapchain image. See the note on RecreateRenderFinishedSemaphores.
        var renderDone = _renderFinishedPerImage[SwapChain.CurrentImageIndex];

        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var cmd = frame.CommandList;

        Trace("QueueSubmit");
        if (GraphicsCommandQueue.TimelineMode)
        {
            ulong dummyWaitValue = 0;
            var timelineSem = GraphicsCommandQueue.TimelineSemaphore;

            var signalSems = stackalloc Silk.NET.Vulkan.Semaphore[2] { renderDone, timelineSem };
            var signalValues = stackalloc ulong[2] { 0, signalTimeline };

            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = &dummyWaitValue,
                SignalSemaphoreValueCount = 2,
                PSignalSemaphoreValues = signalValues
            };

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imgAvail,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
                SignalSemaphoreCount = 2,
                PSignalSemaphores = signalSems
            };

            GraphicsCommandQueue.CheckSubmit(Vk.QueueSubmit(GraphicsQueue, 1, in submit, default));
        }
        else
        {
            // 1.1 fallback: only the binary RenderFinished semaphore is signaled by the submission itself;
            // the completion value rides on a VkFence that PrepareSubmit registers before the submission,
            // so a concurrent WaitForFence(signalTimeline) can always find and wait on it.
            var signalSems = stackalloc Silk.NET.Vulkan.Semaphore[1] { renderDone };

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imgAvail,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = signalSems
            };

            var submitFence = GraphicsCommandQueue.PrepareSubmit(signalTimeline);
            GraphicsCommandQueue.CheckSubmit(Vk.QueueSubmit(GraphicsQueue, 1, in submit, submitFence));
        }

        _fenceValues[FrameIndex] = signalTimeline;

        // Present waits on RenderFinished, one semaphore per image.
        Trace("Present");
        var presentResult = SwapChain.Present(PresentQueue, renderDone, SwapChain.CurrentImageIndex);
        if (presentResult != _lastPresentResult)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] Present result changed: {_lastPresentResult} -> {presentResult}");
            _lastPresentResult = presentResult;
        }
        // Recreate only on OutOfDate.
        // Do not recreate on SuboptimalKhr.
        // This engine forces PreTransform=Identity and leaves rotation to the compositor,
        // so device rotation or free-resize windows may make the driver return Suboptimal every frame.
        // Recreating on that signal would create a storm of DeviceWaitIdle plus swapchain rebuilds every frame,
        // causing full-screen black and white flashing, while the rebuilt swapchain would still remain Suboptimal.
        // Suboptimal frames themselves are already rendered correctly and display normally.
        if (presentResult == Result.ErrorOutOfDateKhr)
        {
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] Present OutOfDate -> HandleResize {_backbufferWidth}x{_backbufferHeight}");
            HandleResize(_backbufferWidth, _backbufferHeight);
        }

        else if (presentResult != Result.Success && presentResult != Result.SuboptimalKhr)
            throw new VulkanCallException("vkQueuePresentKHR", presentResult);

        FrameIndex = (FrameIndex + 1) % frameCount;

        // Ring-fence step aligned with DX MoveToNextFrame:
        // before returning to the frame loop, where the next Update writes per-frame buffers,
        // wait for the previous GPU work on the next slot to complete.
        Trace("WaitForFence");
        if (_fenceValues[FrameIndex] != 0)
            GraphicsCommandQueue.WaitForFence(_fenceValues[FrameIndex]);

        // GPU progress has advanced enough, so dequeue and execute deferred releases that are now safe.
        Trace("PumpDeferredReleases");
        PumpDeferredReleases();

        // Complete the CaptureApp GPU readback, with all GPU commands for this frame already finished.
        CompleteCapture();
    }

    /// <summary>Recreate SwapChain plus Framebuffer and Depth after a window-size change. Equivalent to DX Device.HandleResize.
    /// Returning true means the recreation really happened and DeviceWaitIdle has already completed.
    /// Returning false means the operation was skipped, because the size was invalid or ResizeSemaphore timed out or threw.
    /// In that state the GPU is not idle, and the caller must never continue into BaseApp.Resize.
    /// Its ResizeCompute immediately destroys and recreates the native resources of storage textures,
    /// see the contract on Texture.RecreateComputeStorage that the caller must guarantee GPU idle.
    /// Otherwise VkImage and VkImageView still referenced by in-flight command buffers would be destroyed and the next vkQueueSubmit would crash natively.
    /// The correct behavior is to keep the resize flag and retry on the next frame.</summary>
    internal static bool HandleResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;

        // Contract 2-3:
        // stay mutually exclusive with the background loading thread.
        // Under WSL the background thread may still be performing GPU resource work.
        // On timeout, log for diagnosis instead of silently returning and leaving the swapchain permanently out of date.
        bool acquired = false;
        try
        {
            acquired = BaseApp.ResizeSemaphore.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (ObjectDisposedException ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [VK] HandleResize: ResizeSemaphore disposed: {ex.Message}");
            Trace($"HandleResize skip: semaphore disposed");
            return false;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [VK] HandleResize: Wait threw {ex.GetType().Name}: {ex.Message}");
            Trace($"HandleResize skip: Wait threw {ex.GetType().Name}");
            return false;
        }

        if (!acquired)
        {
            // Timeout means the background thread is currently loading.
            // Log it for diagnosis but do not block the render thread.
            // _resized stays true and the next frame will try again.
            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] HandleResize: ResizeSemaphore wait timed out (background loading?), skip resize this frame");
            Trace("HandleResize skip: ResizeSemaphore timeout");
            return false;
        }

        try
        {
            Trace("HandleResize: DeviceWaitIdle");
            Vk.DeviceWaitIdle(LogicalDevice);
            PumpDeferredReleases(force: true);

            SwapChain.Resize(width, height, GraphicsQueueFamily, PresentQueueFamily);
            RecreateRenderFinishedSemaphores();
            Trace($"HandleResize: extent={SwapChain.Extent.Width}x{SwapChain.Extent.Height} images={SwapChain.FrameCount}");

            DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] HandleResize requested={width}x{height} -> extent={SwapChain.Extent.Width}x{SwapChain.Extent.Height} images={SwapChain.FrameCount}");

            // Use the actual Extent chosen by SwapChain as the framebuffer and viewport size.
            // caps.CurrentExtent may differ from the requested width and height.
            int fbW = (int)SwapChain.Extent.Width;
            int fbH = (int)SwapChain.Extent.Height;
            _backbufferWidth = fbW;
            _backbufferHeight = fbH;
            Display.Resize(fbW, fbH, SwapChain.ImageViews);

            // Recreate offscreen RTs in place, with the GPU already idle.
            // Object identity and descriptor sets stay unchanged, so external references remain valid.
            Trace("HandleResize: Display.Resize done, recreating offscreen RTs");
            foreach (var rt in OffscreenTargets)
                if (rt.Desc.MatchBackbufferSize)
                    rt.Recreate((uint)fbW, (uint)fbH);

            // Clear fence state so the next WaitForFence does not use stale completion values.
            // Do not reset FrameIndex.
            // Update may already have written per-frame buffers for the current FrameIndex,
            // and switching slots mid-frame would make this frame read stale data from another slot.
            // After DeviceWaitIdle, every slot is safe.
            for (int i = 0; i < _fenceValues.Length; i++) _fenceValues[i] = 0;

            return true;
        }
        finally
        {
            BaseApp.ResizeSemaphore.Release();
        }
    }

    /// <summary>
    /// Destroy the Vulkan resources bound to the native window, SwapChain plus VkSurfaceKHR.
    /// Used for cases such as Android SurfaceView SurfaceDestroyed or an iOS view moving offscreen.
    /// The caller must stop the render thread first and ensure the GPU has no in-flight frames.
    /// Instance, PhysicalDevice, LogicalDevice, RenderPass, Pipeline, and already uploaded textures and buffers are preserved.
    /// </summary>
    internal static void ReleaseSurfaceAndSwapChain()
    {
        // Called from the UI thread on Android surface teardown: it waits for the device and destroys the swapchain, so
        // it must not overlap a background load either. Re-entrant when the render thread drives it itself.
        EnterGpuAccess();
        try
        {
            ReleaseSurfaceAndSwapChainCore();
        }
        finally
        {
            ExitGpuAccess();
        }
    }

    static void ReleaseSurfaceAndSwapChainCore()
    {
        if (LogicalDevice.Handle != 0)
        {
            CheckResult(Vk.DeviceWaitIdle(LogicalDevice));
            PumpDeferredReleases(force: true);
        }

        if (SwapChain != null)
        {
            SwapChain.Dispose();
            SwapChain = null!;
        }

        if (Surface.Handle != 0 && KhrSurface != null)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }
    }

    /// <summary>
    /// Recreate VkSurfaceKHR, SwapChain, and Display backend attachments using a new native window, effectively a soft restart.
    /// Must be called only after ReleaseSurfaceAndSwapChain, and the GPU must be idle before the call.
    /// Because RenderPass is reused, this assumes BackBufferFormat stays unchanged, which normally holds for Android surfaces on the same physical device.
    /// </summary>
    internal static void RecreateSurfaceAndSwapChain(IntPtr window, Func<ulong, ulong> createSurface, int width, int height)
    {
        // Same reason as ReleaseSurfaceAndSwapChain: this rebuilds swapchain attachments on the UI thread.
        EnterGpuAccess();
        try
        {
            RecreateSurfaceAndSwapChainCore(window, createSurface, width, height);
        }
        finally
        {
            ExitGpuAccess();
        }
    }

    static void RecreateSurfaceAndSwapChainCore(IntPtr window, Func<ulong, ulong> createSurface, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Recreate surface requires positive size");

        WindowHandle = window;

        if (LogicalDevice.Handle != 0)
            CheckResult(Vk.DeviceWaitIdle(LogicalDevice));

        // 1) Recreate VkSurfaceKHR. The old surface has already been destroyed in ReleaseSurfaceAndSwapChain.
        CreateSurface(createSurface);

        // 2) Verify that the current PresentQueueFamily still supports the new Surface, which is true in the vast majority of cases.
        KhrSurface.GetPhysicalDeviceSurfaceSupport(PhysicalDevice, PresentQueueFamily, Surface, out var supported);
        if (!supported)
            throw new Exception("Recreated surface no longer supports current present queue family");

        _backbufferWidth = width;
        _backbufferHeight = height;

        // 3) Recreate the SwapChain. The old instance has already been disposed.
        SwapChain = new SwapChain(
            Vk, Instance, PhysicalDevice, LogicalDevice,
            KhrSurface, Surface,
            preferredFrameCount: frameCount,
            preferredFormat: BackBufferFormat);
        SwapChain.Create(width, height, GraphicsQueueFamily, PresentQueueFamily);
        if (SwapChain.BackBufferFormat != BackBufferFormat)
            throw new NotSupportedException("Recreated surface changed its format; a full graphics device rebuild is required.");
        // Do not update frameCount.
        // FrameContexts, _fenceValues, and all per-frame N-buffered resources,
        // including Text and Sprite instance buffers,
        // were allocated from the initial frameCount.
        // Changing it here would cause index overflow or slot confusion.
        // In-flight frame count and swapchain image count do not need to match.
        // Rebuild every swapchain-attached semaphore, not only the per-image RenderFinished ones:
        // a soft restart can follow a frame that died after acquiring an image, and the stale
        // ImageAvailable of that slot must never be reused (see RecreateFrameSemaphores).
        RecreateFrameSemaphores();
        DeviceServices.BaseApp.AddLog(LogType.Backend, $"{DateTime.UtcNow} [VK] RecreateSurfaceAndSwapChain requested={width}x{height} -> extent={SwapChain.Extent.Width}x{SwapChain.Extent.Height} images={SwapChain.FrameCount} presentMode={SwapChain.PresentMode}");

        // 4) Recreate the Display backend, keeping the existing RenderPass and refreshing only framebuffer, depth, and viewport.
        // Use SwapChain.Extent as the framebuffer size.
        // caps.CurrentExtent may differ from the requested width and height,
        // for example when caps.CurrentTransform includes rotation and CurrentExtent is reported in pre-rotation size.
        // This guarantees that framebuffer and image sizes match.
        int fbW = (int)SwapChain.Extent.Width;
        int fbH = (int)SwapChain.Extent.Height;
        _backbufferWidth = fbW;
        _backbufferHeight = fbH;
        Display.Resize(fbW, fbH, SwapChain.ImageViews);

        // 4.5) Recreate offscreen RTs in place, with the GPU already idle for the same reason as HandleResize.
        foreach (var rt in OffscreenTargets)
            if (rt.Desc.MatchBackbufferSize)
                rt.Recreate((uint)fbW, (uint)fbH);

        // 5) Clear fence state without resetting FrameIndex, for the same reason as HandleResize.
        for (int i = 0; i < _fenceValues.Length; i++) _fenceValues[i] = 0;
    }

    /// <summary>
    /// Rebuild every semaphore attached to the swapchain: the per-frame ImageAvailable used by
    /// acquire and the per-image RenderFinished waited by present. Called after a frame died
    /// between acquiring an image and submitting it: that slot's ImageAvailable stays signaled, and
    /// passing a signaled semaphore back to vkAcquireNextImageKHR is forbidden and can hang the
    /// acquire. The caller must guarantee the render thread has stopped touching frame state;
    /// DeviceWaitIdle runs first so nothing referenced by in-flight GPU work is destroyed, and the
    /// fence bookkeeping is reset exactly as HandleResize does after its wait.
    /// </summary>
    internal static void RecreateFrameSemaphores()
    {
        if (LogicalDevice.Handle == 0 || FrameContexts == null) return;

        CheckResult(Vk.DeviceWaitIdle(LogicalDevice));

        foreach (var frame in FrameContexts)
            frame?.RecreateSyncSemaphores();

        if (SwapChain != null)
            RecreateRenderFinishedSemaphores();

        // After DeviceWaitIdle every slot is safe, and stale completion values must not be waited again.
        for (int i = 0; i < _fenceValues.Length; i++) _fenceValues[i] = 0;
    }

    /// <summary>
    /// Destroy in the reverse order of construction: DebugMessenger, then Device, then Surface, then Instance.
    /// SwapChain and resources are released earlier by their own managers.
    /// </summary>
    internal static void Shutdown()
    {
        EnterGpuAccess();
        try
        {
            ShutdownCore();
        }
        finally
        {
            ExitGpuAccess();
        }
    }

    static void ShutdownCore()
    {
        if (LogicalDevice.Handle != 0)
        {
            Vk.DeviceWaitIdle(LogicalDevice);
            // 2-2 dual-target Scene render pass and framebuffer, lazily created and possibly never enabled.
            foreach (var entry in _sceneTargetPasses.Values)
            {
                Vk.DestroyFramebuffer(LogicalDevice, entry.Framebuffer, null);
                Vk.DestroyRenderPass(LogicalDevice, entry.Pass, null);
            }
            _sceneTargetPasses.Clear();
            // 2-3 triple-target velocity render pass and framebuffer.
            // The caches for the velocity attachment and the depth-less fog composite have already been uniformly destroyed above.
            // Deferred-release actions depend on LogicalDevice, so they must be drained before DestroyDevice.
            PumpDeferredReleases(force: true);
            Vk.DestroyDevice(LogicalDevice, null);
            LogicalDevice = default;
        }

        if (DebugUtils != null && DebugMessenger.Handle != 0)
        {
            DebugUtils.DestroyDebugUtilsMessenger(Instance, DebugMessenger, null);
            DebugMessenger = default;
        }

        if (Surface.Handle != 0 && KhrSurface != null)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
            Surface = default;
        }

        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
    }

    internal static void CheckResult(Result result)
    {
        if (result != Result.Success)
            throw new VulkanCallException("Vulkan", result);
    }
}

// Retain the original return code, and the platform layer distinguishes between surface failures and unrecoverable device/synchronization errors based on this.
internal sealed class VulkanCallException : Exception
{
    internal Result Result { get; }
    internal VulkanCallException(string operation, Result result)
        : base($"{operation}: {result} ({(int)result})") => Result = result;
}
