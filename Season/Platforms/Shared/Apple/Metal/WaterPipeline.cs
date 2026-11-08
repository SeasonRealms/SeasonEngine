// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;
using Season.Rendering;
using Season.Rendering.Effects;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Metal implementation of the P2 water surface material. One <c>DrawPrimitives</c> per water surface: vertices are
/// expanded from <c>[[vertex_id]]</c> (P3 is a 6-vertex unit plane, P4 onwards is a subdiv² grid inside a tile, <c>[[instance_id]]</c> is the tile index),
/// the world matrix and tile parameters are both in the frame buffer and buffer(1), and there is no vertex buffer and no instance buffer.
///
/// The differences from the other three backends are only Metal's own shapes:
/// <list type="bullet">
/// <item>no render pass object: attachments are built on the fly by the <c>MTLRenderPassDescriptor</c> of <see cref="Device.BeginPass"/>,
/// this class only needs to keep the attachment shape consistent with the baked PSO — single color attachment, no depth, single sample, RGBA16Float;</item>
/// <item>no descriptor ring and no fence: Metal binds resources directly, and cross-frame reuse is precluded by "a new command buffer per frame";</item>
/// <item>depth is a <b>sampling source</b> rather than an attachment: occlusion relies on a software depth test in the shader, for the same reason as decals,
/// and is also the premise for SceneDepth remaining samplable throughout the frame.</item>
/// </list>
///
/// The refraction source is a scene-color snapshot taken before drawing begins, copied by <see cref="Device.CopyRenderTarget"/> using a blit encoder outside the Pass;
/// this Pass's color attachment cannot be sampled at the same time, which is the only reason the snapshot exists.
/// Blending is standard alpha blending (SourceAlpha / OneMinusSourceAlpha), bit-for-bit aligned with the other three backends.
/// </summary>
internal sealed class MTLWaterPipeline : Season.Rendering.WaterPipeline
{
    /// <summary>Per-water-surface normal texture resolver: only queries the backend texture dictionary, does not load.</summary>
    readonly Dictionary<string, Texture> _textures;

    IMTLFunction? _vertexFunction, _fragmentFunction;

    /// <summary>This Pass has no depth attachment, so the compare function must be Always — the same tier as decal and fog composite.</summary>
    IMTLDepthStencilState? _noDepthState;

    IMTLRenderPipelineState? _pipeline;

    readonly Texture?[] _drawNormals = new Texture?[WaterMath.MaxWaters];
    /// <summary>This frame's four-level Hi-Z pyramid; when <see cref="HiZEffect.Ready"/> is false the whole set is empty and falls back to the depth map placeholder.</summary>
    readonly Texture?[] _hz = new Texture?[HiZEffect.LevelCount];

    // Product of Prepare, consumed by Draw exactly once.
    // The RT wrapper objects are held as well: the texture reference is just a field within them, so holding the RT guarantees it cannot be recreated mid-frame.
    MTLRenderTarget? _destination;
    MTLRenderTarget? _color, _depth, _refraction;
    IMTLTexture? _colorTexture, _depthTexture, _refractionTexture, _skyTexture;
    MTLStorageBuffer? _frame;
    IMTLCommandBuffer? _command;
    WaterInputs _inputs;
    int _frameIndex;
    bool _disposed;

    internal MTLWaterPipeline(Dictionary<string, Texture> textures)
    {
        _textures = textures;
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, WaterShaders.Msl);
        // MSL preserves the real entry point names (unlike GLSL, which is always main); a miss means the source and the names here have diverged.
        _vertexFunction = library.CreateFunction("VSMain")
            ?? throw new InvalidOperationException("Water shader entry point VSMain not found.");
        _fragmentFunction = library.CreateFunction("PSMain")
            ?? throw new InvalidOperationException("Water shader entry point PSMain not found.");

        using var noDepth = new MTLDepthStencilDescriptor
        {
            DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
        };
        _noDepthState = MTLPipelineFactory.CreateDepthState(noDepth, "water");

        _pipeline = CreatePipeline();
    }

    IMTLRenderPipelineState CreatePipeline()
    {
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = "Season-Water",
            VertexFunction = _vertexFunction,
            FragmentFunction = _fragmentFunction,
            // The water Pass deliberately does not attach depth: depth is a sampling source this frame, and the same resource cannot be both sampled and used as an attachment.
            DepthAttachmentPixelFormat = MTLPixelFormat.Invalid,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = 1,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = MTLPixelFormat.RGBA16Float;
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        // The shader outputs non-premultiplied color; the alpha slot preserves the destination value, and water does not participate in subsequent alpha decisions.
        color.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
        color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        color.SourceAlphaBlendFactor = MTLBlendFactor.Zero;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.One;
        return MTLPipelineFactory.CreateRender(desc);
    }

    static bool Readable(IMTLTexture? texture, MTLTextureType type) => texture != null
        && texture.Handle != IntPtr.Zero && texture.TextureType == type
        && texture.Width > 0 && texture.Height > 0 && (texture.Usage & MTLTextureUsage.ShaderRead) != 0;

    /// <summary>Texture in the backend texture dictionary: the wrapper object is ready and the underlying texture is samplable.</summary>
    static bool Samplable(Texture? texture) => texture != null && Volatile.Read(ref texture.Ready)
        && Readable(texture.Image, MTLTextureType.k2D);

    /// <summary>
    /// Checks the shape and folds the resources into reference snapshots. Returning false means this frame is abandoned: the water surface holds only under the single shape of "color is single-sampled HDR,
    /// depth is the same size as color, and the refraction snapshot is another texture of the same shape"; in all other cases it would rather produce no image than a wrong image.
    /// </summary>
    bool Resolve(in WaterInputs inputs, MTLRenderTarget destination)
    {
        if (inputs.Color is not MTLRenderTarget color || inputs.Depth is not MTLRenderTarget depth
            || inputs.RefractionSource is not MTLRenderTarget refraction
            || inputs.FrameBuffer is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.Buffer.Handle == IntPtr.Zero
            || frame.SizeInBytes < WaterMath.ByteCount || frame.Buffer.Length < (nuint)WaterMath.ByteCount
            || color.Desc.ColorFormat != RtFormat.Rgba16Float || destination.Desc.ColorFormat != RtFormat.Rgba16Float
            || refraction.Desc.ColorFormat != RtFormat.Rgba16Float
            || color.Desc.SampleCount > 1 || depth.Desc.SampleCount > 1 || destination.Desc.SampleCount > 1
            || refraction.Desc.SampleCount > 1
            || depth.Desc.ColorFormat != RtFormat.None || depth.Desc.DepthFormat != RtFormat.D32Float
            || ReferenceEquals(refraction, color)
            // This Pass uses the color target as the blend base at the same time, so the shared layer must hand out the same target.
            || !ReferenceEquals(destination, color)
            || color.Width <= 0 || color.Height <= 0 || color.Width != depth.Width || color.Height != depth.Height
            || color.Width != refraction.Width || color.Height != refraction.Height
            || inputs.Count <= 0 || inputs.Count > WaterMath.MaxWaters) return false;

        var colorTexture = color.ColorTexture;
        var depthTexture = depth.DepthTexture;
        var refractionTexture = refraction.ColorTexture;
        if (colorTexture == null || destination.ColorTexture == null || refractionTexture == null
            || !Readable(colorTexture, MTLTextureType.k2D) || !Readable(refractionTexture, MTLTextureType.k2D)) return false;
        if (depthTexture == null || depthTexture.PixelFormat != MTLPixelFormat.Depth32Float
            || !Readable(depthTexture, MTLTextureType.k2D)) return false;

        _color = color; _depth = depth; _refraction = refraction;
        _colorTexture = colorTexture; _depthTexture = depthTexture; _refractionTexture = refractionTexture;
        _frame = frame;
        return true;
    }

    public override bool Prepare(in WaterInputs inputs, RenderTarget destination)
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

        // Per-water-surface normal map: only queries the dictionary, does not load here. Water surfaces whose name is unregistered or not yet fully uploaded fall back to procedural waves.
        Array.Clear(_drawNormals);
        lock (_textures)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                string? name = inputs.Draws[i].Texture;
                if (name == null) continue;
                if (!_textures.TryGetValue(name, out var texture) || !Samplable(texture)) continue;
                _drawNormals[i] = texture;
            }
        }

        // The sky LUT is the compute texture created in P0: when the name is unregistered (StaticCube tier) it falls back to the depth map placeholder,
        // and the misc.w gate on the shader side ensures it is never actually sampled.
        _skyTexture = _depthTexture;
        if (inputs.SkyView != null)
        {
            lock (_textures)
            {
                if (_textures.TryGetValue(inputs.SkyView, out var sky) && Samplable(sky))
                    _skyTexture = sky.Image;
            }
        }

        // P3 Hi-Z: resolve this frame's pyramid level by level. If any level is missing the whole set falls back, which shares the same source as the shader-side
        // frame[12].x gate (both are governed by HiZEffect.Ready), so a half pyramid never occurs.
        Array.Clear(_hz);
        if (HiZEffect.Ready)
        {
            lock (_textures)
            {
                for (int level = 0; level < _hz.Length; level++)
                {
                    if (!_textures.TryGetValue(HiZEffect.LevelName(level), out var texture) || !Samplable(texture))
                    {
                        Array.Clear(_hz);
                        break;
                    }
                    _hz[level] = texture;
                }
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
        var refractionTexture = _refractionTexture;
        var skyTexture = _skyTexture;
        var hz = _hz;
        var frame = _frame;
        var inputs = _inputs;
        int frameIndex = _frameIndex;
        ClearPrepared();
        var encoder = Device.GraphicsEncoder;
        if (_disposed || destination == null || frame == null || colorTexture == null || depthTexture == null
            || refractionTexture == null || skyTexture == null
            || encoder == null || encoder.Handle == IntPtr.Zero
            || command == null || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex || Device.ActivePassId != RenderPassId.SceneWater
            || !ReferenceEquals(destination, Device.ActiveColorTarget)
            || Device.ActiveColorTexture?.Handle != colorTexture.Handle
            // The attachment shape can only be inferred from the actual bindings: the water Pass's depth must be a sampling source rather than an attachment.
            || Device.ActiveDepthTexture != null) return false;
        var pipeline = _pipeline;
        if (pipeline == null) return false;

        // The main pipeline rebinds the PSO, depth state, culling, and vertex slots on every draw, so the overrides here will not leak into subsequent draws.
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetDepthStencilState(_noDepthState!);
        encoder.SetFrontFacingWinding(MTLWinding.Clockwise);
        // Double-sided: the water surface should be visible from underwater too; at very large pitch angles back-face culling would make the whole water surface disappear.
        encoder.SetCullMode(MTLCullMode.None);
        encoder.SetVertexBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentBuffer(frame.Buffer, 0, 0);
        encoder.SetFragmentTexture(depthTexture, 0);
        encoder.SetFragmentTexture(refractionTexture, 1);
        encoder.SetFragmentTexture(skyTexture, 3);
        // P3: the four-level Hi-Z pyramid occupies texture(4..7); when missing, each level falls back to the depth map placeholder.
        for (int level = 0; level < hz.Length; level++)
            encoder.SetFragmentTexture(hz[level]?.Image ?? depthTexture, (nuint)(4 + level));

        // The MSL side is uint4, copied byte-for-byte, so integer bits must be written; writing float would make the offset out of bounds from the second water surface onwards.
        // From P4 onwards the last three components are the tiled mesh parameters (tilesX, tilesZ, subdiv); when not tiled they are (1,1,0).
        var parameters = stackalloc uint[4];
        int drawn = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var draw = inputs.Draws[i];
            // Water surfaces without a texture still bind a valid texture: the shader gates with material3.x, so it is never actually sampled.
            encoder.SetFragmentTexture(_drawNormals[i]?.Image ?? depthTexture, 2);
            parameters[0] = (uint)draw.Index;
            parameters[1] = (uint)draw.TilesX;
            parameters[2] = (uint)draw.TilesZ;
            parameters[3] = (uint)draw.Subdiv;
            encoder.SetVertexBytes((IntPtr)parameters, 16, 1);
            encoder.SetFragmentBytes((IntPtr)parameters, 16, 1);
            // P4: tiled instancing. The tile index goes through [[instance_id]], and the slot is still carried by buffer(1).
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, (nuint)draw.VertexCount, (nuint)draw.InstanceCount);
            drawn++;
        }
        return drawn > 0;
    }

    void ClearPrepared()
    {
        _destination = null;
        _color = null; _depth = null; _refraction = null;
        _colorTexture = null; _depthTexture = null; _refractionTexture = null;
        _skyTexture = null;
        _frame = null; _command = null; _inputs = default;
        _frameIndex = 0;
        // The per-water-surface normal table is not cleared here: Draw still consumes it after ClearPrepared,
        // and Prepare always happens before Draw, so clearing at the Prepare entry point is what is safe.
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        Array.Clear(_drawNormals);
        Array.Clear(_hz);
        // Encoded resources are held by the command buffer (Device rule 5), so releasing immediately is safe.
        _pipeline?.Dispose(); _pipeline = null;
        _noDepthState?.Dispose(); _noDepthState = null;
        // The functions are held by this class and must be released; the library is not — it is owned by MTLShaderCompiler's source cache.
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
    }
}
