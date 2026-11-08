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

    /// <summary>Synchronizes window-dependent RT resources and size descriptors before an effect Resize; backends that already synchronized during the platform Resize need no extra action.</summary>
    void SynchronizeRenderTargetSizes() { }

    /// <summary>Returns null when the independent HDR fog composite capability is missing, so no incomplete effect is registered.</summary>
    Season.Rendering.FogCompositePipeline? CreateFogCompositePipeline() => null;
    Season.Rendering.Effects.WeatherCompositePipeline? CreateWeatherCompositePipeline() => null;

    /// <summary>P0: returns null when the capability to "add the reflection delta additively into the scene color" is missing, in which case <see cref="Season.Rendering.Effects.SsrEffect"/>
    /// refuses to register accordingly (zero residue) and the image falls back to sky reflection. Returning null instead of throwing is because what this path lacks is only extra information,
    /// and without it the image is still correct -- the only difference is that adjacent objects cannot be seen in the mirror.</summary>
    Season.Rendering.SsrCompositePipeline? CreateSsrCompositePipeline() => null;

    /// <summary>D1: returns null when the screen-space decal capability is missing, in which case <see cref="Season.Rendering.FrameSchedule.RegisterDecals"/> keeps zero residue.</summary>
    Season.Rendering.DecalPipeline? CreateDecalPipeline() => null;

    /// <summary>P2: returns null when the water surface material capability is missing, in which case <see cref="Season.Rendering.FrameSchedule.RegisterWaters"/> keeps zero residue.</summary>
    Season.Rendering.WaterPipeline? CreateWaterPipeline() => null;

    /// <summary>P1: returns null when the water curtain material capability is missing, in which case <see cref="Season.Rendering.FrameSchedule.RegisterWaterfalls"/> keeps zero residue.</summary>
    Season.Rendering.WaterfallPipeline? CreateWaterfallPipeline() => null;

    /// <summary>
    /// P1 river ribbon: the same pass identity, resource shape and blending as the curtain (the two frame buffers are slot-for-slot
    /// equal, see <see cref="Season.Rendering.WaterRibbonMath"/>), so a backend that can draw a curtain can draw a river by swapping
    /// only the shader source. Null means the river is just data and nothing is drawn.
    /// </summary>
    Season.Rendering.WaterfallPipeline? CreateWaterRibbonPipeline() => null;

    /// <summary>
    /// P2: copies the contents of <paramref name="source"/> verbatim into <paramref name="destination"/> (same size, same format).
    /// Water surface refraction needs to sample the scene color in the frame where "SceneColor is still a color attachment", and the same resource cannot be both an attachment and a sampling source,
    /// so an independent copy must be obtained before drawing begins. Returning false means this backend does not support it or the shapes do not match, and the caller skips the water surface for the whole frame accordingly.
    /// </summary>
    bool CopyRenderTarget(Season.Rendering.RenderTarget source, Season.Rendering.RenderTarget destination) => false;

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

    /// <summary>Returns whether this dispatch has been recorded; false when any resource is missing, and does not mean the GPU has finished executing.</summary>
    bool TryDispatchCompute(in Season.Rendering.ComputeDispatchArgs args) => false;

    /// <summary>Returns true only when the full-frame data upload has been recorded; on failure the consumer must not publish the output corresponding to the old buffer.</summary>
    bool TryUpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data) => false;

    /// <summary>Creates or recreates a 3D texture at a GPU-safe point, returning whether the real resource is available.</summary>
    bool TryCreateComputeTexture3D(string name, uint width, uint height, uint depth,
        Season.Rendering.ComputeStorageFormat format = Season.Rendering.ComputeStorageFormat.Rgba8Unorm) => false;

    /// <summary>Unregisters a 3D texture exclusively owned by an effect, and defers the release according to the backend's in-flight resource contract.</summary>
    void ReleaseComputeTexture3D(string name) { }

    /// <summary>
    /// Step 2: finishes the kernels that were registered since the previous frame boundary.
    ///
    /// <para><b>Contract.</b> <see cref="CreateComputeKernel"/> may return a handle whose pipeline does not exist yet, so
    /// the backend must be given one point per frame - at the boundary, before any compute phase can dispatch - to
    /// complete them. The backend is free to do that work in parallel and to use whatever split it likes between CPU
    /// compilation and GPU pipeline creation, because <see cref="Season.Rendering.ComputeKernel"/> gives effects no way
    /// to observe the handle's internal state.</para>
    ///
    /// <para><b>Why it is optional.</b> A backend that compiles inside CreateComputeKernel is already complete at the
    /// moment it returns and has nothing to flush; the default is therefore a no-op rather than an error, which is the
    /// same shape the rest of this interface uses for capabilities it may not have.</para>
    ///
    /// <para><b>Called from</b> RenderPass.ExecuteFrame via BeginSceneFrame, on the frame-loop thread, once per frame,
    /// before the first RecordPhase. Late registrations that arrive after this point are not lost: the backend's own
    /// dispatch path must still work on a kernel that was never flushed.</para>
    /// </summary>
    void FlushPendingCompute() { }

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

    /// <summary>Whether the backend has integrated indirect indexed draw; when false, consumers should switch to the degenerate path with a fixed instance count or not register at all.</summary>
    bool IndirectDrawSupported => false;

    /// <summary>
    /// A0.2: whether the backend has integrated indirect compute dispatch (the path where <see cref="Season.Rendering.ComputeDispatchArgs.Arguments"/> is non-empty).
    /// When false, consumers should switch to a dispatch with a fixed group count rather than skip this work.
    ///
    /// It and <see cref="IndirectDrawSupported"/> are two separate things: D3D12 indirect draw only needs one command signature,
    /// while indirect dispatch needs another (the argument type differs, and so does the byte stride), so the two capability bits each handle their own.
    /// All four existing backends are integrated, but the capability bit is kept -- consumers use it to decide whether it is worth paying
    /// one extra kernel for "the group count determined by the GPU"; if not available, dispatch directly by the upper bound.
    ///
    /// Unlike draw, there is no paired Prepare entry point here, and none is needed: DispatchCompute is itself constrained by ComputePhase and
    /// naturally recorded outside a Pass, and the parameter handoff and the dispatch are the same call. The reason the draw side must be split into Prepare + Draw
    /// is that Draw falls inside a Pass, and PassDesc forbids switching resource state inside a Pass body.
    /// </summary>
    bool DispatchIndirectSupported => false;

    /// <summary>Hands the indirect argument buffer over from the compute write state to the indirect read state and returns whether it has been recorded. Must be called outside a Pass;
    /// returns false when a non-ComputeAndIndirectArgs buffer is passed or the backend is not integrated, and the caller abandons the frame's indirect submission accordingly.</summary>
    bool TryPrepareIndirectArgs(Season.Rendering.StorageBuffer buffer) => false;

    // Contract P0 instance palette: compute writes, vertex shader reads.
    // - This is the second half of <see cref="Season.Rendering.StorageBufferUsage.ComputeAndVertexShaderRead"/>, and it is a different
    //   handoff from <see cref="TryPrepareIndirectArgs"/>: that one ends in the DrawIndirect stage, this one ends in the vertex shader's
    //   own read. Vulkan and D3D12 both need a barrier/transition for it, Metal and WebGPU track it themselves.
    // - Like every other Prepare entry point it must be recorded outside a Pass. A Vulkan render-pass instance cannot contain barriers,
    //   so the handoff has to happen before the pass that will draw the palette, not inside it.
    // - The consumer is the palette slot the shader already declares (t6 this frame, t8 the previous one), so a producer that swaps the
    //   CPU palette ring for a storage ring changes no shader, no root signature and no descriptor layout.

    /// <summary>Whether the backend can bind a compute-written storage buffer into the vertex stage. When false, the instance palette must
    /// stay on the CPU-written ring: a producer tests this once at allocation time and keeps its CPU path rather than degrading mid-frame.</summary>
    bool VertexShaderStorageReadSupported => false;

    /// <summary>Hands a storage buffer over from the compute write state to the vertex-shader read state and returns whether it has been recorded.
    /// Must be called outside a Pass; returns false when the buffer did not declare <see cref="Season.Rendering.StorageBufferUsage.ComputeAndVertexShaderRead"/>
    /// or the backend is not integrated, in which case the frame must not draw from it.</summary>
    bool TryPrepareVertexShaderRead(Season.Rendering.StorageBuffer buffer) => false;

    // Contract C1 per-vertex stream: compute writes whole vertices, the input assembler reads them.
    // - This is the public half of <see cref="Season.Rendering.StorageBufferUsage.ComputeAndVertexInput"/>. A7 already used that
    //   usage inside the D3D12 backend (the gather block becomes the instance stream); C1 lets an effect produce the
    //   per-vertex stream of a Mesh3D surface instead, laid out exactly as <see cref="Vertex"/> (80 bytes per vertex).
    // - Because the stream replaces the surface's static vertex buffer one for one, the vertex shader, the shadow vertex
    //   shader, the PSO and the root signature are all the ordinary ones, so the shadow pass rasterizes the same shape the
    //   scene pass shows by construction.
    // - Like every other Prepare entry point it must be recorded outside a Pass. The return trip to the compute write state is
    //   handled by DispatchCompute's own gated transition, so there is no matching "release" call.

    /// <summary>Whether the backend can bind a compute-written storage buffer as a per-vertex IA stream. When false, a producer
    /// keeps the surface's static vertices and registers nothing.</summary>
    bool VertexInputStorageSupported => false;

    /// <summary>Hands a storage buffer over from the compute write state to the vertex-input read state and returns whether it has
    /// been recorded. Must be called outside a Pass; returns false when the buffer did not declare
    /// <see cref="Season.Rendering.StorageBufferUsage.ComputeAndVertexInput"/> or the backend is not integrated, in which case the
    /// frame must not draw from that stream (the backend falls back to the static vertices).</summary>
    bool TryPrepareVertexInput(Season.Rendering.StorageBuffer buffer) => false;

    // Contract L3.1: diagnostic readback. This is the only entry point in the interface that moves buffer
    // contents from the GPU back to the CPU, and it exists because one class of number has no other source.
    // "How many instances did culling keep" lives entirely on the GPU (the index table is compressed and the
    // per-slot counts are written by cull and cleared by gather), and it cannot be recovered from pixels: a
    // frame that culled correctly and a frame that culled nothing are identical by construction, which is
    // exactly the property A7 was accepted on. Re-deriving the answer on the CPU is the mistake
    // InstanceCull.ReportStats exists to avoid.
    // - The default returns null. Callers must read that as "this platform cannot answer", never as "the
    //   buffer was empty": an empty buffer is a legitimate answer and comes back as a zeroed array.
    // - It waits for the GPU. That is acceptable only because call sites are one-shot capture paths; a
    //   per-frame caller would serialize the pipeline against itself for a number nobody renders from.

    /// <summary>Whether <see cref="TryReadStorageBuffers"/> can answer on this backend.</summary>
    bool StorageBufferReadbackSupported => false;

    /// <summary>
    /// Copies each storage buffer's full contents back to the CPU in one submission, or returns null when the
    /// backend has no readback path. Entry <c>i</c> is <see cref="Season.Rendering.StorageBuffer.SizeInBytes"/>
    /// long and holds what the GPU last wrote to buffer <c>i</c>.
    ///
    /// <para>Batched rather than one buffer per call because the cull chain owns one argument block per group:
    /// reading twenty-six blocks one call at a time would cost twenty-six queue waits for the same twenty-six
    /// kilobytes, while one submission costs one wait no matter how many blocks ride along.</para>
    ///
    /// <para>Must be called between frames, outside a Pass and outside the frame's own list recording: the
    /// backend submits a copy list of its own and waits for it before returning.</para>
    /// </summary>
    byte[][]? TryReadStorageBuffers(IReadOnlyList<Season.Rendering.StorageBuffer> buffers) => null;

    /// <summary>Returns null when the indirect instanced quad draw capability is missing, in which case the particle effect is not registered at all; a non-null return means the ExecuteIndirect-equivalent primitive has been integrated.</summary>
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

    // Contract 1-9 runtime statistics (frame rate / GPU memory / CPU memory).
    // The work is split between the shared layer and the backends on purpose:
    // - Frame rate and CPU memory are process facts, not device facts. <see cref="GraphicsStatistics"/> measures them once
    //   for every backend, fed by the single per-frame entry point in FrameSchedule.Execute, so four backends do not end up
    //   with four slightly different definitions of "a frame".
    // - GPU memory can only be answered by whoever owns the device, so it is the one member here. It is queried at most once
    //   per second by GraphicsStatistics, never per frame, which is what makes a driver query affordable.
    // - Usage and ceiling are independent: a backend that knows what it holds but not what the budget is (WebGPU) still
    //   reports a real usage, and unknown usage reports <see cref="GraphicsMemoryInfo.Unknown"/> rather than a fabricated 0.
    //   The default implementation below is exactly that: platforms that never integrated a memory query degrade to "--"
    //   in the HUD instead of lying.

    /// <summary>
    /// This backend's view of graphics memory: what the process currently holds on the GPU and the ceiling the same source
    /// exposes. Never throws; an unavailable query returns <see cref="GraphicsMemoryInfo.Unknown"/>.
    /// </summary>
    GraphicsMemoryInfo QueryGraphicsMemory() => default;

    // Contract 3-1 GPU frame timing.
    // - The shared layer owns the instrumentation (see FrameProfiler); the backend owns only the two device facts it alone knows:
    //   whether this device can write timestamps, and how to read the results back without stalling a frame.
    // - Returning null - the default here - is the honest answer for a backend that has not integrated a timer yet.
    //   The profiler then reports the CPU and allocation columns and prints "--" for every GPU number, which is a
    //   measurement, not a fallback zero. A platform lifts this bit by implementing the interface, and nothing above it
    //   changes: no runner, no CSV column, no HUD branch.
    // - Called exactly once, on the render thread, the first time a profiled frame begins. The returned timer is owned by
    //   the caller (FrameProfiler) and must be disposable, because building it allocates a query heap and readback ring.

    /// <summary>
    /// Creates this backend's GPU frame timer, or null when it has none. Called at most once per process, only while
    /// profiling is enabled, so the cost of building a query heap is never paid by an ordinary run.
    /// </summary>
    IGpuFrameTimer? CreateGpuFrameTimer() => null;
}

/// <summary>
/// Contract 3-1: the GPU half of frame profiling, implemented by a backend that can write timestamps into its command
/// stream and read the results back later.
///
/// <para>The shape is deliberately narrow. The shared layer decides what is worth timing and what the numbers mean; the
/// backend only answers "write a timestamp here", "record the readback", and "tell me which frames are done". That split
/// is what lets the CSV columns, the runner and the HUD stay backend-agnostic across D3D12, Vulkan, Metal and WebGPU.</para>
///
/// <para>Threading: every member is called on the render thread, in submission order. No member may block: an
/// implementation that waits for the GPU would make the profiler the cost it is trying to measure.</para>
/// </summary>
public interface IGpuFrameTimer : IDisposable
{
    /// <summary>False once the device is lost or a call has failed; the profiler then stops asking and keeps the CPU columns.</summary>
    bool Available { get; }

    /// <summary>Nanoseconds per timestamp tick, from the queue's own frequency. Multiply a tick delta by this for real time.</summary>
    double NanosecondsPerTick { get; }

    /// <summary>Marks this timer accepts per frame. <see cref="Season.Rendering.FrameProfiler.MaxIntervals"/> intervals need twice this budget.</summary>
    int Capacity { get; }

    /// <summary>Starts a new frame and resets the mark cursor. <paramref name="token"/> is echoed back by <see cref="TryTake"/>.</summary>
    void BeginFrame(long token);

    /// <summary>Writes one timestamp and returns its slot, or -1 when this frame has exhausted <see cref="Capacity"/>.</summary>
    int Mark();

    /// <summary>
    /// Records the resolve-and-copy of this frame's marks. Called while the frame's command list is still open, after the
    /// last mark. A frame that never reaches this call (it threw, or its Begin/End pair was unbalanced) is simply dropped.
    /// </summary>
    void EndFrame();

    /// <summary>Called after the queue submitted the command lists, so the timer can signal its own fence.</summary>
    void OnSubmitted();

    /// <summary>
    /// Non-blocking readback of one finished frame. Returns false when nothing is ready. <paramref name="ticks"/> stays valid
    /// only until the next call, so the caller copies what it needs before asking again. A frame whose slot was still busy
    /// is never returned: it is dropped and the caller publishes its GPU column as unknown.
    /// </summary>
    bool TryTake(out long token, out ReadOnlySpan<ulong> ticks);
}

/// <summary>
/// One reading of graphics memory (contract 1-9).
/// <para><see cref="UsedBytes"/> is the process's own GPU footprint where the platform can report one: the D3D12 local
/// segment from DXGI, <c>currentAllocatedSize</c> on Metal, the memory-budget heaps on Vulkan, and the host-side texture
/// sum on WebGPU. <see cref="TotalBytes"/> is the ceiling the same source exposes, or 0 when it has none.</para>
/// </summary>
public readonly struct GraphicsMemoryInfo
{
    /// <summary>Bytes of GPU memory attributed to this process; meaningful only when <see cref="UsedKnown"/> is true.</summary>
    public long UsedBytes { get; init; }

    /// <summary>Ceiling reported by the same source, or 0 when the platform has none to report.</summary>
    public long TotalBytes { get; init; }

    /// <summary>False when the backend could not measure usage; <see cref="UsedBytes"/> is then not a reading.</summary>
    public bool UsedKnown { get; init; }

    /// <summary>"This backend cannot measure it": the only value a platform without a memory query may return.</summary>
    public static GraphicsMemoryInfo Unknown => default;

    public GraphicsMemoryInfo(long usedBytes, long totalBytes = 0, bool usedKnown = true)
    {
        UsedBytes = usedBytes;
        TotalBytes = totalBytes;
        UsedKnown = usedKnown;
    }
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
