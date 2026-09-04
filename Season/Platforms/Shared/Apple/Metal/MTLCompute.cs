// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Compute-kernel handle for render-quality 1-6 on the Metal backend.
/// It compiles MSL from ComputeKernelDesc once and creates an IMTLComputePipelineState.
/// Metal uses a kernel registration model and has no root signature or descriptor set layout.
/// Binding slots are declared directly inside MSL through [[buffer(n)]] and [[texture(n)]],
/// so no separate layout object is created at registration time.
///
/// Slot contract, aligned with the MSL conventions summarized by ComputeBindingType,
/// with DispatchCompute binding mechanically against it:
/// - Params, when present and always at Bindings[0], goes to SetBytes buffer(0).
///   The contract keeps it at 128 bytes or less, while Metal allows up to 4 KB for setBytes.
/// - Texture bindings map to texture slots in declaration order, and buffer bindings map to buffer slots in declaration order plus one.
/// - The sampler for SampledTexture is always sampler(0), using linear clamp and reusing Pipeline.StaticSampler.
///
/// Compilation goes through MTLShaderCompiler, which caches IMTLLibrary objects by source string.
/// The entry point comes from desc.Source.EntryPoint.
/// MSL keeps the real entry-point name instead of always using main like GLSL.
/// Compilation or pipeline-creation failures are caught by Graphics.CreateComputeKernel,
/// which returns null so registration degrades gracefully.
/// </summary>
internal sealed class MTLComputeKernel : Season.Rendering.ComputeKernel
{
    internal IMTLComputePipelineState PipelineState;

    /// <summary>Byte size of the Params block, or zero when there is no Params binding.</summary>
    internal readonly uint ParamsSize;

    /// <summary>Debug label for the compute encoder, used for Xcode GPU capture grouping.</summary>
    internal readonly string Label;

    /// <summary>
    /// Render-quality 1-8 workgroup size.
    /// This backend is the only one of the four that consumes WorkgroupSize at runtime.
    /// MSL has no compile-time declarations like [numthreads] or layout(local_size_*),
    /// so the threadgroup shape is determined entirely by the second argument of dispatchThreadgroups.
    /// Because of that, the default value of <see cref="Season.Rendering.ComputeKernelDesc"/>, namely (8, 8, 1),
    /// is materialized here directly.
    /// The existing seven effects, Plasma, SceneColorCopy, Bloom, DepthView, GTAO, TAA, and VelocityView,
    /// all keep the default 8x8x1 unchanged with no modifications and no regressions.
    /// </summary>
    internal readonly MTLSize ThreadsPerGroup;

    internal MTLComputeKernel(Season.Rendering.ComputeKernelDesc desc)
    {
        Desc = desc;
        Label = $"Compute {desc.Name}";
        ThreadsPerGroup = new MTLSize(
            (nint)desc.WorkgroupX, (nint)desc.WorkgroupY, (nint)desc.WorkgroupZ);

        var bindings = desc.Bindings;
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Type == Season.Rendering.ComputeBindingType.Params)
                ParamsSize = bindings[i].SizeInBytes;
        }

        var library = MTLShaderCompiler.Compile(Device.MtlDevice, desc.Source.Msl!);
        var function = library.CreateFunction(desc.Source.EntryPoint)
            ?? throw new Exception($"MSL function '{desc.Source.EntryPoint}' (compute '{desc.Name}') not found");

        PipelineState = Device.MtlDevice.CreateComputePipelineState(function, out NSError error);
        if (PipelineState == null)
            throw new Exception($"CreateComputePipelineState ('{desc.Name}') failed: {error?.LocalizedDescription}");

        // In render-quality 1-8, MaxTotalThreadsPerThreadgroup is a PSO property.
        // It varies with register pressure and is not a fixed 1024,
        // so it can only be validated after the PSO exists.
        // Exceeding it throws an exception, which Graphics.CreateComputeKernel catches and turns into null
        // so registration degrades gracefully.
        // The shared-layer ValidateWorkgroupSize limit, where the product must stay at or below 128,
        // is the strictest common denominator across all four backends.
        // This check is the second gate using the real device limit on Metal.
        nuint total = (nuint)(ThreadsPerGroup.Width * ThreadsPerGroup.Height * ThreadsPerGroup.Depth);
        if (total > PipelineState.MaxTotalThreadsPerThreadgroup)
            throw new Exception($"WorkgroupSize {ThreadsPerGroup.Width}×{ThreadsPerGroup.Height}×"
                + $"{ThreadsPerGroup.Depth}={total} exceeds PSO ('{desc.Name}') "
                + $"MaxTotalThreadsPerThreadgroup={PipelineState.MaxTotalThreadsPerThreadgroup}");
    }

    public override void Dispose()
    {
        // Disposal path:
        // in-flight command buffers keep retained references to the PSO under Device rule 5,
        // so releasing the C# wrapper immediately is safe and no delayed-release queue is needed,
        // unlike the fence-gated DX and VK paths.
        PipelineState?.Dispose();
        PipelineState = null!;
    }
}

/// <summary>
/// Compute storage buffer for render-quality 1-6 on the Metal backend.
/// It uses StorageModePrivate, meaning GPU-resident memory with no CPU mapping,
/// equivalent to a DX default heap or VK DEVICE_LOCAL.
/// Metal has no explicit buffer state machine, so binding goes directly through SetBuffer.
/// Dependencies between kernels in the same frame are tracked automatically by the driver through hazard tracking,
/// matching rule 2 with zero explicit barriers.
/// </summary>
internal sealed class MTLStorageBuffer : Season.Rendering.StorageBuffer
{
    internal IMTLBuffer Buffer;

    readonly nuint _alignedSize;

    /// <summary>Step 0 of 2-4: a StorageModeShared staging ring for CPU-to-GPU uploads, split by in-flight frame slot.
    /// Earlier, each UpdateStorageBuffer call created a buffer and disposed it immediately,
    /// relying on command-buffer retained references to keep it alive.
    /// That was harmless for infrequent calls, but DDGI parameter blocks are written every frame,
    /// which would have meant one GPU allocation per frame and violated the zero-per-frame-allocation goal.
    /// Slotting also avoids CPU write versus in-flight blit read races that occur when reusing a single staging block.
    /// Shared memory allows direct CPU writes, and automatic hazard tracking only covers GPU-side dependencies,
    /// not CPU writes.
    /// Slot safety is guaranteed by the frame-loop _inFlight semaphore, matching the engine's other per-frame buffers.</summary>
    IMTLBuffer[]? _staging;

    internal MTLStorageBuffer(uint sizeInBytes)
    {
        // Round capacity up to 16 bytes so alignment matches the D3D12 raw-view granularity
        // and behavior stays consistent across backends.
        uint alignedSize = (sizeInBytes + 15u) & ~15u;
        SizeInBytes = sizeInBytes;
        _alignedSize = (nuint)alignedSize;
        Buffer = Device.ResourceManager.CreateBuffer(_alignedSize, MTLResourceOptions.StorageModePrivate);
    }

    /// <summary>Gets the staging buffer for the current-frame slot, creating it on demand. Creation failure returns null so the caller can degrade gracefully.</summary>
    internal IMTLBuffer? TryGetStagingForCurrentFrame()
    {
        _staging ??= new IMTLBuffer[Device.frameCount];
        int slot = Device.FrameIndex % _staging.Length;
        if (_staging[slot] == null)
        {
            try
            {
                _staging[slot] = Device.ResourceManager.CreateBuffer(_alignedSize, MTLResourceOptions.StorageModeShared);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp.AddLog(LogType.Error, $"{DateTime.UtcNow} [MTLStorageBuffer] Failed to create staging buffer: {ex.Message}");
                return null;
            }
        }
        return _staging[slot];
    }

    public override void Dispose()
    {
        // Same rule as kernels:
        // retained references keep the object alive for in-flight command buffers,
        // so releasing the wrapper immediately is safe.
        Buffer?.Dispose();
        Buffer = null!;

        if (_staging != null)
        {
            for (int i = 0; i < _staging.Length; i++)
            {
                _staging[i]?.Dispose();
                _staging[i] = null!;
            }
            _staging = null;
        }
    }
}
