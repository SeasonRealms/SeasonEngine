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

    void BeginPass(in Season.Rendering.PassDesc desc)
        => throw new NotImplementedException("BeginPass: this platform has not integrated pass scheduling yet");

    void EndPass()
        => throw new NotImplementedException("EndPass: this platform has not integrated pass scheduling yet");

    void BlitToBackbuffer(Season.Rendering.RenderTarget src)
        => throw new NotImplementedException("BlitToBackbuffer: implement when step 2 blit support is introduced");

    /// <summary>Currently used by Windows DX for the Outline2D object mask pass; other platforms default to a no-op implementation.</summary>
    void RenderOutlineMask() { }

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

    Season.Rendering.StorageBuffer CreateStorageBuffer(uint sizeInBytes)
        => throw new NotImplementedException("CreateStorageBuffer: this platform has not integrated the 1-6 compute foundation yet");

    void UpdateStorageBuffer(Season.Rendering.StorageBuffer buffer, ReadOnlySpan<byte> data)
        => throw new NotImplementedException("UpdateStorageBuffer: this platform has not integrated the 1-8 compute 3D extension yet");

    void DispatchCompute(in Season.Rendering.ComputeDispatchArgs args)
        => throw new NotImplementedException("DispatchCompute: this platform has not integrated the 1-6 compute foundation yet");

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
