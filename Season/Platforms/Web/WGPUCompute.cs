// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

/// <summary>
/// Wrapper for a WebGPU compute kernel (1-6, aligned with the name-as-handle pattern used by WGPURenderTarget).
/// The actual pipeline and bind-group layout live on the JS side in _computeKernels[JsName]
/// (WGSL is pushed down through interop, so JS keeps zero shader source code).
/// JsName uses an incrementing prefix for deduplication so multiple effects with the same Desc.Name do not collide.
/// resourcesJson is cached by content: when the resource group is unchanged
/// (the common case for built-in effects, where cached readonly arrays are reused every frame),
/// it avoids string allocations, and JS also uses that string as the bind-group cache key, avoiding rebuilds on both layers.
/// </summary>
internal sealed class WGPUComputeKernel : Season.Rendering.ComputeKernel
{
    static int _nextId;

    internal readonly string JsName;

    Season.Rendering.ComputeResourceRef[]? _cachedRefs;
    string _cachedResourcesJson = "";

    internal WGPUComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        Desc = desc;
        JsName = $"ck{_nextId++}_{desc.Name}";
    }

    /// <summary>Resource references are encoded into a prefix-tagged JSON array such as "t:textureName" / "b:bufferId" / "r:renderTargetName" (2-1);
    /// when a content-level comparison hits the cache, the cached string is reused.</summary>
    internal string ResolveResourcesJson(ReadOnlySpan<Season.Rendering.ComputeResourceRef> resources)
    {
        if (_cachedRefs != null && _cachedRefs.Length == resources.Length)
        {
            bool same = true;
            for (int i = 0; i < resources.Length; i++)
            {
                if (_cachedRefs[i].TextureName != resources[i].TextureName ||
                    !ReferenceEquals(_cachedRefs[i].Buffer, resources[i].Buffer) ||
                    !ReferenceEquals(_cachedRefs[i].Target, resources[i].Target))
                {
                    same = false;
                    break;
                }
            }
            if (same) return _cachedResourcesJson;
        }

        var sb = new StringBuilder(64);
        sb.Append('[');
        for (int i = 0; i < resources.Length; i++)
        {
            if (i > 0) sb.Append(',');
            ref readonly var r = ref resources[i];
            if (r.TextureName != null)
                sb.Append("\"t:").Append(r.TextureName).Append('"');
            else if (r.Buffer is WGPUStorageBuffer buf)
                sb.Append("\"b:").Append(buf.Id).Append('"');
            else if (r.Target is WGPURenderTarget rtRef)
                // 2-1: RenderTarget color can be used as compute input (for example, bloom prefilter reads SceneColor).
                // If the matchBackbuffer render target is rebuilt lazily and its view becomes stale, the JS-side slot.rtRefs identity check covers it.
                sb.Append("\"r:").Append(rtRef.Name).Append('"');
            else
                throw new NotSupportedException("[DispatchCompute] Unsupported resource reference. Expected one of TextureName, StorageBuffer, or RenderTarget.");
        }
        sb.Append(']');

        _cachedRefs = resources.ToArray();
        _cachedResourcesJson = sb.ToString();
        return _cachedResourcesJson;
    }

    public override void Dispose() => WebGPUInterop.DisposeComputeKernel(JsName);
}

/// <summary>Wrapper for a WebGPU storage buffer: the actual GPUBuffer lives on the JS side in _storageBuffers[Id].</summary>
internal sealed class WGPUStorageBuffer : Season.Rendering.StorageBuffer
{
    static int _nextId;

    internal readonly string Id;

    internal WGPUStorageBuffer(uint sizeInBytes, Season.Rendering.StorageBufferUsage usage)
    {
        SizeInBytes = sizeInBytes;
        Usage = usage;
        Id = $"sb_{_nextId++}";
    }

    public override void Dispose() => WebGPUInterop.DisposeStorageBuffer(Id);
}

/// <summary>独立 HDR 雾合成句柄；原生 PSO、资源身份与同帧参数槽由 JS 管理。</summary>
internal sealed class WGPUFogCompositePipeline(bool weather = false) : Season.Rendering.FogCompositePipeline
{
    static int _nextId;
    readonly string _name = $"fog_composite_{_nextId++}";
    bool _disposed, _prepared;

    internal bool Initialize() => WebGPUInterop.CreateFogCompositePipeline(_name,
        Season.Rendering.Effects.VolumetricFogEffect.CompositeVertexWgsl,
        weather ? Season.Rendering.Effects.WeatherShaders.CompositeWgsl : Season.Rendering.Effects.VolumetricFogEffect.CompositeWgsl, weather);

    public override bool Prepare(in Season.Rendering.FogCompositeInputs inputs, Season.Rendering.RenderTarget destination)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color || inputs.Depth is not WGPURenderTarget depth
            || inputs.FrameBuffer is not WGPUStorageBuffer frame || destination is not WGPURenderTarget target)
            return false;
        _prepared = WebGPUInterop.PrepareFogComposite(_name, color.Name, depth.Name, frame.Id,
            inputs.IntegratedVolume, inputs.LocalVolume, inputs.Ao.TextureName,
            (inputs.Ao.Target as WGPURenderTarget)?.Name, inputs.AoIntensity, inputs.ApplyAo, target.Name);
        return _prepared;
    }

    public override Season.Rendering.FogCompositeResult Draw()
    {
        if (_disposed || !_prepared) return default;
        _prepared = false;
        int flags = WebGPUInterop.DrawFogComposite(_name);
        return new((flags & 1) != 0, (flags & 2) != 0);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeFogCompositePipeline(_name);
    }
}

/// <summary>
/// A0.1 粒子间接绘制句柄。原生 PSO、绑定组与 Pass 身份校对全在 JS 侧，这边只做类型下转与一次性消费。
/// Depth 为空是合法输入：场景 Pass 此时跑在默认深度缓冲上，JS 侧改为只要求深度附件存在。
/// </summary>
internal sealed class WGPUParticleDrawPipeline : Season.Rendering.ParticleDrawPipeline
{
    static int _nextId;
    readonly string _name = $"particle_draw_{_nextId++}";
    bool _disposed, _prepared;

    internal bool Initialize() => WebGPUInterop.CreateParticleDrawPipeline(_name,
        Season.Rendering.Effects.ParticleSystemEffect.DrawVertexWgsl,
        Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentWgsl,
        Season.Rendering.Effects.ParticleSystemEffect.DrawFragmentVelocityWgsl);

    public override bool Prepare(in Season.Rendering.ParticleDrawInputs inputs)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color
            || inputs.Instances is not WGPUStorageBuffer instances
            || inputs.IndirectArgs is not WGPUStorageBuffer args
            || inputs.Frame is not WGPUStorageBuffer frame) return false;
        // Depth 为空要走通，但类型不对不能当空处理：那是后端混用，不是质量档差异。
        if (inputs.Depth != null && inputs.Depth is not WGPURenderTarget) return false;
        _prepared = WebGPUInterop.PrepareParticleDraw(_name, color.Name,
            (inputs.Depth as WGPURenderTarget)?.Name, instances.Id, args.Id, frame.Id);
        return _prepared;
    }

    public override bool Draw()
    {
        if (_disposed || !_prepared) return false;
        _prepared = false;
        return WebGPUInterop.DrawParticles(_name);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeParticleDrawPipeline(_name);
    }
}
