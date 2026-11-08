// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using Season.Rendering;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// D1 screen-space decal Metal implementation.
///
/// One 36-vertex <c>DrawPrimitives</c> per decal: vertices compute the six faces of the projection box from <c>[[vertex_id]]</c>,
/// and the rasterizer keeps only back faces (<see cref="MTLCullMode.Front"/>), so each pixel is hit at most once —— blending is not applied twice,
/// and no stencil is needed. The pixel shader reconstructs world coordinates from this frame's depth, folds them into the box's local space, and takes the UV; the color is written back to
/// SceneColor via fixed-function blending, so SceneColor does not need to be a samplable resource at the same time, and in the Pass it is only a color attachment.
///
/// The three differences from the other three backends are all Metal's own shape:
/// <list type="bullet">
/// <item>No render pass object: attachments are created on the fly by <see cref="Device.BeginPass"/>'s <c>MTLRenderPassDescriptor</c>,
/// so this class only needs to confirm on the <see cref="Device"/> side "no depth attachment + optional color attachment 1";</item>
/// <item>The attachment formats are baked into the PSO, but the decal's attachment shape has only one form (RGBA16Float + optional second RGBA16Float + no depth),
/// so the four PSOs are baked once at construction time, and failures are caught by <c>CreateDecalPipeline</c> and fall back entirely;</item>
/// <item>There is neither a descriptor ring nor a fence: Metal binds resources directly, and cross-frame reuse is excluded by intra-frame timing (a new command buffer each frame).</item>
/// </list>
///
/// The timing is the same as <see cref="MTLFogCompositePipeline"/>: Prepare resolves resources outside the Pass, and Draw can only
/// consume once inside <see cref="RenderPassId.SceneDecal"/>; any mismatch with the preparation stage gives up the draw rather than partially drawing.
/// </summary>
internal sealed class MTLDecalPipeline : DecalPipeline
{
    /// <summary>Per-decal texture resolver: only looks up the backend texture dictionary, does not load. When name → texture is empty, that draw is skipped.</summary>
    readonly Dictionary<string, Texture> _textures;

    // The three functions come from the same source and the same library, and the whole pipeline is compiled only once.
    IMTLFunction? _vertexFunction, _fragmentFunction, _fragmentRoughnessFunction;

    /// <summary>This Pass has no depth attachment, so the compare function must be Always —— the same choice as the depthless tiers of fog composite and particles.</summary>
    IMTLDepthStencilState? _noDepthState;

    /// <summary>[mode 0=Modulate/1=Additive, whether a normal attachment is present]. The attachment count must match the PSO's color attachment count.</summary>
    readonly IMTLRenderPipelineState?[,] _pipelines = new IMTLRenderPipelineState?[2, 2];

    readonly Texture?[] _drawTextures = new Texture?[DecalMath.MaxDecals];
    readonly bool[] _drawSkip = new bool[DecalMath.MaxDecals];

    // Product of Prepare, consumed by Draw exactly once.
    // The RT wrapper object is held as well: the texture reference is only a field within it, and holding the RT ensures it cannot be rebuilt midway through the frame.
    MTLRenderTarget? _destination;
    MTLRenderTarget? _color, _depth, _normal;
    IMTLTexture? _colorTexture, _depthTexture, _normalTexture;
    MTLStorageBuffer? _frame;
    IMTLCommandBuffer? _command;
    DecalInputs _inputs;
    int _frameIndex;
    bool _writeRoughness;
    bool _disposed;

    internal MTLDecalPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, DecalShaders.Msl);
        _vertexFunction = CreateFunction(library, "VSMain");
        _fragmentFunction = CreateFunction(library, "PSMain");
        _fragmentRoughnessFunction = CreateFunction(library, "PSMainRoughness");

        using var noDepth = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
        };
        _noDepthState = MTLPipelineFactory.CreateDepthState(noDepth, "decal");

        for (int roughness = 0; roughness < 2; roughness++)
        {
            var fragment = roughness == 1 ? _fragmentRoughnessFunction : _fragmentFunction;
            _pipelines[0, roughness] = CreatePipeline(fragment!, additive: false, roughness == 1);
            _pipelines[1, roughness] = CreatePipeline(fragment!, additive: true, roughness == 1);
        }
    }

    static IMTLFunction CreateFunction(IMTLLibrary library, string entryPoint)
        // MSL keeps the real entry-point names (unlike GLSL, which is always main), so a failure to find one means the source and the name here have diverged.
        => library.CreateFunction(entryPoint)
            ?? throw new InvalidOperationException($"Decal shader entry point {entryPoint} not found.");

    /// <summary>
    /// The two blend semantics share one shader, differing only in the source factors:
    /// Modulate uses <c>dst * src</c> (SourceRgb = DestinationColor, DestinationRgb = Zero), treating the pixel output as the modulation amount,
    /// and Additive uses <c>dst + src</c>. alpha always keeps the target value, and decals do not participate in subsequent alpha tests.
    ///
    /// When <paramref name="roughness"/> is true, a second color attachment (SceneNormal) is attached. It enables only the B channel write mask,
    /// and interpolates onto the original value using coverage, so the octahedral normal and metallic are not touched.
    /// </summary>
    IMTLRenderPipelineState CreatePipeline(IMTLFunction fragment, bool additive, bool roughness)
    {
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = (additive ? "Season-Decal-Add" : "Season-Decal-Mod") + (roughness ? "-Rough" : string.Empty),
            VertexFunction = _vertexFunction,
            FragmentFunction = fragment,
            // The decal Pass deliberately does not attach depth: depth is a sampling source this frame, and the same resource cannot be both sampled and used as an attachment.
            DepthAttachmentPixelFormat = MTLPixelFormat.Invalid,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = MTLPixelFormat.RGBA16Float;
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        color.SourceRgbBlendFactor = additive ? MTLBlendFactor.One : MTLBlendFactor.DestinationColor;
        color.DestinationRgbBlendFactor = additive ? MTLBlendFactor.One : MTLBlendFactor.Zero;
        color.SourceAlphaBlendFactor = MTLBlendFactor.Zero;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.One;
        if (roughness)
        {
            // Only .z (Blue) is written: the normal's octahedral encoding is in .xy and the reflection weight in .w, and both must be preserved.
            // The blend lerps using coverage, and when coverage is 0 the result is exactly the original value, so a "does not touch roughness" decal does not change the target.
            var normal = desc.ColorAttachments[1];
            normal.PixelFormat = MTLPixelFormat.RGBA16Float;
            normal.BlendingEnabled = true;
            normal.RgbBlendOperation = MTLBlendOperation.Add;
            normal.AlphaBlendOperation = MTLBlendOperation.Add;
            normal.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
            normal.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
            normal.SourceAlphaBlendFactor = MTLBlendFactor.One;
            normal.DestinationAlphaBlendFactor = MTLBlendFactor.Zero;
            normal.WriteMask = MTLColorWriteMask.Blue;
        }
        return MTLPipelineFactory.CreateRender(desc);
    }

    static bool Readable(IMTLTexture? texture, MTLTextureType type) => texture != null
        && texture.Handle != IntPtr.Zero && texture.TextureType == type
        && texture.Width > 0 && texture.Height > 0 && (texture.Usage & MTLTextureUsage.ShaderRead) != 0;

    /// <summary>A texture in the backend texture dictionary: the wrapper object is ready and the underlying texture is samplable. Decals whose texture has not finished uploading are skipped entirely.</summary>
    static bool Samplable(Texture? texture) => texture != null && Volatile.Read(ref texture.Ready)
        && Readable(texture.Image, MTLTextureType.k2D);

    /// <summary>
    /// Verifies the shape and folds the resources into a reference snapshot. Returning false means giving up this frame: a decal only holds
    /// under the one shape of "color is single-sample HDR and depth has the same size as color"; in all other cases it is better to produce no image than a wrong one.
    ///
    /// After s9 the normal target is an <b>attachment</b> rather than a sampling source: the same resource cannot be both an attachment and a sampling source.
    /// Unlike DX, this backend does not fall back: whether <see cref="Device.BeginPass"/> attaches color attachment 1 depends only on
    /// <see cref="DecalInputs.WriteRoughness"/>, while Metal bakes the attachment count into the PSO ——
    /// if it degraded to a single-attachment PSO here, the actual Pass would still be attaching two attachments. Better to produce no decals for the whole frame than to gamble on an undefined attachment count.
    /// </summary>
    bool Resolve(in DecalInputs inputs, MTLRenderTarget destination, out bool roughness)
    {
        roughness = false;
        if (inputs.Color is not MTLRenderTarget color || inputs.Depth is not MTLRenderTarget depth
            || inputs.FrameBuffer is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.Buffer.Handle == IntPtr.Zero
            || frame.SizeInBytes < DecalMath.ByteCount || frame.Buffer.Length < (nuint)DecalMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount > 1 || depth.Desc.SampleCount > 1 || destination.Desc.SampleCount > 1
            || depth.Desc.ColorFormat != RtFormat.None || depth.Desc.DepthFormat != RtFormat.D32Float
            // This Pass uses the color target as the blend backdrop at the same time, so the sharing layer must hand over the same target.
            || !ReferenceEquals(destination, color)
            || color.Width <= 0 || color.Height <= 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > DecalMath.MaxDecals) return false;

        var colorTexture = color.ColorTexture;
        var depthTexture = depth.DepthTexture;
        if (colorTexture == null || destination.ColorTexture == null
            || !Readable(colorTexture, MTLTextureType.k2D)) return false;
        if (depthTexture == null || depthTexture.PixelFormat != MTLPixelFormat.Depth32Float
            || !Readable(depthTexture, MTLTextureType.k2D)) return false;

        // Roughness writing requires the normal target to have the same shape as the color. Metal bakes the attachment count into the PSO, so on a shape mismatch it gives up the whole frame rather than degrading,
        // otherwise you would get the undefined combination of "two attachments + a single-attachment PSO".
        MTLRenderTarget? normal = null;
        IMTLTexture? normalTexture = null;
        if (inputs.WriteRoughness)
        {
            if (inputs.Normal is not MTLRenderTarget n || n.Desc.ColorFormat != RtFormat.Rgba16Float
                || n.Desc.SampleCount > 1 || n.Width != color.Width || n.Height != color.Height
                || !Readable(n.ColorTexture, MTLTextureType.k2D)) return false;
            normal = n;
            normalTexture = n.ColorTexture;
        }

        _color = color; _depth = depth; _normal = normal;
        _colorTexture = colorTexture; _depthTexture = depthTexture; _normalTexture = normalTexture;
        _frame = frame;
        roughness = normalTexture != null;
        return true;
    }

    public override bool Prepare(in DecalInputs inputs, RenderTarget destination)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // On Metal the Pass is the encoder, so "outside a Pass" is equivalent to there being no render encoder currently.
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || destination is not MTLRenderTarget target || !Resolve(inputs, target, out bool roughness)) return false;

        _inputs = inputs;
        _destination = target;
        _writeRoughness = roughness;
        _command = command;
        _frameIndex = Device.FrameIndex;

        // s3 per-decal textures: only looks up the dictionary, does not load here. Decals whose name is unregistered or whose upload has not finished are skipped entirely.
        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        lock (_textures)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                string? name = inputs.Draws[i].Texture;
                if (name == null) continue;
                if (!_textures.TryGetValue(name, out var texture) || !Samplable(texture))
                {
                    _drawSkip[i] = true;
                    continue;
                }
                _drawTextures[i] = texture;
            }
        }
        return true;
    }

    public override unsafe bool Draw()
    {
        var destination = _destination;
        var command = _command;
        var colorTexture = _colorTexture;
        var depthTexture = _depthTexture;
        var normalTexture = _normalTexture;
        var frame = _frame;
        var inputs = _inputs;
        bool roughness = _writeRoughness;
        int frameIndex = _frameIndex;
        ClearPrepared();
        var encoder = Device.GraphicsEncoder;
        if (_disposed || destination == null || frame == null || colorTexture == null || depthTexture == null
            || encoder == null || encoder.Handle == IntPtr.Zero
            || command == null || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex || Device.ActivePassId != RenderPassId.SceneDecal
            || !ReferenceEquals(destination, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle
            // The attachment shape can only be inferred from the actual bindings: whether the normal is attached decides which PSO tier to use.
            || roughness != (Device.ActiveNormalTexture != null)
            || (roughness && Device.ActiveNormalTexture!.Handle != normalTexture!.Handle)) return false;

        var pipeline = _pipelines[0, roughness ? 1 : 0];
        if (pipeline == null) return false;

        // The main pipeline rebinds the PSO, depth state, culling, and vertex slots 0..4 on every draw, so the overrides here do not leak into subsequent draws.
        encoder.SetDepthStencilState(_noDepthState!);
        // Winding is an encoder dynamic state on Metal, not a PSO state: the default CounterClockwise would make
        // "keep only back faces" cull the wrong side (Metal's winding is determined in y-down frame-buffer coordinates, exactly the opposite of DX's default),
        // so here it is explicitly aligned with the main pipeline: clockwise is front-facing, and Front culling = culling the side DX would cull too.
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);
        // Keep only back faces: the projection box is convex, so a back face is hit exactly once per pixel.
        encoder.SetCullMode(MTLCullMode.Front);
        encoder.SetVertexBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentBuffer(frame.Buffer, 0, 0);
        // With no texture the shader does not sample, but the binding must be valid: like DX/VK, use the depth map as a placeholder.
        encoder.SetFragmentTexture(depthTexture, 0);
        encoder.SetFragmentTexture(depthTexture, 1);

        int mode = -1;
        // The MSL side is a uint4 and the bytes are copied as-is, so integer bits must be written; writing a float would make the offsets from the second decal onward go out of bounds.
        var parameters = stackalloc uint[4];
        for (int i = 0; i < inputs.Count; i++)
        {
            // Decals whose texture is not ready are skipped entirely: sampling a placeholder texture would render "not yet finished loading" as a patch of wrong color.
            if (_drawSkip[i]) continue;
            var draw = inputs.Draws[i];
            int next = draw.Mode == DecalMode.Additive ? 1 : 0;
            if (next != mode)
            {
                var selected = _pipelines[next, roughness ? 1 : 0];
                if (selected == null) return false;
                encoder.SetRenderPipelineState(selected);
                mode = next;
            }
            var texture = _drawTextures[i];
            if (texture != null) encoder.SetFragmentTexture(texture.Image, 1);
            // Decal slots only go up to 64, so a uint4 is enough; the vertex and pixel stages read the same copy, so both stages must push it.
            parameters[0] = (uint)draw.Index;
            encoder.SetVertexBytes((IntPtr)parameters, 16, 1);
            encoder.SetFragmentBytes((IntPtr)parameters, 16, 1);
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 36);
        }
        return true;
    }

    void ClearPrepared()
    {
        _destination = null;
        _color = null; _depth = null; _normal = null;
        _colorTexture = null; _depthTexture = null; _normalTexture = null;
        _frame = null; _command = null; _inputs = default;
        _frameIndex = 0; _writeRoughness = false;
        // The per-decal textures and skip table are not cleared here: Draw still needs to consume them after ClearPrepared,
        // and Prepare always happens before Draw, so clearing at the entry of Prepare is the safe choice.
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        Array.Clear(_drawTextures);
        Array.Clear(_drawSkip);
        // Encoded resources are held by the command buffer (Device rule 5), so immediate release is safe; unencoded preparation results are already invalid.
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                _pipelines[i, j]?.Dispose();
                _pipelines[i, j] = null;
            }
        _noDepthState?.Dispose(); _noDepthState = null;
        // The functions are held by this class and must be released; the library is not —— it belongs to MTLShaderCompiler's source cache.
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
        _fragmentRoughnessFunction?.Dispose(); _fragmentRoughnessFunction = null;
    }
}
