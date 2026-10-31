// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Basic;

public class Graphics
{
    public static IGraphics Instance { get; set; }

}

public interface IGraphics
{
    /// <summary>Native real-time 2D backend; Null clearly indicates that the current platform does not support it.</summary>
    IImmediate2DBackend? Immediate2D => null;

    void Init();

    Task<bool> LoadSprite2D(Sprite2D sprite);

    void UpdateSprite2D(Sprite2D sprite);

    void DrawSprite2D(Sprite2D sprite);

    Task<bool> LoadTexts(Texts texts);

    /// <summary>Incrementally append glyphs: create atlas entries and holders for <paramref name="appendTexs"/>,
    /// and grow existing GPU buffers on demand without rebuilding per-text state
    /// (unlike the full replacement performed by <see cref="LoadTexts"/>).
    /// On success, <paramref name="appendHolders"/> has the same length and index mapping as <paramref name="appendTexs"/>
    /// (blank / glyphless entries are null), and capacity grows exponentially so append stays amortized O(1).
    /// Returning false means the current control has no appendable state or buffer growth failed,
    /// so the caller must fall back to a full rebuild through <see cref="LoadTexts"/>.</summary>
    Task<bool> AppendTexts(Texts texts, Tex[] appendTexs, ITextureHolder[] appendHolders);

    void UpdateTexts(Texts texts);

    void DrawTexts(Texts texts);

    void DisposeTexts(Texts texts);

    void DisposeTextureHolders(ITextureHolder[] holders);

    void FlushTextAtlas();

    Task<bool> LoadModel(Model model);

    void UpdateModel(Model model, float time);

    void DrawModel(Model model);

    void DisposeModel(Model model);

    Task<bool> LoadSprite3D(Sprite3D sprite);

    void UpdateSprite3D(Sprite3D sprite, float time);

    void DrawSprite3D(Sprite3D sprite);

    void DisposeSprite3D(Sprite3D sprite);

    Task<bool> LoadMesh3D(Mesh3D mesh);

    void UpdateMesh3D(Mesh3D mesh, float time);

    void DrawMesh3D(Mesh3D mesh);

    void DisposeMesh3D(Mesh3D mesh);

    Task<bool> LoadInstancedMesh3D(InstancedMesh3D mesh);

    void UpdateInstancedMesh3D(InstancedMesh3D mesh, float time);

    void DrawInstancedMesh3D(InstancedMesh3D mesh);

    void DisposeInstancedMesh3D(InstancedMesh3D mesh);

    Task<bool> LoadInstancedModel(InstancedModel model);

    void UpdateInstancedModel(InstancedModel model, float time);

    void DrawInstancedModel(InstancedModel model);

    void DisposeInstancedModel(InstancedModel model);

    void ExecuteUpload();

    void DisposeSprite2D(Sprite2D sprite);

    // Shape (procedural geometry).

    Task<bool> LoadShape(Shape shape);

    void UpdateShape(Shape shape);

    void DrawShape(Shape shape);

    void DisposeShape(Shape shape);

    // Pass scheduling / off-screen rendering (D3D12 already implements step 0~3 + contract 1-4 HDR;
    // other platforms fill this in when they align).
    // Implementation contract:
    // - BeginPass/EndPass handle target resolution, viewport sizing, clears, state/layout transitions,
    //   and finalization work such as MSAA resolve and present transitions.
    //   Resource transitions are allowed only inside Begin/End or binding APIs.
    // - Bindings do not persist across passes; draw code must rebind pipeline state and descriptors per pass.
    // - CreateRenderTarget supports both color-only and depth-only targets.
    //   MatchBackbufferSize targets are rebuilt in place on platform resize while external references remain valid.
    // HDR + tone-mapping contract 1-4:
    // - When RenderQuality.HdrSceneColor is enabled, SceneColor uses Rgba16Float and the main pass outputs linear HDR.
    //   Exposure + ACES (Narkowicz) + gamma are closed out in the BlitToBackbuffer tonemap variant.
    // - HDR render-target clear colors are linearized with pow(2.2) in BeginPass; the LDR path passes through unchanged.
    // - Rules for exposure injection and inverse-ACES text compensation are documented on RenderQuality.
    // The default implementation throws because platforms that have not integrated FrameSchedule should never reach these members.

    Season.Rendering.RenderTarget CreateRenderTarget(in Season.Rendering.RenderTargetDesc desc)
        => throw new NotImplementedException("CreateRenderTarget: implement when step 2 off-screen render targets are introduced");

    /// <summary>在效果 Resize 前同步随窗口变化的 RT 资源与尺寸描述；已在平台 Resize 完成同步的后端无需额外操作。</summary>
    void SynchronizeRenderTargetSizes() { }

    /// <summary>缺少独立 HDR 雾合成能力时返回 null，不注册不完整的效果。</summary>
    Season.Rendering.FogCompositePipeline? CreateFogCompositePipeline() => null;
    Season.Rendering.Effects.WeatherCompositePipeline? CreateWeatherCompositePipeline() => null;

    /// <summary>D1: 缺少屏幕空间贴花能力时返回 null，<see cref="Season.Rendering.FrameSchedule.RegisterDecals"/> 据此保持零残留。</summary>
    Season.Rendering.DecalPipeline? CreateDecalPipeline() => null;

    void BeginPass(in Season.Rendering.PassDesc desc)
        => throw new NotImplementedException("BeginPass: this platform has not integrated pass scheduling yet");

    void EndPass()
        => throw new NotImplementedException("EndPass: this platform has not integrated pass scheduling yet");

    void BlitToBackbuffer(Season.Rendering.RenderTarget src)
        => throw new NotImplementedException("BlitToBackbuffer: implement when step 2 blit support is introduced");

    /// <summary>Currently used by Windows DX for the Outline2D object mask pass; other platforms default to a no-op implementation.</summary>
    void RenderOutlineMask() { }

    // Contract P3 secondary views (minimap / rear mirror).
    // - SecondaryViewSupported tells the shared layer whether this backend can render a second view into a fixed-size
    //   offscreen target at all. It is false by default and each backend lifts it only once its per-view constants,
    //   per-view attachments and offscreen presentation actually work; <c>SecondaryView</c> then declines to build the
    //   view and the app keeps its single-view look instead of rendering garbage.
    // - RegisterViewSurface publishes a rendered target into the platform texture dictionary under a name, so an
    //   ordinary Sprite2D can display a view without knowing it is not a file (the same bridge CreateComputeTexture
    //   uses). The wrapper must not take ownership of the target: it is released by the backend, not by the control.

    /// <summary>Whether this backend can render an additional offscreen view. False by default.</summary>
    bool SecondaryViewSupported => false;

    /// <summary>Publishes <paramref name="target"/> under <paramref name="name"/> so 2D controls can sample it.
    /// Returns false when the backend has no bridge or the name is already taken by a real texture.</summary>
    bool RegisterViewSurface(string name, Season.Rendering.RenderTarget target) => false;

    /// <summary>Revokes a publication made by <see cref="RegisterViewSurface"/>. The wrapper is dropped, the target
    /// itself is untouched (the caller still owns and disposes it). Names that were never published are ignored.</summary>
    bool UnregisterViewSurface(string name) => false;

    // Contract 1-5 shadow projection dispatch (clauses 3/7).
    // Per-control entry point for the shadow pass. Control.DrawShadow dispatches through the shared layer into the platform dictionaries.
    // The default is a no-op rather than a throw so platforms without a shadow pass safely ignore it, equivalent to ShadowsEnabled=false.

    void DrawModelShadow(Model model) { }

    void DrawMesh3DShadow(Mesh3D mesh) { }

    void DrawInstancedModelShadow(InstancedModel model) { }

    void DrawInstancedMesh3DShadow(InstancedMesh3D mesh) { }

    // Contract 1-6 compute foundation (kernel registration model: no master shader, every kernel is equal,
    // and source code is provided by effect classes for runtime compilation).
    // Implementation contract:
    // - CreateComputeKernel builds pipeline state and binding layout from ComputeKernelDesc in one shot.
    //   Return null if source is missing or compilation fails.
    // - CreateComputeTexture creates a storage texture that is both write-only storage and sampleable,
    //   then registers it by name in the platform texture dictionary.
    // - DispatchCompute closes over all synchronization internally. Callers may invoke it only inside
    //   ComputeEffect.Record and outside render passes.
    // - Platforms with ComputeSupported=false safely degrade before reaching these members.
    // Current state: the basic four-backend path exists; target-input support and rgba16float expansion are D3D12-first.
    // Contract 1-8 extensions:
    // - CreateComputeTexture3D registers 3D storage textures in a dedicated 3D dictionary, never in the 2D texture dictionary.
    //   That means Sprite2D cannot display them directly; visualization must go through a 3D-to-2D slicing kernel.
    //   The naming convention uses the compute3d:// prefix.
    // - UpdateStorageBuffer is the CPU upload path for StorageBufferRead / StorageBufferReadWrite resources.
    //   It is intended for large constant blocks that do not fit in the 128-byte Params budget.
    // - UpdateStorageBuffer is allowed every frame. Native backends use N-buffered staging indexed by Device.FrameIndex;
    //   WebGPU relies on queue.writeBuffer.
    // - The only restriction is that if the same buffer is uploaded multiple times in one frame,
    //   no dispatch that reads it may be inserted between uploads.

    bool ComputeSupported => false;

    Season.Rendering.ComputeKernel? CreateComputeKernel(Season.Rendering.ComputeKernelDesc desc)
        => throw new NotImplementedException("CreateComputeKernel: this platform has not integrated the 1-6 compute foundation yet");

    void CreateComputeTexture(string name, uint width, uint height,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
        => throw new NotImplementedException("CreateComputeTexture: this platform has not integrated the 1-6 compute foundation yet");

    void CreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm)
        => throw new NotImplementedException("CreateComputeTexture3D: this platform has not integrated the 1-8 compute 3D extension yet");

    Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes,
        Season.Rendering.StorageBufferUsage usage = Season.Rendering.StorageBufferUsage.Compute)
        => throw new NotImplementedException("CreateStorageBuffer: this platform has not integrated the 1-6 compute foundation yet");

    void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
        => throw new NotImplementedException("UpdateStorageBuffer: this platform has not integrated the 1-8 compute 3D extension yet");

    void DispatchCompute(in Season.Rendering.ComputeDispatchArgs args)
        => throw new NotImplementedException("DispatchCompute: this platform has not integrated the 1-6 compute foundation yet");

    /// <summary>返回本次调度是否已录制；缺少任一资源时为 false，不代表 GPU 已执行完成。</summary>
    bool TryDispatchCompute(in Season.Rendering.ComputeDispatchArgs args) => false;

    /// <summary>完整帧数据上传已录制才返回 true；失败时消费者不得发布旧缓冲对应的输出。</summary>
    bool TryUpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data) => false;

    /// <summary>创建或在 GPU 安全点重建三维纹理，返回真实资源是否可用。</summary>
    bool TryCreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm) => false;

    /// <summary>注销效果独占的三维纹理，并按后端在飞资源契约延迟释放。</summary>
    void ReleaseComputeTexture3D(string name) { }

    // Contract A0.1 indirect draw.
    // - IndirectDrawSupported means this backend can source an indexed draw's arguments from a
    //   StorageBufferUsage.ComputeAndIndirectArgs buffer. It does not add a pass, a render target, or a shader variant:
    //   indirect only moves the five DrawIndexedInstanced arguments from CPU registers into GPU memory,
    //   so PSO, root signature, vertex layout, attachments, blend and depth test are all the ordinary ones.
    // - The argument producer is a compute kernel, so the write happens in a ComputeEffect.Record and therefore outside
    //   any render pass, which is already the existing ComputePhase contract.
    // - TryPrepareIndirectArgs is the write-to-read handoff and must also be called outside a render pass.
    //   This is not a stylistic preference: PassDesc forbids resource state switches inside a pass body because a Vulkan
    //   render-pass instance cannot contain barriers. So the submit itself splits into a Prepare step outside the pass and
    //   a Draw step inside it, exactly like FogCompositePipeline.
    // - Arguments are never read back. A readback would stall the frame and remove the only reason to do this at all.
    // - The submit entry point is deliberately absent from this interface. It lives on the backend pipeline object that
    //   owns the matching PSO and index buffer, which can downcast the buffer handle itself, the same way
    //   FogCompositePipeline consumes a StorageBuffer through FogCompositeInputs.

    /// <summary>后端是否已接通间接索引绘制；为 false 时消费者应改走固定实例数的退化路径或整体不注册。</summary>
    bool IndirectDrawSupported => false;

    /// <summary>把间接参数缓冲从 compute 写入状态交接到间接读取状态，返回是否已录制。必须在 Pass 之外调用；
    /// 传入非 ComputeAndIndirectArgs 缓冲、或后端未接通时返回 false，调用方据此放弃本帧的间接提交。</summary>
    bool TryPrepareIndirectArgs(Season.Rendering.StorageBuffer buffer) => false;

    /// <summary>缺少间接实例化四边形绘制能力时返回 null，粒子效果据此整体不注册；返回非 null 即表示 ExecuteIndirect 等效原语已接通。</summary>
    Season.Rendering.ParticleDrawPipeline? CreateParticleDrawPipeline() => null;

    // Contract 1-7 cubemap type + environment IBL.
    // - On platforms with TextureCubeSupported=false, EnvironmentMap.LoadFromFacesAsync returns null,
    //   BaseApp.SceneEnvironment stays null, EnvParams stay zeroed, and rendering falls back to constant ambient light.
    // - CreateTextureCube receives the six faces in CubeFace declaration order (+X,-X,+Y,-Y,+Z,-Z),
    //   already decoded as RGBA8, single-mip, equal-sized squares validated by the shared layer.
    // - The platform registers the resource in its own cube dictionary by name and returns null on failure
    //   so the shared layer can log and degrade gracefully.

    bool TextureCubeSupported => false;

    Season.Rendering.TextureCube? CreateTextureCube(string name, int size,
        Season.Rendering.TextureCubeFormat format, INativeImageDecoder[] faces) => null;

    /// <summary>
    /// Re-runs normal-map mip generation on every already-loaded normal map so a change to
    /// <see cref="RenderQuality.TextureNormalVariance"/> takes effect in the running process. Without this the switch is
    /// frozen into whatever it was when each texture was uploaded, because MipChain.Build reads it and nothing else does,
    /// so comparing it on against off meant restarting and lining up two screenshots.
    ///
    /// It is deliberately the only texture knob offered this way. <see cref="RenderQuality.TextureMipmaps"/> decides the
    /// level count, which is baked into the resource at creation, and <see cref="RenderQuality.TextureLodBias"/> is a
    /// shader compile-time constant; neither can be redone without rebuilding what holds it. Variance only rewrites the
    /// alpha channel of the levels below zero, so the level count and the resource survive and an in-place pixel upload
    /// carries it.
    ///
    /// The default is a no-op returning zero, which is honest rather than lazy: a backend can only implement this if it
    /// still holds the authored level-0 pixels. Vulkan and Metal drop them once the upload batch has run (see the note at
    /// that release), and the Web backend never keeps them on the C# side at all - its JS fallback loader does not even
    /// decode them there. So this is D3D12-only by decision, not by omission: it buys a development-time A/B on the one
    /// platform that A/B is performed on, and everywhere else the switch still applies, just from the next launch.
    /// </summary>
    /// <returns>How many textures were rebuilt. Zero means the platform does not support it, or nothing was eligible.</returns>
    int RebuildNormalVarianceTextures() => 0;
}

public interface INativeImageDecoder : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Stride { get; }

    /// Always exposed as an RGBA8 pixel block.
    ReadOnlySpan<byte> PixelSpan { get; }
}

public sealed class NativeImageData : INativeImageDecoder
{
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public ReadOnlySpan<byte> PixelSpan => _pixels;

    public NativeImageData(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        _pixels = pixels;
    }

    public void Dispose() { }
}

/// <summary>
/// Texture update source for materials: either a file path or decoded pixel data.
/// Implicit conversions support both string file paths and <see cref="INativeImageDecoder"/> pixel sources.
/// After it is assigned, the next Update* call consumes it internally and then resets it back to default.
/// </summary>
public struct TextureUpdateSource
{
    /// <summary>File path. The engine decodes it into <see cref="INativeImageDecoder"/> through ImageUtils.</summary>
    public string? Path { get; set; }

    /// <summary>Decoded pixels (RGBA8). The engine consumes them directly and is responsible for disposal.</summary>
    public INativeImageDecoder? Image { get; set; }

    /// <summary>Whether valid data is present (Path or Image is non-null).</summary>
    public bool HasValue => Path != null || Image != null;

    public static implicit operator TextureUpdateSource(string path)
        => new() { Path = path };

    /// <summary>Create an update source from decoded pixel data.</summary>
    public static TextureUpdateSource FromImage(INativeImageDecoder image)
        => new() { Image = image };
}
