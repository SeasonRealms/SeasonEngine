// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>每次绘制的完整快照，28 DWORD；root constants 不会被同帧后续绘制覆盖。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Draw2DConstants
{
    public Vector4 OriginXAxis;
    public Vector4 YAxis;
    public Vector4 Uv;
    public Vector4 Color;
    public Vector4 Clip;
    public Vector4 Parameters;
    public Vector4 UvClamp;
}

internal sealed unsafe class Draw2DPipeline : IDisposable
{
    ID3D12RootSignature* _root;
    ID3D12PipelineState* _pipeline;

    internal const string ShaderSource = """
        Texture2D image : register(t0);
        SamplerState linearSampler : register(s0);
        SamplerState pointSampler : register(s1);
        cbuffer DrawParameters : register(b0)
        {
            float4 originXAxis;
            float4 yAxis;
            float4 uvRect;
            float4 tint;
            float4 clipRect;
            float4 parameters;
            float4 uvClamp;
        };
        struct VertexOutput { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        VertexOutput VSMain(uint id : SV_VertexID)
        {
            VertexOutput o;
            float2 corner = float2(id & 1, id >> 1);
            o.position = float4(originXAxis.xy + corner.x * originXAxis.zw + corner.y * yAxis.xy, 0, 1);
            o.uv = uvRect.xy + corner * uvRect.zw;
            return o;
        }
        float4 PSMain(VertexOutput input) : SV_TARGET
        {
            // 裁剪不改 viewport/scissor，完整遵守 pass 对光栅状态的所有权。
            if (any(input.position.xy < clipRect.xy) || any(input.position.xy >= clipRect.zw)) discard;
            float2 uv = clamp(input.uv, uvClamp.xy, uvClamp.zw);
            float4 sampleColor = parameters.x > 0.5
                ? image.Sample(pointSampler, uv) : image.Sample(linearSampler, uv);
            if (parameters.y > 0)
            {
                float median = max(min(sampleColor.r, sampleColor.g), min(max(sampleColor.r, sampleColor.g), sampleColor.b));
                float msdfDistance = median - 0.5;
                float trueDistance = sampleColor.a - 0.5;
                float distance = msdfDistance * trueDistance > 0 ? msdfDistance : trueDistance;
                float2 screenTextureSize = 1.0 / max(fwidth(input.uv), float2(1e-5, 1e-5));
                float range = max(0.5 * dot(parameters.y * parameters.zw, screenTextureSize), 1.0);
                return float4(tint.rgb, tint.a * saturate(range * distance + 0.5));
            }
            return sampleColor * tint;
        }
        """;

    internal Draw2DPipeline()
    {
        try
        {
            CreateRoot();
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void CreateRoot()
    {
        using ComPtr<ID3D10Blob> signature = null;
        using ComPtr<ID3D10Blob> error = null;
        var range = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1 };
        var parameters = stackalloc RootParameter[2];
        parameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &range },
            ShaderVisibility = ShaderVisibility.Pixel
        };
        parameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            Constants = new RootConstants { ShaderRegister = 0, Num32BitValues = 28 },
            ShaderVisibility = ShaderVisibility.All
        };
        var samplers = stackalloc StaticSamplerDesc[2];
        for (int i = 0; i < 2; i++)
            samplers[i] = new StaticSamplerDesc
            {
                Filter = i == 0 ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ShaderRegister = (uint)i, ShaderVisibility = ShaderVisibility.Pixel,
                ComparisonFunc = ComparisonFunc.Always, MaxLOD = float.MaxValue, MaxAnisotropy = 1
            };
        var desc = new RootSignatureDesc
        {
            NumParameters = 2, PParameters = parameters,
            NumStaticSamplers = 2, PStaticSamplers = samplers, Flags = RootSignatureFlags.None
        };
        Marshal.ThrowExceptionForHR(Device.D3D12.SerializeRootSignature(&desc, D3DRootSignatureVersion.Version1,
            signature.GetAddressOf(), error.GetAddressOf()));
        var iid = ID3D12RootSignature.Guid;
        ID3D12RootSignature* root;
        Marshal.ThrowExceptionForHR(Device.D3dDevice->CreateRootSignature(0, signature.Get().GetBufferPointer(),
            signature.Get().GetBufferSize(), &iid, (void**)&root));
        _root = root;
    }

    void CreatePipeline()
    {
        ID3D10Blob* vs = null;
        ID3D10Blob* ps = null;
        try
        {
            vs = ShaderCompiler.CompileShaderFromSource(ShaderSource, "VSMain", "vs_5_0", 0);
            ps = ShaderCompiler.CompileShaderFromSource(ShaderSource, "PSMain", "ps_5_0", 0);
            if (vs == null || ps == null) throw new InvalidOperationException("即时 2D shader 编译失败。");
            var stencil = new DepthStencilopDesc
            {
                StencilFailOp = StencilOp.Keep, StencilDepthFailOp = StencilOp.Keep,
                StencilPassOp = StencilOp.Keep, StencilFunc = ComparisonFunc.Always
            };
            var blend = new RenderTargetBlendDesc
            {
                BlendEnable = 1, SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.Zero, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All
            };
            var desc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _root,
                VS = new ShaderBytecode(vs->GetBufferPointer(), vs->GetBufferSize()),
                PS = new ShaderBytecode(ps->GetBufferPointer(), ps->GetBufferSize()),
                RasterizerState = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 },
                BlendState = new BlendDesc { RenderTarget = new BlendDesc.RenderTargetBuffer { [0] = blend } },
                DepthStencilState = new DepthStencilDesc
                {
                    DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always,
                    StencilEnable = 0, FrontFace = stencil, BackFace = stencil,
                    StencilReadMask = D3D12.DefaultStencilReadMask, StencilWriteMask = D3D12.DefaultStencilWriteMask
                },
                SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1, SampleDesc = new SampleDesc(1, 0), DSVFormat = Format.FormatUnknown
            };
            desc.RTVFormats[0] = Device.BackBufferFormat;
            var iid = ID3D12PipelineState.Guid;
            ID3D12PipelineState* pipeline;
            Marshal.ThrowExceptionForHR(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
            _pipeline = pipeline;
        }
        finally
        {
            if (vs != null) vs->Release();
            if (ps != null) ps->Release();
        }
    }

    internal void Draw(DXTexture texture, Draw2DConstants constants)
    {
        var commandList = Device.GraphicsCommandList;
        // 绑定入口负责复制队列 fence 和资源状态，业务命令不发起屏障。
        texture.EnsureReadyForRendering(commandList);
        commandList->SetGraphicsRootSignature(_root);
        commandList->SetPipelineState(_pipeline);
        commandList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip);
        commandList->SetGraphicsRootDescriptorTable(0, texture.GpuDescriptorHandle);
        commandList->SetGraphicsRoot32BitConstants(1, 28, &constants, 0);
        commandList->DrawInstanced(4, 1, 0, 0);
    }

    /// <summary>仅在 GPU idle 或延迟释放回调中执行。</summary>
    public void Dispose()
    {
        if (_pipeline != null) { _pipeline->Release(); _pipeline = null; }
        if (_root != null) { _root->Release(); _root = null; }
    }
}
