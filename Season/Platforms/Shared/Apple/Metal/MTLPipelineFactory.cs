// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Foundation;
using Metal;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// The one place this backend turns a descriptor into a GPU pipeline or state object.
///
/// <para><b>Why a single entry point.</b> D3D12 and Vulkan each build every PSO through one
/// <c>CreatePipelineState</c>, so the startup profiler, the error message and the null check all live on one line. Metal
/// had no such path: the main pipeline, blit, decal, immediate 2D, particle, water, waterfall, fog composite, SSR
/// composite and compute each called <c>IMTLDevice</c> directly and each invented its own failure wording, which meant a
/// later change to how a creation failure is reported had to find eleven call sites. This file is that single path for
/// all four object kinds the backend creates at startup.</para>
///
/// <para><b>What is measured.</b> Only the driver call. Descriptor building is CPU work the caller owns, and the
/// measured interval is deliberately identical in shape to the D3D12 and Vulkan rows so the three reports can be read
/// side by side. A production process never reaches this file (nothing here runs per frame), and the timing guard costs
/// one static bool read when <see cref="Season.Rendering.StartupProfiler.Enabled"/> is false.</para>
///
/// <para><b>What is not done.</b> Nothing is retried, coalesced or swallowed. A null result means the driver rejected
/// the descriptor, and every caller is a startup path that cannot continue without that object - throwing here with the
/// descriptor's own label is the whole point of routing every site through one method.</para>
/// </summary>
internal static class MTLPipelineFactory
{
    /// <summary>
    /// Bakes one render pipeline state. The descriptor's label becomes the pipeline's name in the startup report and in
    /// the failure message; an unlabelled descriptor still gets a row and still throws with a usable name.
    /// </summary>
    internal static IMTLRenderPipelineState CreateRender(MTLRenderPipelineDescriptor descriptor)
    {
        string name = NameOf(descriptor.Label);

        long start = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var pipeline = Device.MtlDevice.CreateRenderPipelineState(descriptor, out NSError? error);

        if (start != 0)
        {
            StartupProfiler.Item("pso", name, StartupProfiler.ElapsedMs(start), 0,
                $"caller=mtl failed={(pipeline == null ? 1 : 0)} samples={descriptor.RasterSampleCount} " +
                $"color={descriptor.ColorAttachments[0].PixelFormat}");
        }

        return pipeline ?? throw new InvalidOperationException(
            $"CreateRenderPipelineState [{name}] failed: {error?.LocalizedDescription ?? "(no NSError)"}");
    }

    /// <summary>
    /// Bakes one compute pipeline state. <paramref name="name"/> is the kernel's own name, so a slow row is traceable
    /// to the kernel instead of every row reading "compute main"; the library compile is a separate row.
    /// </summary>
    internal static IMTLComputePipelineState CreateCompute(IMTLFunction function, string name)
    {
        long start = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var pipeline = Device.MtlDevice.CreateComputePipelineState(function, out NSError error);

        if (start != 0)
        {
            double ms = StartupProfiler.ElapsedMs(start);

            StartupProfiler.Item("pso", $"compute {name}", ms, 0, "caller=mtl kind=compute");

            // Kept as its own aggregate: the compute half of the startup bake is what the AI panels wait on, so its
            // total is reported separately from the render PSO subtotal.
            StartupProfiler.Add("compute.pso", ms);
        }

        return pipeline ?? throw new InvalidOperationException(
            $"CreateComputePipelineState [compute {name}] failed: {error?.LocalizedDescription ?? "(no NSError)"}");
    }

    /// <summary>Bakes one depth-stencil state. These are immutable and created once per pass family at startup.</summary>
    internal static IMTLDepthStencilState CreateDepthState(MTLDepthStencilDescriptor descriptor, string name)
    {
        long start = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var state = Device.MtlDevice.CreateDepthStencilState(descriptor);

        if (start != 0)
            StartupProfiler.Item("state", $"depth {name}", StartupProfiler.ElapsedMs(start), 0,
                $"caller=mtl failed={(state == null ? 1 : 0)} cmp={descriptor.DepthCompareFunction} write={descriptor.DepthWriteEnabled}");

        return state ?? throw new InvalidOperationException($"CreateDepthStencilState [{name}] failed");
    }

    /// <summary>Bakes one sampler state. Every sampler in this backend is immutable and created at startup.</summary>
    internal static IMTLSamplerState CreateSampler(MTLSamplerDescriptor descriptor, string name)
    {
        long start = StartupProfiler.Enabled ? StartupProfiler.Ticks() : 0;

        var state = Device.MtlDevice.CreateSamplerState(descriptor);

        if (start != 0)
            StartupProfiler.Item("state", $"sampler {name}", StartupProfiler.ElapsedMs(start), 0,
                $"caller=mtl failed={(state == null ? 1 : 0)} min={descriptor.MinFilter} mag={descriptor.MagFilter} " +
                $"addr={descriptor.SAddressMode}");

        return state ?? throw new InvalidOperationException($"CreateSamplerState [{name}] failed");
    }

    /// <summary>A descriptor that never set a label still needs a name in the row and in the throw.</summary>
    static string NameOf(string? label) => string.IsNullOrEmpty(label) ? "(unlabelled)" : label;
}
