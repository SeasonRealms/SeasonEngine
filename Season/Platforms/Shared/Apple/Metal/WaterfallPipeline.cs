// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using Season.Rendering;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal implementation of the P1 waterfall curtain. One <c>DrawPrimitives</c> per curtain: vertices are expanded from
/// <c>[[vertex_id]]</c> along the control points stored in the frame buffer, the slot and mesh parameters are in buffer(1),
/// and there is no vertex buffer and no instance buffer.
///
/// The differences from the other three backends are only Metal's own shapes:
/// <list type="bullet">
/// <item>no render pass object: attachments are built on the fly by the <c>MTLRenderPassDescriptor</c> of <see cref="Device.BeginPass"/>,
/// this class only needs to keep the attachment shape consistent with the baked PSO — single color attachment, no depth, single sample, RGBA16Float;</item>
/// <item>no descriptor ring and no fence: Metal binds resources directly, and cross-frame reuse is precluded by "a new command buffer per frame";</item>
/// <item>depth is a <b>sampling source</b> rather than an attachment: occlusion relies on a software depth test in the shader,
/// which is also the premise for SceneDepth remaining samplable throughout the frame.</item>
/// </list>
///
/// There is no refraction snapshot: the curtain never reads the scene color, so the color attachment is itself the blend base.
/// Any curtain that declared a texture which failed to resolve is skipped whole — sampling the depth placeholder would turn
/// the foam channel into a solid value, which reads as "a white sheet" rather than "a missing texture".
/// Blending is standard alpha blending (SourceAlpha / OneMinusSourceAlpha), bit-for-bit aligned with the other three backends.
/// </summary>
internal sealed class MTLWaterfallPipeline : Season.Rendering.WaterfallPipeline
{
    /// <summary>Per-curtain texture resolver: only queries the backend texture dictionary, does not load.</summary>
    readonly Dictionary<string, Texture> _textures;

    IMTLFunction? _vertexFunction, _fragmentFunction;

    /// <summary>This Pass has no depth attachment, so the compare function must be Always — the same tier as decal and water.</summary>
    IMTLDepthStencilState? _noDepthState;

    IMTLRenderPipelineState? _pipeline;

    readonly Texture?[] _masks = new Texture?[WaterfallMath.MaxSheets];
    readonly Texture?[] _normals = new Texture?[WaterfallMath.MaxSheets];
    readonly Texture?[] _flows = new Texture?[WaterfallMath.MaxSheets];
    readonly bool[] _skip = new bool[WaterfallMath.MaxSheets];

    // Product of Prepare, consumed by Draw exactly once.
    // The RT wrapper objects are held as well: the texture reference is just a field within them, so holding the RT guarantees it cannot be recreated mid-frame.
    MTLRenderTarget? _destination;
    IMTLTexture? _colorTexture, _depthTexture;
    MTLStorageBuffer? _frame;
    IMTLCommandBuffer? _command;
    WaterfallInputs _inputs;
    int _frameIndex;
    bool _disposed;

    internal MTLWaterfallPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, WaterfallShaders.Msl);
        // MSL preserves the real entry point names (unlike GLSL, which is always main); a miss means the source and the names here have diverged.
        _vertexFunction = library.CreateFunction("VSMain")
            ?? throw new InvalidOperationException("Waterfall shader entry point VSMain not found.");
        _fragmentFunction = library.CreateFunction("PSMain")
            ?? throw new InvalidOperationException("Waterfall shader entry point PSMain not found.");

        using var noDepth = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
        };
        _noDepthState = Device.MtlDevice.CreateDepthStencilState(noDepth)
            ?? throw new InvalidOperationException("Failed to create waterfall depth state.");

        _pipeline = CreatePipeline();
    }

    IMTLRenderPipelineState CreatePipeline()
    {
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = "Season-Waterfall",
            VertexFunction = _vertexFunction,
            FragmentFunction = _fragmentFunction,
            // The waterfall Pass deliberately does not attach depth: depth is a sampling source this frame, and the same resource cannot be both sampled and used as an attachment.
            DepthAttachmentPixelFormat = MTLPixelFormat.Invalid,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = MTLPixelFormat.RGBA16Float;
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        // The shader outputs non-premultiplied color; the alpha slot preserves the destination value, and the curtain does not participate in later alpha decisions.
        color.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
        color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        color.SourceAlphaBlendFactor = MTLBlendFactor.Zero;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.One;
        var pipeline = Device.MtlDevice.CreateRenderPipelineState(desc, out NSError? error);
        return pipeline ?? throw new InvalidOperationException(
            $"Failed to create waterfall PSO: {error?.LocalizedDescription ?? "(no NSError)"}");
    }

    static bool Readable(IMTLTexture? texture, MTLTextureType type) => texture != null
        && texture.Handle != IntPtr.Zero && texture.TextureType == type
        && texture.Width > 0 && texture.Height > 0 && (texture.Usage & MTLTextureUsage.ShaderRead) != 0;

    /// <summary>Texture in the backend texture dictionary: the wrapper object is ready and the underlying texture is samplable.</summary>
    static bool Samplable(Texture? texture) => texture != null && Volatile.Read(ref texture.Ready)
        && Readable(texture.Image, MTLTextureType.k2D);

    /// <summary>
    /// Checks the shape and folds the resources into reference snapshots. Returning false means this frame is abandoned:
    /// the curtain holds only under the single shape of "color is single-sampled HDR, depth is the same size as color";
    /// in all other cases it would rather produce no image than a wrong image.
    /// </summary>
    bool Resolve(in WaterfallInputs inputs, MTLRenderTarget destination)
    {
        if (inputs.Color is not MTLRenderTarget color || inputs.Depth is not MTLRenderTarget depth
            || inputs.FrameBuffer is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.Buffer.Handle == IntPtr.Zero
            || frame.SizeInBytes < WaterfallMath.ByteCount || frame.Buffer.Length < (nuint)WaterfallMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount > 1 || depth.Desc.SampleCount > 1 || destination.Desc.SampleCount > 1
            || depth.Desc.ColorFormat != RtFormat.None || depth.Desc.DepthFormat != RtFormat.D32Float
            // This Pass uses the color target as the blend base at the same time, so the shared layer must hand out the same target.
            || !ReferenceEquals(destination, color)
            || color.Width <= 0 || color.Height <= 0 || color.Width != depth.Width || color.Height != depth.Height
            || inputs.Count <= 0 || inputs.Count > WaterfallMath.MaxSheets) return false;

        var colorTexture = color.ColorTexture;
        var depthTexture = depth.DepthTexture;
        if (colorTexture == null || destination.ColorTexture == null
            || !Readable(colorTexture, MTLTextureType.k2D)) return false;
        if (depthTexture == null || depthTexture.PixelFormat != MTLPixelFormat.Depth32Float
            || !Readable(depthTexture, MTLTextureType.k2D)) return false;

        _colorTexture = colorTexture; _depthTexture = depthTexture;
        _frame = frame;
        return true;
    }

    public override bool Prepare(in WaterfallInputs inputs, RenderTarget destination)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // A Metal Pass is the encoder, so "outside the Pass" is equivalent to there being no render encoder currently.
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || destination is not MTLRenderTarget target || !Resolve(inputs, target)) return false;

        _inputs = inputs;
        _destination = target;
        _command = command;
        _frameIndex = Device.FrameIndex;

        // Per-curtain textures: only query the dictionary, do not load here.
        Array.Clear(_masks); Array.Clear(_normals); Array.Clear(_flows); Array.Clear(_skip);
        lock (_textures)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                var draw = inputs.Draws[i];
                _skip[i] = !ResolveTexture(draw.Mask, _masks, i)
                    || !ResolveTexture(draw.Normal, _normals, i)
                    || !ResolveTexture(draw.Flow, _flows, i);
            }

            bool ResolveTexture(string? name, Texture?[] table, int index)
            {
                table[index] = null;
                if (name == null) return true;
                if (!_textures.TryGetValue(name, out var texture) || !Samplable(texture)) return false;
                table[index] = texture;
                return true;
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
        var frame = _frame;
        var inputs = _inputs;
        int frameIndex = _frameIndex;
        ClearPrepared();
        var encoder = Device.GraphicsEncoder;
        if (_disposed || destination == null || frame == null || colorTexture == null || depthTexture == null
            || encoder == null || encoder.Handle == IntPtr.Zero
            || command == null || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex || Device.ActivePassId != RenderPassId.SceneWaterfall
            || !ReferenceEquals(destination, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle
            // The attachment shape can only be inferred from the actual bindings: the waterfall Pass's depth must be a sampling source rather than an attachment.
            || Device.ActiveDepthTexture != null) return false;
        var pipeline = _pipeline;
        if (pipeline == null) return false;

        // The main pipeline rebinds the PSO, depth state, culling, and vertex slots on every draw, so the overrides here will not leak into subsequent draws.
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetDepthStencilState(_noDepthState!);
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);
        // Double-sided: the section is a closed ring, and its far wall is exactly the half that reads as thickness.
        encoder.SetCullMode(MTLCullMode.None);
        encoder.SetVertexBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentTexture(depthTexture, 0);

        // The MSL side is uint4, copied byte-for-byte, so integer bits must be written; writing float would make the offset
        // out of bounds from the second curtain onwards. The last two components are the mesh parameters (columns, subdiv).
        var parameters = stackalloc uint[4];
        int drawn = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (_skip[i]) continue;
            var draw = inputs.Draws[i];
            if (draw.VertexCount <= 0) continue;
            // Missing maps bind the depth placeholder: the shader's three flags gate them, so it is never actually sampled.
            encoder.SetFragmentTexture(_masks[i]?.Image ?? depthTexture, 1);
            encoder.SetFragmentTexture(_normals[i]?.Image ?? depthTexture, 2);
            encoder.SetFragmentTexture(_flows[i]?.Image ?? depthTexture, 3);
            parameters[0] = (uint)draw.Index;
            parameters[1] = (uint)draw.Columns;
            parameters[2] = (uint)draw.Subdiv;
            parameters[3] = 0u;
            encoder.SetVertexBytes((IntPtr)parameters, 16, 1);
            encoder.SetFragmentBytes((IntPtr)parameters, 16, 1);
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, (nuint)draw.VertexCount, 1);
            drawn++;
        }
        return drawn > 0;
    }

    void ClearPrepared()
    {
        _destination = null;
        _colorTexture = null; _depthTexture = null;
        _frame = null; _command = null; _inputs = default;
        _frameIndex = 0;
        // The per-curtain tables are not cleared here: Draw still consumes them after ClearPrepared,
        // and Prepare always happens before Draw, so clearing at the Prepare entry point is what is safe.
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        Array.Clear(_masks);
        Array.Clear(_normals);
        Array.Clear(_flows);
        Array.Clear(_skip);
        // Encoded resources are held by the command buffer (Device rule 5), so releasing immediately is safe.
        _pipeline?.Dispose(); _pipeline = null;
        _noDepthState?.Dispose(); _noDepthState = null;
        // The functions are held by this class and must be released; the library is not — it is owned by MTLShaderCompiler's source cache.
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
    }
}
