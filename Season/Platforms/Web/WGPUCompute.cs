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

    /// <summary>
    /// C1: set by <c>TryPrepareVertexInput</c> once the JS side has been told to draw this buffer as slot 0, and cleared again as
    /// soon as a dispatch writes it. WebGPU has no barrier to record - the queue order plus the usage bit declared at creation
    /// carries the handoff - so this flag is the whole handoff, and it is what makes a stream that was never written (the cloth
    /// never stepped, or the effect already disposed) fall back to the static vertex buffer.
    /// </summary>
    internal bool VertexInputReady;

    internal WGPUStorageBuffer(uint sizeInBytes, Season.Rendering.StorageBufferUsage usage)
    {
        SizeInBytes = sizeInBytes;
        Usage = usage;
        Id = $"sb_{_nextId++}";
    }

    public override void Dispose()
    {
        // A released stream is never in the vertex-input state, so any mesh still pointing at it draws its static buffer even
        // before the producer clears Surface.VertexStream.
        VertexInputReady = false;
        WebGPUInterop.DisposeStorageBuffer(Id);
    }
}

/// <summary>Standalone HDR fog composite handle; the native PSO, resource identity, and per-frame parameter slots are managed by JS.</summary>
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
/// P0 SSR additive composite handle; the native PSO, binding groups, and Pass identity check are managed by JS.
/// The fragment only reads the delta and adds it, while the weights have already been computed to their final values in the kernel of <see cref="Season.Rendering.Effects.SsrEffect"/>.
/// </summary>
internal sealed class WGPUSsrCompositePipeline : Season.Rendering.SsrCompositePipeline
{
    static int _nextId;

    // The vertex reuses the full-screen triangle from the fog composite; the fragment uses textureLoad to fetch integer pixels and needs no sampler.
    const string Fragment = """
@group(0) @binding(0) var reflectionDelta:texture_2d<f32>;

@fragment fn PSMain(@builtin(position) pos:vec4f)->@location(0) vec4f {
    let delta = textureLoad(reflectionDelta, vec2<i32>(floor(pos.xy)), 0);
    return vec4f(delta.rgb, 0.0);
}
""";

    readonly string _name = $"ssr_composite_{_nextId++}";
    bool _disposed, _prepared;

    internal bool Initialize() => WebGPUInterop.CreateSsrCompositePipeline(_name,
        Season.Rendering.Effects.VolumetricFogEffect.CompositeVertexWgsl, Fragment);

    public override bool Prepare(in Season.Rendering.SsrCompositeInputs inputs)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color || string.IsNullOrEmpty(inputs.ReflectionTexture))
            return false;
        _prepared = WebGPUInterop.PrepareSsrComposite(_name, color.Name, inputs.ReflectionTexture);
        return _prepared;
    }

    public override bool Draw()
    {
        if (_disposed || !_prepared) return false;
        _prepared = false;
        return WebGPUInterop.DrawSsrComposite(_name);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeSsrCompositePipeline(_name);
    }
}

/// <summary>
/// A0.1 particle indirect draw handle. The native PSO, binding groups, and Pass identity check all live on the JS side; this side only does the type downcast and one-shot consumption.
/// An empty Depth is a legal input: the Scene pass then runs on the default depth buffer, and the JS side relaxes to requiring only that a depth attachment exists.
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
        // An empty Depth must work, but a wrong type must not be treated as empty: that is backend mixing, not a quality-tier difference.
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

/// <summary>
/// D1 screen-space decal handle. Four PSOs, two binding groups, and the Pass identity check all live on the JS side; this side only does the type downcast and one-shot consumption.
/// Unlike fog/particles, it does not depend on compute (depth is only a sampling source), so there is no ComputeSupported gate.
/// The vertex stage carries the decal slot through the firstInstance of draw(36,1,0,slot) -- WebGPU has no push constant.
/// </summary>
internal sealed class WGPUDecalPipeline : Season.Rendering.DecalPipeline
{
    static int _nextId;
    readonly string _name = $"decal_{_nextId++}";
    readonly StringBuilder _draws = new(256);
    bool _disposed, _prepared;

    internal bool Initialize() => WebGPUInterop.CreateDecalPipeline(_name, Season.Rendering.DecalShaders.Wgsl);

    public override bool Prepare(in Season.Rendering.DecalInputs inputs, Season.Rendering.RenderTarget destination)
    {
        _prepared = false;
        if (_disposed || inputs.Color is not WGPURenderTarget color || inputs.Depth is not WGPURenderTarget depth
            || inputs.FrameBuffer is not WGPUStorageBuffer frame || destination is not WGPURenderTarget target)
            return false;
        // The normal is an optional input (null when roughness is not modified), but a wrong type must not be treated as empty: that is backend mixing.
        if (inputs.Normal != null && inputs.Normal is not WGPURenderTarget) return false;
        if (inputs.Count <= 0 || inputs.Count > Season.Rendering.DecalMath.MaxDecals) return false;
        _prepared = WebGPUInterop.PrepareDecal(_name, color.Name, depth.Name,
            (inputs.Normal as WGPURenderTarget)?.Name, frame.Id, BuildDrawsJson(inputs.Draws, inputs.Count),
            inputs.WriteRoughness, target.Name);
        return _prepared;
    }

    /// <summary>Encodes [slot, mode, texture name] into JSON in draw order; a null texture name means the shader uses a procedural pattern.</summary>
    string BuildDrawsJson(Season.Rendering.DecalDraw[] draws, int count)
    {
        _draws.Clear().Append('[');
        for (int i = 0; i < count; i++)
        {
            var draw = draws[i];
            if (i > 0) _draws.Append(',');
            _draws.Append('[').Append(draw.Index).Append(',').Append((int)draw.Mode).Append(',');
            if (draw.Texture == null) _draws.Append("null");
            else _draws.Append('"').Append(draw.Texture).Append('"');
            _draws.Append(']');
        }
        return _draws.Append(']').ToString();
    }

    public override bool Draw()
    {
        if (_disposed || !_prepared) return false;
        _prepared = false;
        return WebGPUInterop.DrawDecal(_name);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _prepared = false;
        WebGPUInterop.DisposeDecalPipeline(_name);
    }
}
