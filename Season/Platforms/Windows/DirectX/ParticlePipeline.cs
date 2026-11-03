// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Season.Rendering.Effects;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// D3D12 submission side of A0.1: one ExecuteIndirect draws a compute-determined number of billboards.
///
/// Compared with DXFogCompositePipeline, a whole set of mechanisms is deliberately omitted here - no descriptor heap, no per-frame binding ring,
/// no native resource AddRef keep-alive. The reason is that particles only consume buffers and not textures, and a root SRV can directly take a GPU virtual address,
/// so the problem "a descriptor may be reused by an in-flight frame" simply does not exist.
///
/// The PSO is not baked at construction time but baked on demand in Draw: the three things that determine the OM shape (sample count, whether a velocity slot is bound,
/// color format) are all only determined after entering the Pass, while Prepare must execute outside the Pass. Creating a PSO is a device call
/// rather than command recording, so doing it inside the Pass is legal, and after caching by key each shape pays the cost only once.
///
/// The blend and depth state are copied from the Scene transparent branch of the main pipeline, but the OM format and sample count are asked directly of the bound resource,
/// rather than reading the global tier of Device. The two agree during normal operation, but once they disagree (the MSAA tier binds
/// MsaaSceneRT, and the offscreen or windowless path may in turn be a single-sample target), the global value would bake out a PSO that does not match the attachment,
/// and this mismatch can only be reported by the debug layer at runtime. The resource description itself is the source of truth for the OM, so it is taken as authoritative.
/// </summary>
internal sealed unsafe class DXParticleDrawPipeline : ParticleDrawPipeline
{
    /// <summary>Root parameter indices, corresponding one-to-one with t0/t1 in ParticleSystemEffect.DrawHlsl.</summary>
    const uint InstancesRoot = 0;
    const uint FrameRoot = 1;

    /// <summary>The OM signature of the PSO. Velocity being Unknown means this Pass binds only one render target,
    /// so "whether there is a velocity slot" and "the velocity slot format" are expressed by the same field, and no key with contradictory values can occur.
    /// The same holds for Normal (slot 2 of A0.2), and it only appears when Velocity is not Unknown.</summary>
    readonly record struct PipelineKey(Format Color, Format Velocity, Format Normal, uint SampleCount, uint SampleQuality);

    readonly Dictionary<PipelineKey, nint> _pipelines = [];
    ID3D12RootSignature* _signature;
    ID3D12CommandSignature* _commands;
    ID3D10Blob* _vertex;
    ID3D10Blob* _pixel;
    ID3D10Blob* _pixelVelocity;

    // The product of Prepare, consumed by Draw exactly once.
    ID3D12GraphicsCommandList* _command;
    DXRenderTarget? _color;
    RenderTarget? _depth;
    DXStorageBuffer? _instances;
    DXStorageBuffer? _args;
    DXStorageBuffer? _frame;
    ulong _stamp;
    uint _frameIndex;
    bool _prepared;
    bool _disposed;

    internal DXParticleDrawPipeline()
    {
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    void Initialize()
    {
        // Two root SRVs, visible only to the vertex stage. There is no input layout, so we cannot declare
        // AllowInputAssemblerInputLayout - that flag requires the PSO to actually carry input elements.
        var parameters = stackalloc RootParameter[2];
        parameters[0] = new()
        {
            ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        parameters[1] = new()
        {
            ParameterType = RootParameterType.TypeSrv,
            Descriptor = new RootDescriptor { ShaderRegister = 1, RegisterSpace = 0 },
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        var root = new RootSignatureDesc { NumParameters = 2, PParameters = parameters };
        using ComPtr<ID3D10Blob> blob = null;
        using ComPtr<ID3D10Blob> error = null;
        Device.CheckResult(Device.D3D12.SerializeRootSignature(&root, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), error.GetAddressOf()));
        ID3D12RootSignature* signature = null;
        var iid = ID3D12RootSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateRootSignature(0, blob.Get().GetBufferPointer(),
            blob.Get().GetBufferSize(), &iid, (void**)&signature));
        _signature = signature;

        // The command signature describes only a single DrawIndexed record and does not change any root parameter, so root signature must be passed as null;
        // ByteStride is the same constant as IndirectDrawIndexedArgs in the shared layer, and a layout drift will be exposed here.
        var argument = new IndirectArgumentDesc { Type = IndirectArgumentType.DrawIndexed };
        var commandDesc = new CommandSignatureDesc
        {
            ByteStride = IndirectDrawIndexedArgs.SizeInBytes,
            NumArgumentDescs = 1,
            PArgumentDescs = &argument,
            NodeMask = 0,
        };
        ID3D12CommandSignature* commands = null;
        iid = ID3D12CommandSignature.Guid;
        Device.CheckResult(Device.D3dDevice->CreateCommandSignature(&commandDesc, null, &iid, (void**)&commands));
        _commands = commands;

        _vertex = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "VSMain", "vs_5_0");
        _pixel = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "PSMain", "ps_5_0");
        _pixelVelocity = ShaderCompiler.CompileShaderFromSource(ParticleSystemEffect.DrawHlsl, "PSMainVelocity", "ps_5_0");
    }

    ID3D12PipelineState* GetOrCreatePipeline(in PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out nint cached)) return (ID3D12PipelineState*)cached;
        bool velocity = key.Velocity != Format.FormatUnknown;
        bool normal = velocity && key.Normal != Format.FormatUnknown;

        var stencil = new DepthStencilopDesc
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always,
        };
        // The pixel shader outputs premultiplied alpha, so the source factor is One rather than SrcAlpha.
        var blend = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.One,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.InvSrcAlpha,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        // The velocity slot write mask is zeroed, consistent with the main pipeline's existing choice for transparent geometry: preserving the velocity of the opaque layer below
        // is better than writing 0, which would make TAA accumulate moving particles as if they were a static background.
        var velocityBlend = blend;
        velocityBlend.BlendEnable = 0;
        velocityBlend.DestBlend = Blend.Zero;
        velocityBlend.DestBlendAlpha = Blend.Zero;
        velocityBlend.RenderTargetWriteMask = 0;

        var desc = new GraphicsPipelineStateDesc
        {
            PRootSignature = _signature,
            VS = new(_vertex->GetBufferPointer(), _vertex->GetBufferSize()),
            PS = velocity
                ? new(_pixelVelocity->GetBufferPointer(), _pixelVelocity->GetBufferSize())
                : new(_pixel->GetBufferPointer(), _pixel->GetBufferSize()),
            RasterizerState = new()
            {
                FillMode = FillMode.Solid,
                // The winding order of the billboard depends on the combination of the camera basis and the index order; disabling culling is more reliable than flipping the sign in the shader.
                CullMode = CullMode.None,
                FrontCounterClockwise = 0,
                DepthClipEnable = 1,
                MultisampleEnable = key.SampleCount > 1 ? 1u : 0u,
            },
            // Depth test on, depth write off: particles are not sorted against each other, writing depth would only let later-drawn ones be occluded by earlier-drawn ones.
            DepthStencilState = new()
            {
                DepthEnable = 1,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunc.LessEqual,
                StencilEnable = 0,
                StencilReadMask = D3D12.DefaultStencilReadMask,
                StencilWriteMask = D3D12.DefaultStencilWriteMask,
                FrontFace = stencil,
                BackFace = stencil,
            },
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = normal ? 3u : (velocity ? 2u : 1u),
            SampleDesc = new(key.SampleCount, key.SampleQuality),
            DSVFormat = Device.DepthBufferFormat,
        };
        desc.BlendState.IndependentBlendEnable = velocity ? 1u : 0u;
        desc.BlendState.RenderTarget[0] = blend;
        desc.BlendState.RenderTarget[1] = velocity ? velocityBlend : blend;
        desc.RTVFormats[0] = key.Color;
        if (velocity) desc.RTVFormats[1] = key.Velocity;
        // The normal slot follows the same rule as the velocity slot: write mask zero, preserving the normal of the opaque surface below. The shader does not need to output SV_Target2,
        // a slot with a zero write mask does not require a corresponding output, so the pixel shader of the velocity variant is reused.
        if (normal)
        {
            desc.BlendState.RenderTarget[2] = velocityBlend;
            desc.RTVFormats[2] = key.Normal;
        }

        ID3D12PipelineState* pipeline = null;
        var iid = ID3D12PipelineState.Guid;
        Device.CheckResult(Device.D3dDevice->CreateGraphicsPipelineState(&desc, &iid, (void**)&pipeline));
        _pipelines[key] = (nint)pipeline;
        return pipeline;
    }

    public override bool Prepare(in ParticleDrawInputs inputs)
    {
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        var cmd = Device.GraphicsCommandList;
        // ActiveTarget being non-null means we are inside a Pass; recording a barrier here violates the PassDesc contract, so it is better to give up this frame's submission.
        if (_disposed || cmd == null || Device.ActiveTarget != null
            || Pipeline.UnitQuadIndexBuffer == null
            || inputs.Color is not DXRenderTarget color || color.Color == null
            || color.RtvIndex == DXRenderTarget.NoRtv
            || inputs.Instances is not DXStorageBuffer instances || instances.Resource == null
            || instances.SizeInBytes < ParticleInstance.SizeInBytes
            || inputs.IndirectArgs is not DXStorageBuffer args || args.Resource == null
            || args.Usage != StorageBufferUsage.ComputeAndIndirectArgs
            || args.SizeInBytes < IndirectDrawIndexedArgs.SizeInBytes
            || inputs.Frame is not DXStorageBuffer frame || frame.Resource == null
            || frame.SizeInBytes < ParticleDraw.FrameBytes) return false;
        // The transition of the argument buffer is the responsibility of IGraphics.TryPrepareIndirectArgs, which must have already run first;
        // here we only verify the landing state, to avoid feeding a buffer still sitting in UnorderedAccess to ExecuteIndirect.
        if (args.CurrentState != ResourceStates.IndirectArgument) return false;

        // The instance buffer has just been written by compute and sits in UnorderedAccess; the vertex stage needs to read it.
        // The frame buffer already sits in AllShaderResource, so the gated transition is a no-op.
        instances.TransitionTo(cmd, ResourceStates.AllShaderResource);
        frame.TransitionTo(cmd, ResourceStates.AllShaderResource);

        _color = color; _depth = inputs.Depth;
        _instances = instances; _args = args; _frame = frame;
        _command = cmd; _stamp = Device.GetCurrentRetireFenceValue(); _frameIndex = Device.FrameIndex;
        _prepared = true;
        return true;
    }

    public override bool Draw()
    {
        bool prepared = _prepared;
        var color = _color; var depth = _depth;
        var instances = _instances; var args = _args; var frame = _frame;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        if (!prepared || _disposed || color == null || instances == null || args == null || frame == null) return false;

        var cmd = Device.GraphicsCommandList;
        if (cmd == null || cmd != _command || Device.FrameIndex != _frameIndex
            || Device.GetCurrentRetireFenceValue() != _stamp
            || Device.ActivePassId is not (RenderPassId.Scene or RenderPassId.SceneTransparent)) return false;

        var target = Device.ActiveTarget;
        if (target == null || target.Color == null || target.CurrentState != ResourceStates.RenderTarget) return false;
        // In the MSAA tier BeginPass swaps the Scene color target for MsaaSceneRT, and the one handed out by the shared layer is demoted to a resolve target.
        // Not accepting this form would make particles silently disappear for the entire MSAA tier.
        if (!ReferenceEquals(target, color)
            && !(ReferenceEquals(target, Device.MsaaSceneRT) && ReferenceEquals(target.ResolveDest, color)))
            return false;
        // null == use the backend default depth, which likewise requires the prepare phase to have seen null, otherwise the depth semantics have already changed.
        if (!ReferenceEquals(Device.ActiveDepthTarget, depth)) return false;
        if (instances.Resource == null || (instances.CurrentState & ResourceStates.NonPixelShaderResource) == 0
            || frame.Resource == null || (frame.CurrentState & ResourceStates.NonPixelShaderResource) == 0
            || args.Resource == null || args.CurrentState != ResourceStates.IndirectArgument) return false;

        // Ask the bound resource for the OM signature. The PSO's SampleDesc must match the attachment bit-for-bit, and the attachment's sample count and
        // sample quality are only known precisely by the resource itself; the same holds for the color format - the ColorNativeFormat on the wrapper is
        // left empty for the back buffer wrapper form, whereas back-deriving from the resource always has a value.
        var colorDesc = target.Color->GetDesc();
        var velocityTarget = Device.ActiveVelocityTarget;
        var velocityFormat = velocityTarget != null && velocityTarget.Color != null
            ? velocityTarget.Color->GetDesc().Format
            : Format.FormatUnknown;
        var normalTarget = Device.ActiveNormalTarget;
        var normalFormat = normalTarget != null && normalTarget.Color != null
            ? normalTarget.Color->GetDesc().Format
            : Format.FormatUnknown;
        var key = new PipelineKey(colorDesc.Format, velocityFormat, normalFormat,
            colorDesc.SampleDesc.Count, colorDesc.SampleDesc.Quality);
        var pipeline = GetOrCreatePipeline(key);
        if (pipeline == null) return false;

        // The main pipeline resets the root signature, PSO and topology on every SetPipeline, so the override here will not leak into subsequent draws.
        cmd->SetGraphicsRootSignature(_signature);
        cmd->SetPipelineState(pipeline);
        cmd->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        var indexView = Pipeline.UnitQuadIndexBufferView;
        cmd->IASetIndexBuffer(&indexView);
        cmd->SetGraphicsRootShaderResourceView(InstancesRoot, instances.Resource->GetGPUVirtualAddress());
        cmd->SetGraphicsRootShaderResourceView(FrameRoot, frame.Resource->GetGPUVirtualAddress());
        // The instance count comes from the second field of the argument buffer; the CPU side has never known its value.
        cmd->ExecuteIndirect(_commands, 1, args.Resource, 0, null, 0);
        return true;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prepared = false;
        _color = null; _depth = null; _instances = null; _args = null; _frame = null;
        // A Blob is just CPU-side bytes, not referenced by the GPU, so it can be released immediately.
        if (_vertex != null) { _vertex->Release(); _vertex = null; }
        if (_pixel != null) { _pixel->Release(); _pixel = null; }
        if (_pixelVelocity != null) { _pixelVelocity->Release(); _pixelVelocity = null; }
        var pipelines = _pipelines.Values.ToArray();
        _pipelines.Clear();
        nint commands = (nint)_commands, signature = (nint)_signature;
        _commands = null; _signature = null;
        Device.EnqueueDeferredRelease(Device.GetCurrentRetireFenceValue(), () =>
        {
            foreach (nint pipeline in pipelines)
                if (pipeline != 0) ((ID3D12PipelineState*)pipeline)->Release();
            if (commands != 0) ((ID3D12CommandSignature*)commands)->Release();
            if (signature != 0) ((ID3D12RootSignature*)signature)->Release();
        });
    }
}
