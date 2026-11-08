// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// A0.1's Metal submission side: a single indirect indexed draw renders the number of billboards determined by compute.
///
/// This is the lightest of the four backends, missing three things compared to VKParticleDrawPipeline.
/// First, descriptors: Metal calls SetVertexBuffer directly, with no set and no per-frame descriptor ring, so even the Device.WaitForFrameSlot
/// reasoning that "a set from three frames ago may still be referenced by a pending command buffer" does not exist.
/// Second, barriers: read-after-write dependencies are honored by the driver's hazard tracking, so Prepare records nothing,
/// and on this backend IGraphics.TryPrepareIndirectArgs only performs an admission check.
/// Third, deferred release: already-encoded resources are held by the command buffer's retained references, so Dispose can let go immediately.
///
/// The PSO is not baked at construction time but baked on demand inside Draw, for the same reason as the other two backends: the facts that determine the attachment shape only hold
/// after entering the Pass, whereas Prepare must be outside the Pass. Creating a PSO is a device call, not an encoder call, so it is legal inside the Pass.
///
/// "Attachment shape" in Metal is literally three pixel formats — color, depth, velocity — because Metal bakes the attachment formats directly
/// into the PSO, and a mismatch means creation failure or the draw being discarded. So the key here is not an enum like in Vulkan, but literally the format triple:
/// Depth == Invalid means this Pass has no depth attachment (a NoImplicitDepth target), in which case enabling depth testing is illegal;
/// Velocity == Invalid means a single-target shape, with the PSO having only one color attachment. All three can only be inferred from the actual bindings recorded by the Device.
/// </summary>
internal sealed class MTLParticleDrawPipeline : Season.Rendering.ParticleDrawPipeline
{
    /// <summary>The 6 indices of the unit quad, with a winding order matching the 0,1,2,1,3,2 shared by the other three backends; the corners are expanded from the low two bits of [[vertex_id]].</summary>
    static readonly ushort[] QuadIndices = [0, 1, 2, 1, 3, 2];

    /// <summary>PSO signature. Metal bakes the attachment formats into the PSO, so the signature is the format
    /// combination of the actual attachments. The sample count is part of it as well: the Msaa4x tier binds a
    /// multisampled color attachment, and a PSO baked for 1x would be rejected by the encoder.</summary>
    readonly record struct PipelineKey(MTLPixelFormat Color, MTLPixelFormat Depth, MTLPixelFormat Velocity,
        MTLPixelFormat Normal, nuint Samples);

    readonly Dictionary<PipelineKey, IMTLRenderPipelineState> _pipelines = [];

    IMTLBuffer? _quadIndices;

    // The shader functions are compiled at construction time, the same choice as the DX backend. This is the only stage in the whole chain with no offline compilation means
    // (there is no xcrun metal on Windows); leaving its failure to Draw means throwing an exception inside the Pass, whereas doing it at construction time lets
    // CreateParticleDrawPipeline catch it and return null, so the particle effect is not registered at all and the image falls back to its state before the change.
    IMTLFunction? _vertexFunction, _fragmentFunction, _fragmentVelocityFunction;

    /// <summary>Depth test on, depth write off: particles are not sorted among themselves, and writing depth would only let later-drawn ones be occluded by earlier-drawn ones.
    /// Scene depth is forward (the main pipeline uses Less/LessEqual), so this uses LessEqual, matching the shape of Pipeline.TransparentDepthState.</summary>
    IMTLDepthStencilState? _depthState;

    /// <summary>The variant with no depth attachment. Metal requires the compare function to be Always here, otherwise it is undefined behavior,
    /// the same choice as MTLFogCompositePipeline's depth-less full-screen pipeline.</summary>
    IMTLDepthStencilState? _noDepthState;

    // The product of Prepare, consumed by Draw and consumed only once.
    MTLRenderTarget? _color;
    Season.Rendering.RenderTarget? _depth;
    MTLStorageBuffer? _instances, _args, _frame;
    IMTLTexture? _colorTexture;
    IMTLCommandBuffer? _command;
    int _frameIndex;
    bool _prepared;
    bool _disposed;

    internal MTLParticleDrawPipeline()
    {
        try
        {
            _quadIndices = Device.ResourceManager.CreateIndexBuffer(QuadIndices);
            _quadIndices.Label = "Season-ParticleQuadIndices";

            // MTLShaderCompiler caches IMTLLibrary by source string, so both fragment variants share the same vertex library,
            // and each source is compiled only once, even if multiple PSOs are later baked for different color formats.
            _vertexFunction = CreateFunction(Season.Rendering.Effects.ParticleSystemEffect.DrawVertexMsl,
                "VSMain");
            _fragmentFunction = CreateFunction(Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentMsl,
                "PSMain");
            _fragmentVelocityFunction = CreateFunction(
                Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentVelocityMsl, "PSMain");

            using var depth = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MTLCompareFunction.LessEqual, DepthWriteEnabled = false,
            };
            _depthState = MTLPipelineFactory.CreateDepthState(depth, "particle");
            using var noDepth = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MTLCompareFunction.Always, DepthWriteEnabled = false,
            };
            _noDepthState = MTLPipelineFactory.CreateDepthState(noDepth, "particle-no-depth");
        }
        catch { Dispose(); throw; }
    }

    static IMTLFunction CreateFunction(string source, string entryPoint)
    {
        var library = MTLShaderCompiler.Compile(Device.MtlDevice, source);
        // MSL preserves the real entry-point name (unlike GLSL, which is always main), so a failure to find it means the source and the name here have diverged.
        return library.CreateFunction(entryPoint)
            ?? throw new InvalidOperationException($"Particle shader entry point not found: {entryPoint}.");
    }

    IMTLRenderPipelineState? GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var cached)) return cached;
        bool velocity = key.Velocity != MTLPixelFormat.Invalid;

        // The functions were already compiled at construction time; only baking the PSO is left here — because only it needs the attachment formats, and the formats only hold once inside the Pass.
        // Note: the functions must not be disposed here, as they are still needed for PSOs of subsequent color formats.
        // No VertexDescriptor: the corners come from [[vertex_id]] and the instance values come from a read-only buffer in the vertex stage,
        // so vertex slots 0/1 in this pipeline are not [[stage_in]] streams and do not conflict with the main pipeline's vertex layout.
        using var desc = new MTLRenderPipelineDescriptor
        {
            Label = velocity ? "Season-ParticleDraw-Vel" : "Season-ParticleDraw",
            VertexFunction = _vertexFunction,
            FragmentFunction = velocity ? _fragmentVelocityFunction : _fragmentFunction,
            DepthAttachmentPixelFormat = key.Depth,
            StencilAttachmentPixelFormat = MTLPixelFormat.Invalid,
            RasterSampleCount = key.Samples,
        };
        var color = desc.ColorAttachments[0];
        color.PixelFormat = key.Color;
        // The fragment shader outputs premultiplied alpha, so the source factor is One rather than SourceAlpha.
        color.BlendingEnabled = true;
        color.RgbBlendOperation = MTLBlendOperation.Add;
        color.AlphaBlendOperation = MTLBlendOperation.Add;
        color.SourceRgbBlendFactor = MTLBlendFactor.One;
        color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        color.SourceAlphaBlendFactor = MTLBlendFactor.One;
        color.DestinationAlphaBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
        if (velocity)
        {
            // The velocity slot write mask is zeroed, consistent with the main pipeline's existing choice for transparent geometry (slot 1
            // of Pipeline.CreatePipelineState): preserving the velocity of the opaque layer below is better than writing 0, which would make TAA accumulate moving particles as if they were a static background.
            var motion = desc.ColorAttachments[1];
            motion.PixelFormat = key.Velocity;
            motion.BlendingEnabled = false;
            motion.WriteMask = MTLColorWriteMask.None;

            // Same for the A0.2 normal slot: particles are not a surface that SSR/decals should see. An attachment with a zero write mask does not require the fragment function to have
            // a corresponding [[color(2)]] output, so the velocity variant's fragment function is reused.
            if (key.Normal != MTLPixelFormat.Invalid)
            {
                var normal = desc.ColorAttachments[2];
                normal.PixelFormat = key.Normal;
                normal.BlendingEnabled = false;
                normal.WriteMask = MTLColorWriteMask.None;
            }
        }

        var pipeline = MTLPipelineFactory.CreateRender(desc);
        _pipelines[key] = pipeline;
        return pipeline;
    }

    public override bool Prepare(in Season.Rendering.ParticleDrawInputs inputs)
    {
        ClearPrepared();
        var command = Device.GraphicsCommandBuffer;
        // A Metal Pass is an encoder, so "outside the Pass" is equivalent to there being no render encoder currently;
        // recording anything while an encoder exists violates the PassDesc contract, so it is better to abandon this frame's submission.
        if (_disposed || Device.GraphicsEncoder != null || command == null || command.Handle == IntPtr.Zero
            || _quadIndices == null || _depthState == null || _noDepthState == null
            || _vertexFunction == null || _fragmentFunction == null || _fragmentVelocityFunction == null
            || inputs.Color is not MTLRenderTarget color || color.ColorTexture == null
            || color.Desc.SampleCount != 1
            || inputs.Instances is not MTLStorageBuffer instances || instances.Buffer == null
            || instances.SizeInBytes < Season.Rendering.ParticleInstance.SizeInBytes
            || inputs.IndirectArgs is not MTLStorageBuffer args || args.Buffer == null
            || args.Usage != Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs
            || args.SizeInBytes < Season.Rendering.IndirectDrawIndexedArgs.SizeInBytes
            || inputs.Frame is not MTLStorageBuffer frame || frame.Buffer == null
            || frame.SizeInBytes < Season.Rendering.ParticleDraw.FrameBytes) return false;

        _color = color; _depth = inputs.Depth; _colorTexture = color.ColorTexture;
        _instances = instances; _args = args; _frame = frame;
        _command = command; _frameIndex = Device.FrameIndex;
        _prepared = true;
        return true;
    }

    public override bool Draw()
    {
        bool prepared = _prepared;
        var color = _color; var depth = _depth;
        var instances = _instances; var args = _args; var frame = _frame;
        var colorTexture = _colorTexture; var command = _command;
        int frameIndex = _frameIndex;
        ClearPrepared();
        if (!prepared || _disposed || color == null || colorTexture == null || command == null
            || instances == null || args == null || frame == null) return false;

        var encoder = Device.GraphicsEncoder;
        // Metal has no fence retirement value to compare against, so the identity check is "managed reference to the same command buffer + the same
        // FrameIndex": the command buffer is recreated every frame, so both holding at once rules out reusing the prepare result across frames.
        // Msaa4x: the encoder binds the backend-private multisample color instead of the shared target by design, and the
        // multisample one resolves into exactly this target at pass end, so the two forms mean the same surface.
        var activeColorTexture = Device.ActiveColorTexture;
        bool msaaScene = Device.MsaaSampleCount > 1 && Device.IsMsaaScenePass(Device.ActivePassId)
            && activeColorTexture != null && activeColorTexture.SampleCount > 1
            && ReferenceEquals(Device.MsaaSceneTarget.ColorTexture, activeColorTexture);
        if (encoder == null || encoder.Handle == IntPtr.Zero
            || command.Handle == IntPtr.Zero || !ReferenceEquals(command, Device.GraphicsCommandBuffer)
            || frameIndex != Device.FrameIndex
            || Device.ActivePassId is not (Season.Rendering.RenderPassId.Scene
                or Season.Rendering.RenderPassId.SceneOpaque
                or Season.Rendering.RenderPassId.SceneTransparent)
            || !ReferenceEquals(color, Device.ActiveColorTarget)
            || (activeColorTexture?.Handle != colorTexture.Handle && !msaaScene)) return false;
        // null == use the depth built into that target, which likewise requires that null was seen during preparation, otherwise the depth semantics have already changed.
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;

        // The attachment shape can only be inferred from the actual bindings: the absence of an explicit depth does not mean there is no depth attachment, since the target's own one counts too;
        // the velocity slot only exists in the three-target shape, and the PassDesc held during preparation cannot infer this.
        var velocityTexture = Device.ActiveVelocityTexture;
        var velocityFormat = MTLPixelFormat.Invalid;
        var normalFormat = MTLPixelFormat.Invalid;
        if (velocityTexture != null)
        {
            if (velocityTexture.PixelFormat != MTLPixelFormat.RG16Float) return false;
            velocityFormat = velocityTexture.PixelFormat;
            normalFormat = Device.ActiveNormalTexture?.PixelFormat ?? MTLPixelFormat.Invalid;
        }
        var depthFormat = Device.ActiveDepthTexture?.PixelFormat ?? MTLPixelFormat.Invalid;
        var pipeline = GetOrCreatePipeline(new PipelineKey(colorTexture.PixelFormat, depthFormat, velocityFormat,
            normalFormat, activeColorTexture?.SampleCount ?? 1));
        if (pipeline == null) return false;

        // The main pipeline rebinds the PSO, depth state, culling and vertex slots 0..4 on every draw (see Pipeline.DrawPrimitive),
        // so the overrides here will not leak into subsequent draws.
        encoder.SetRenderPipelineState(pipeline);
        encoder.SetDepthStencilState(depthFormat == MTLPixelFormat.Invalid ? _noDepthState! : _depthState!);
        // The billboard winding order depends on the combination of the camera basis and the index order; turning off culling is more reliable than flipping the sign in the shader.
        encoder.SetCullMode(MTLCullMode.None);
        encoder.SetVertexBuffer(instances.Buffer, 0, 0);
        encoder.SetVertexBuffer(frame.Buffer, 0, 1);
        // The instance count comes from the second field of the argument buffer, whose value the CPU side never knows.
        // The overload is drawIndexedPrimitives:indexType:indexBuffer:indexBufferOffset:indirectBuffer:indirectBufferOffset:
        // — the index buffer comes first and the argument buffer second, and both offsets are nuint.
        encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, MTLIndexType.UInt16,
            _quadIndices!, 0, args.Buffer, 0);
        return true;
    }

    void ClearPrepared()
    {
        _prepared = false;
        _color = null; _depth = null; _colorTexture = null;
        _instances = null; _args = null; _frame = null;
        _command = null; _frameIndex = 0;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPrepared();
        // Already-encoded resources are held by the command buffer (Device rule 5), so releasing the wrapper objects immediately is safe,
        // with no need for the DX and VK style fence-gated deferred release queue; the not-yet-encoded prepare result has already been invalidated.
        foreach (var pipeline in _pipelines.Values) pipeline?.Dispose();
        _pipelines.Clear();
        _depthState?.Dispose(); _depthState = null;
        _noDepthState?.Dispose(); _noDepthState = null;
        // The functions are owned by this class and must be released; the library is not — it is owned by MTLShaderCompiler's source cache.
        _vertexFunction?.Dispose(); _vertexFunction = null;
        _fragmentFunction?.Dispose(); _fragmentFunction = null;
        _fragmentVelocityFunction?.Dispose(); _fragmentVelocityFunction = null;
        _quadIndices?.Dispose(); _quadIndices = null;
    }
}
