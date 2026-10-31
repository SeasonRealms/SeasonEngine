// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Declared usage of a storage buffer, fixed at creation time because three of the four backends bake usage into the native buffer object
/// and cannot widen it afterwards (Vulkan VkBufferUsageFlags, WebGPU GPUBufferUsage, and the D3D12 resource flags that decide which states a resource may enter).
/// Making usage explicit rather than granting every buffer indirect capability keeps the intent readable at the call site and keeps validation layers able to catch a buffer
/// that is fed to an indirect submit without having asked for it.
/// </summary>
public enum StorageBufferUsage
{
    /// <summary>Compute read/write only. This is what every call site before A0.1 expects, so it stays the default.</summary>
    Compute,

    /// <summary>
    /// Compute read/write plus use as an indirect argument source. The buffer is still a normal storage buffer for binding purposes;
    /// the only difference is that the backend is allowed to move it into the state/usage that an indirect submit reads from.
    /// Carries a real cost on D3D12, where a resource that can become INDIRECT_ARGUMENT is excluded from some driver fast paths, so it is not the default.
    /// </summary>
    ComputeAndIndirectArgs,
}

/// <summary>
/// A0.1: the indexed-draw argument block that an indirect submit reads from GPU memory. This exact five-field layout is the only one all four backends agree on
/// (D3D12 D3D12_DRAW_INDEXED_ARGUMENTS, Vulkan VkDrawIndexedIndirectCommand, Metal MTLDrawIndexedPrimitivesIndirectArguments, WebGPU drawIndexedIndirect),
/// field order and width included, which is why the engine exposes it as a struct rather than letting each backend describe its own.
/// <para>
/// The struct exists for CPU-side seeding and for documenting the layout that compute shaders must write. It is never read back:
/// a readback would stall the frame and defeat the entire point of letting the GPU decide the draw.
/// </para>
/// <para>
/// Contract for producers, all four backends:
/// <list type="number">
/// <item>StartInstanceLocation must be 0. WebGPU rejects a non-zero firstInstance unless the optional indirect-first-instance feature is enabled,
/// and the engine does not assume optional features. Per-instance offsetting belongs in the shader, addressed off the instance index.</item>
/// <item>One command per buffer for now. Multi-draw with a GPU-provided command count is deferred to A7 GPU-driven culling, because WebGPU has no count buffer at all
/// and Metal needs MTLIndirectCommandBuffer for it; both would force a capability split that single-command indirect avoids entirely.</item>
/// <item>The producing dispatch and the argument barrier must both be recorded outside a render pass; see IGraphics.TryPrepareIndirectArgs for why.</item>
/// </list>
/// </para>
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct IndirectDrawIndexedArgs
{
    /// <summary>Byte size of one argument block, identical on all four backends. Also the stride if a buffer ever holds several.</summary>
    public const uint SizeInBytes = 20;

    /// <summary>Indices consumed per instance. For the particle path this is the constant quad index count, not a GPU-decided value.</summary>
    public uint IndexCountPerInstance;

    /// <summary>The field the GPU actually decides. For particles, the alive count written by the simulation kernel.</summary>
    public uint InstanceCount;

    /// <summary>First index read from the bound index buffer.</summary>
    public uint StartIndexLocation;

    /// <summary>Value added to every vertex index before vertex-buffer lookup. Signed on all four backends.</summary>
    public int BaseVertexLocation;

    /// <summary>Must stay 0; see the type-level contract.</summary>
    public uint StartInstanceLocation;
}
