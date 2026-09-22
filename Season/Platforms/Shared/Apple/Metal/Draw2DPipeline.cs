// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.

using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

internal sealed class Draw2DPipeline : IDisposable
{
    internal const string Source = """
        #include <metal_stdlib>
        using namespace metal;
        struct Params {
            float4 originXAxis, yAxis, uvRect, tint, clipRect, parameters, uvClamp;
        };
        struct Vertex { float4 position [[position]]; float2 uv; };
        vertex Vertex immediate_vs(uint index [[vertex_id]], constant Params& p [[buffer(0)]]) {
            float2 corner = float2(index & 1u, index >> 1u);
            Vertex v;
            v.position = float4(p.originXAxis.xy + corner.x * p.originXAxis.zw + corner.y * p.yAxis.xy, 0, 1);
            v.uv = p.uvRect.xy + corner * p.uvRect.zw;
            return v;
        }
        fragment float4 immediate_fs(Vertex v [[stage_in]], constant Params& p [[buffer(0)]],
            texture2d<float> image [[texture(0)]], sampler linearSampler [[sampler(0)]],
            sampler pointSampler [[sampler(1)]]) {
            float2 screenSize = 1.0 / max(fwidth(v.uv), float2(0.00001));
            float2 uv = clamp(v.uv, p.uvClamp.xy, p.uvClamp.zw);
            float4 sampled = p.parameters.x > 0.5
                ? image.sample(pointSampler, uv, level(0))
                : image.sample(linearSampler, uv, level(0));
            if (any(v.position.xy < p.clipRect.xy) || any(v.position.xy >= p.clipRect.zw))
                discard_fragment();
            if (p.parameters.y > 0) {
                float median = max(min(sampled.r, sampled.g), min(max(sampled.r, sampled.g), sampled.b));
                float msdfDistance = median - 0.5;
                float trueDistance = sampled.a - 0.5;
                float distance = msdfDistance * trueDistance > 0 ? msdfDistance : trueDistance;
                float range = max(0.5 * dot(p.parameters.y * p.parameters.zw, screenSize), 1.0);
                return float4(p.tint.rgb, p.tint.a * saturate(range * distance + 0.5));
            }
            return sampled * p.tint;
        }
        """;

    IMTLRenderPipelineState? _pipeline;
    IMTLDepthStencilState? _depth;
    IMTLSamplerState? _linear, _point;

    internal unsafe Draw2DPipeline()
    {
        if (sizeof(Draw2DConstants) != 112)
            throw new InvalidOperationException("Immediate Metal constants must occupy 112 bytes.");
        try
        {
            var library = MTLShaderCompiler.Compile(Device.MtlDevice, Source);
            using var vertex = library.CreateFunction("immediate_vs")
                ?? throw new InvalidOperationException("Missing immediate vertex function.");
            using var fragment = library.CreateFunction("immediate_fs")
                ?? throw new InvalidOperationException("Missing immediate fragment function.");
            using var descriptor = new MTLRenderPipelineDescriptor
            {
                Label = "Season-Immediate2D",
                VertexFunction = vertex,
                FragmentFunction = fragment,
                DepthAttachmentPixelFormat = Device.DepthBufferFormat,
                RasterSampleCount = 1
            };
            var color = descriptor.ColorAttachments[0];
            color.PixelFormat = Device.BackBufferFormat;
            color.BlendingEnabled = true;
            color.RgbBlendOperation = color.AlphaBlendOperation = MTLBlendOperation.Add;
            color.SourceRgbBlendFactor = MTLBlendFactor.SourceAlpha;
            color.DestinationRgbBlendFactor = MTLBlendFactor.OneMinusSourceAlpha;
            color.SourceAlphaBlendFactor = MTLBlendFactor.One;
            color.DestinationAlphaBlendFactor = MTLBlendFactor.Zero;
            _pipeline = Device.MtlDevice.CreateRenderPipelineState(descriptor, out Foundation.NSError? error)
                ?? throw new InvalidOperationException($"Immediate Metal pipeline: {error?.LocalizedDescription}");
            using var depth = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MTLCompareFunction.Always,
                DepthWriteEnabled = false
            };
            _depth = Device.MtlDevice.CreateDepthStencilState(depth)
                ?? throw new InvalidOperationException("Cannot create immediate depth state.");
            _linear = CreateSampler(MTLSamplerMinMagFilter.Linear);
            _point = CreateSampler(MTLSamplerMinMagFilter.Nearest);
        }
        catch { Dispose(); throw; }
    }

    static IMTLSamplerState CreateSampler(MTLSamplerMinMagFilter filter)
    {
        using var descriptor = new MTLSamplerDescriptor
        {
            MinFilter = filter, MagFilter = filter,
            SAddressMode = MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = MTLSamplerAddressMode.ClampToEdge
        };
        return Device.MtlDevice.CreateSamplerState(descriptor)
            ?? throw new InvalidOperationException("Cannot create immediate sampler.");
    }

    internal unsafe void Draw(Texture texture, Draw2DConstants constants)
    {
        var encoder = Device.GraphicsEncoder;
        encoder.SetRenderPipelineState(_pipeline!);
        encoder.SetDepthStencilState(_depth!);
        encoder.SetCullMode(MTLCullMode.None);
        // SetBytes copies each snapshot into command-buffer-owned storage.
        encoder.SetVertexBytes((IntPtr)(&constants), (nuint)sizeof(Draw2DConstants), 0);
        encoder.SetFragmentBytes((IntPtr)(&constants), (nuint)sizeof(Draw2DConstants), 0);
        encoder.SetFragmentTexture(texture.Image, 0);
        encoder.SetFragmentSamplerState(_linear!, 0);
        encoder.SetFragmentSamplerState(_point!, 1);
        encoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);
    }

    public void Dispose()
    {
        _pipeline?.Dispose(); _pipeline = null;
        _depth?.Dispose(); _depth = null;
        _linear?.Dispose(); _linear = null;
        _point?.Dispose(); _point = null;
    }
}
