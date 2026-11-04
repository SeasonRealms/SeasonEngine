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
    /// <para>
    /// Covers both indirect flavours, because no backend separates them: Vulkan reads both from VK_BUFFER_USAGE_INDIRECT_BUFFER,
    /// WebGPU from GPUBufferUsage.INDIRECT, D3D12 from the same INDIRECT_ARGUMENT state, and Metal has no usage bits at all.
    /// A block holding <see cref="IndirectDrawIndexedArgs"/> and one holding <see cref="IndirectDispatchArgs"/> (or several of
    /// either, laid out back to back) therefore declare the same thing.
    /// </para>
    /// </summary>
    ComputeAndIndirectArgs,

    /// <summary>
    /// Compute read/write plus use as a vertex input stream. A7 introduced this: the gather kernel materialises
    /// the surviving instances into a per-slot block, and the very same buffer is then bound as the IA instance
    /// stream. Vulkan and WebGPU both bake this into the native object (VkBufferUsageFlags.VertexBuffer,
    /// GPUBufferUsage.VERTEX) and refuse to widen it afterwards, so it has to be asked for at creation.
    /// D3D12 and Metal need nothing extra: a D3D12 resource moves into VERTEX_AND_CONSTANT_BUFFER by barrier and
    /// an MTLBuffer has no usage bits at all, but both still honour the declaration for validation symmetry.
    /// </summary>
    ComputeAndVertexInput,

    /// <summary>
    /// Compute read/write plus use as a shader resource <em>inside the vertex stage</em>, which is what a
    /// compute-written, VS-consumed data block needs. This is not <see cref="ComputeAndVertexInput"/>: that one feeds
    /// IA vertex attributes, addressed per vertex/instance by the input assembler, while this one is read by the vertex
    /// shader itself through an SRV/SSBO at a slot the shader indexes freely. The instance bone palette deliberately
    /// takes this route rather than a vertex attribute: the joint index is per vertex and the palette row is per
    /// instance, so the two are combined in the shader, not by the IA.
    /// <para>
    /// Backend consequences. Vulkan needs no extra VkBufferUsageFlags (StorageBufferBit already covers it) but does need
    /// a shader-stage barrier, because a compute-side write must become visible to VERTEX_SHADER reads; that barrier is
    /// recorded outside any render pass by <c>Graphics.TryPrepareVertexShaderRead</c>. D3D12 needs the resource moved to
    /// NON_PIXEL_SHADER_RESOURCE, recorded by the same entry point, and a <em>structured</em> SRV whose stride matches the
    /// shader declaration (a raw ByteAddressBuffer view would not type-match a StructuredBuffer). Metal and WebGPU need
    /// nothing extra at submit time: Metal tracks the dependency through its encoder barriers, and WebGPU inserts
    /// synchronization between passes itself. WebGPU does require the bind group layout to declare the binding with
    /// <c>GPUShaderStage.VERTEX</c> visibility, which is a pipeline-side property, not a buffer property.
    /// </para>
    /// <para>
    /// Read-only from the shader's point of view on all four backends: core WebGPU has no read_write storage access in
    /// the vertex stage, so a producer that needs to read back its own output must do so in compute.
    /// </para>
    /// </summary>
    ComputeAndVertexShaderRead,
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

/// <summary>
/// A0.2: the thread-group argument block that an indirect compute dispatch reads from GPU memory.
/// Twelve bytes, three uint32, no padding — the one layout all four backends agree on
/// (D3D12 D3D12_DISPATCH_ARGUMENTS, Vulkan VkDispatchIndirectCommand,
/// Metal MTLDispatchThreadgroupsIndirectArguments, WebGPU dispatchWorkgroupsIndirect).
/// <para>
/// What this is for: a kernel whose useful thread count is decided on the GPU, so the CPU cannot
/// name the group count without reading back, and a readback would stall the frame. A7 gather is the
/// first consumer — the cull kernel leaves a per-slot visible count that only the GPU knows, and
/// gather would otherwise have to be dispatched over the full capacity of every slot.
/// </para>
/// <para>
/// Contract for producers, all four backends:
/// <list type="number">
/// <item>All three fields are workgroup counts, never thread counts. The backend multiplies by the
/// kernel's own workgroup size, which the shader declares; a producer that writes thread counts
/// over-dispatches by that factor and is correct but slow, not wrong — which is exactly why this
/// mistake survives review.</item>
/// <item>GroupCountX == 0 dispatches nothing and is legal everywhere. This is the fast path for a
/// producer whose whole input was rejected: leave the block zeroed instead of guarding the submit.</item>
/// <item>GroupCountY and GroupCountZ may be non-zero with GroupCountX == 0; the dispatch is still empty.
/// The three fields are not independently significant.</item>
/// <item>As with <see cref="IndirectDrawIndexedArgs"/>, the producing dispatch and the argument
/// barrier both have to be recorded outside a render pass, and the block is never read back.</item>
/// </list>
/// </para>
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct IndirectDispatchArgs
{
    /// <summary>Byte size of one argument block. Also the stride if a buffer holds several.</summary>
    public const uint SizeInBytes = 12;

    /// <summary>Workgroups along X. The producer's size decides this; 0 means nothing runs.</summary>
    public uint GroupCountX;

    /// <summary>Workgroups along Y. A7 gathers one slot per row, so this carries the slot count.</summary>
    public uint GroupCountY;

    /// <summary>Workgroups along Z.</summary>
    public uint GroupCountZ;
}
