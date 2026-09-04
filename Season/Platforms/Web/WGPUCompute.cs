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

    internal WGPUStorageBuffer(uint sizeInBytes)
    {
        SizeInBytes = sizeInBytes;
        Id = $"sb_{_nextId++}";
    }

    public override void Dispose() => WebGPUInterop.DisposeStorageBuffer(Id);
}
