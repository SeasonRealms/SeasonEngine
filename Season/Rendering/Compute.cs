// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Shader source bundle for all four backends. Introduced with 1-6 Compute as the common carrier, and reused by future custom rendering shaders.
/// The engine does not translate across shader languages: each graphics API is implemented separately, so effect authors must provide source for each target backend.
/// A null source for any backend means that backend is unsupported, and registration degrades gracefully by returning false from RegisterCompute with no residue.
/// The single source of truth for shader source lives in the effect class file, such as Effects/Plasma.cs. Backend modules and seasonWebGPU.js must not embed shader code.
/// </summary>
public sealed class ShaderSourceSet
{
    /// <summary>D3D12: cs_5_0, compiled by fxc. Keep kernels single-exit to avoid the X4000 issue.</summary>
    public string? Hlsl;

    /// <summary>Vulkan: GLSL #version 450 compute, compiled to SPIR-V at runtime by glslang.</summary>
    public string? Glsl;

    /// <summary>Metal: MSL kernel function.</summary>
    public string? Msl;

    /// <summary>WebGPU: WGSL @compute, passed through the interop layer into JS. The JS file itself does not contain shader source.</summary>
    public string? Wgsl;

    /// <summary>Entry function name. GLSL always uses main, so this field is ignored for GLSL.</summary>
    public string EntryPoint = "CSMain";
}

/// <summary>
/// Binding resource types. Declaration order is the cross-backend slot contract, a strict rule for effect authors and AI translation tools, with fully mechanical mapping on all four backends:
/// - HLSL: Params→b0 root constants; SampledTexture/DepthTexture→t{SRV declaration order} with only the former paired with s0;
///   StorageTextureWrite→u{UAV declaration order};
///   StorageBufferRead→ByteAddressBuffer t{SRV declaration order}; StorageBufferReadWrite→RWByteAddressBuffer u{UAV declaration order}
///   (bindings have no stride metadata, so D3D12 always creates raw views to align with typeless storage blocks in WGSL/GLSL)
/// - GLSL 450: Params→push_constant; all other resources use binding=declaration index i, including the Params placeholder, so binding 0 remains unused for them
/// - MSL: Params→buffer(0); textures→texture(texture declaration order); buffers→buffer(buffer declaration order + 1)
/// - WGSL: @group(0) @binding(declaration index i), with Params at @binding(0) as a uniform
/// Workgroup size is declared by ComputeKernelDesc.WorkgroupX/Y/Z, defaulting to 8×8×1; see that summary for limits.
/// The two 3D types added in 1-8 do not introduce new counter domains: in HLSL, Texture3D and Texture2D share the t register space, and RWTexture3D shares u with RWTexture2D;
/// in MSL, texture3d and texture2d share the texture index space.
/// </summary>
public enum ComputeBindingType
{
    /// <summary>Parameter block. At most one is allowed and it must appear at Bindings[0]. It is 16-byte aligned and limited to ≤128 bytes, matching Vulkan's strict push-constant minimum guarantee.</summary>
    Params,

    /// <summary>Read-only sampled texture. The engine provides a static linear-clamp sampler.</summary>
    SampledTexture,

    /// <summary>Read-only depth texture, introduced in 2-2 Step A and required by SSAO.
    /// All backends use texel-load access with no sampler, which matches the strictest platform constraint because WebGPU depth32float cannot be filter-sampled,
    /// and also avoids WGSL implicit-derivative issues inside uniform control flow.
    /// Access forms are HLSL Texture2D&lt;float&gt;.Load, GLSL texelFetch, MSL depth2d access::read .read(), and WGSL texture_depth_2d textureLoad.
    /// The resource may only come from ComputeResourceRef.Target referencing a depth-only render target such as FrameSchedule.SceneDepth or ShadowMap.
    /// Support status: wired up on all four backends in 2-2 Step C; Vulkan uses CombinedImageSampler + immutable point sampler,
    /// and the web side exposes SceneDepth as depth24plus. See each backend's DispatchCompute for details.</summary>
    DepthTexture,

    /// <summary>Writable storage texture, always write-only to match the strictest platform because core WebGPU does not support read-write rgba storage.
    /// The format is declared by ComputeBindingDesc.StorageFormat and must match the format used by CreateComputeTexture.</summary>
    StorageTextureWrite,

    /// <summary>Read-only storage buffer.</summary>
    StorageBufferRead,

    /// <summary>Read-write storage buffer. Compute→compute dependencies within the same frame's kernel chain are synchronized by backend-inserted barriers.</summary>
    StorageBufferReadWrite,

    /// <summary>Read-only 3D sampled texture, introduced in 1-8 and required for Global SDF and voxel albedo.
    /// The engine-provided static sampler uses linear + clamp-to-edge on all three axes, so 3D sampling naturally gives trilinear filtering with edge clamping and does not require the effect to issue eight manual loads.
    /// The resource may only be referenced through ComputeResourceRef.TextureName and must point to a 3D texture created by CreateComputeTexture3D.
    /// The naming convention uses the compute3d:// prefix, and 3D textures live in a backend-specific dictionary separate from 2D textures, so Sprite2D cannot consume them.
    /// Backend declarations are HLSL Texture3D&lt;float4&gt; t{SRV declaration order} + s0, GLSL sampler3D binding=i,
    /// MSL texture3d&lt;float&gt; texture(texture declaration order), and WGSL texture_3d&lt;f32&gt; @binding(i).
    /// WebGPU bind group layouts must set viewDimension:'3d', otherwise validation fails.</summary>
    SampledTexture3D,

    /// <summary>Writable 3D storage texture, introduced in 1-8 and always write-only, just like the 2D storage-texture case.
    /// The format is declared by ComputeBindingDesc.StorageFormat and must match the format used when creating the texture through CreateComputeTexture3D.
    /// Backend declarations are HLSL RWTexture3D&lt;float4&gt; u{UAV declaration order}, GLSL writeonly image3D binding=i,
    /// MSL texture3d&lt;float, access::write&gt; texture(texture declaration order), and
    /// WGSL texture_storage_3d&lt;FMT, write&gt; @binding(i), where FMT is the concrete WebGPU format described by ComputeStorageFormat.</summary>
    StorageTexture3DWrite,
}

/// <summary>
/// Intended storage-texture pixel format, introduced in 2-1 Step A and expanded to five entries in 1-8.
/// Enum values represent format intent rather than concrete backend formats.
/// Each backend maps the intent to its best concrete format. WebGPU is the constrained backend and carries a capability-based fallback chain
/// because core WebGPU does not allow r16float, r8unorm, or rg16float as STORAGE_BINDING without the optional texture-formats-tier1 feature.
///
/// | Intent | D3D12 | Vulkan (after querying FormatProperties at runtime) | Metal | WebGPU (capability fallback chain) |
/// |---|---|---|---|---|
/// | Rgba8Unorm | R8G8B8A8_UNORM | R8G8B8A8_UNORM | RGBA8Unorm | rgba8unorm |
/// | Rgba16Float | R16G16B16A16_FLOAT | R16G16B16A16_SFLOAT | RGBA16Float | rgba16float |
/// | R16Float | R16_FLOAT | R16_SFLOAT ↓ rgba16f | R16Float | tier1→r16float ↓ float32-filterable→r32float ↓ rgba16float |
/// | R8Unorm | R8_UNORM | R8_UNORM ↓ rgba8unorm | R8Unorm | tier1→r8unorm ↓ rgba8unorm |
/// | Rg16Float | R16G16_FLOAT | R16G16_SFLOAT ↓ rgba16f | RG16Float | tier1→rg16float ↓ float32-filterable→rg32float ↓ rgba16float |
///
/// Authoring note for effect writers: the format declared inside shader source must use the concrete backend format.
/// WGSL's texture_storage_3d&lt;FMT, write&gt; is therefore allowed to differ from the HLSL-side declaration.
/// Since all four shader sources are already separate, this adds no extra burden.
/// When a fallback increases the channel count, writing only .x or .xy is sufficient, and the reader side should likewise consume only the valid channels.
/// Support status: D3D12, WebGPU, Vulkan, and Metal are all wired up as of 1-8.
/// </summary>
public enum ComputeStorageFormat
{
    Rgba8Unorm,
    Rgba16Float,

    /// <summary>1-8: single-channel half precision, preferred for Global SDF distance fields and using one quarter of the memory of rgba16f.</summary>
    R16Float,

    /// <summary>1-8: single-channel 8-bit normalized, for scalar fields in [0,1] such as occlusion or coverage.</summary>
    R8Unorm,

    /// <summary>1-8: two-channel half precision, used by the probe depth atlas for mean distance + distance squared in Chebyshev visibility.</summary>
    Rg16Float,
}

/// <summary>Description of one binding slot. SizeInBytes is only used by Params, and StorageFormat is only used by StorageTextureWrite and StorageTexture3DWrite. Other types ignore those fields.</summary>
public struct ComputeBindingDesc
{
    public ComputeBindingType Type;

    /// <summary>Size in bytes of the Params block. Must be a multiple of 16 and ≤128. Use 0 for all other binding types.</summary>
    public uint SizeInBytes;

    /// <summary>Declared format for StorageTextureWrite and StorageTexture3DWrite.
    /// WebGPU bind group layouts require explicit format metadata, and other backends use this for validation.
    /// It must match both the format used when the bound texture was created through CreateComputeTexture or CreateComputeTexture3D and the format declared in shader source.</summary>
    public ComputeStorageFormat StorageFormat;
}

/// <summary>
/// Description of a compute kernel: shader source plus binding layout.
/// Backends use this to build the pipeline and binding layout once, including the D3D12 root signature, Vulkan descriptor set layout, Metal direct binding setup, and WebGPU bind group layout.
/// Per-frame dispatch then requires no layout parsing.
/// </summary>
public sealed class ComputeKernelDesc
{
    /// <summary>Diagnostic name, used for compile-error attribution and GPU debug markers.</summary>
    public string Name = "";

    public ShaderSourceSet Source = new();

    /// <summary>Binding-slot declarations. Order is part of the contract; see the ComputeBindingType summary.</summary>
    public ComputeBindingDesc[] Bindings = Array.Empty<ComputeBindingDesc>();

    /// <summary>
    /// Workgroup size for threadgroup/local_size, introduced in 1-8 and defaulting to 8×8×1, so existing kernels require no changes.
    /// Limits are the intersection of the minimum guarantees shared by all four backends, with Vulkan being the strictest:
    /// maxComputeWorkGroupSize=[128,128,64] and maxComputeWorkGroupInvocations=128, so X≤128, Y≤128, Z≤64, and X*Y*Z≤128.
    /// Violations cause CreateComputeKernel on all backends to throw ArgumentException, matching the existing parameter-validation style for programming errors.
    /// Going beyond 128 threads per group would require device capability queries and is not enabled in this phase.
    /// These three values must match the [numthreads], local_size_*, or @workgroup_size declarations in shader source.
    /// Metal is the only backend that consumes them at runtime because MSL has no compile-time declaration and instead uses the second parameter of DispatchThreadgroups.
    /// On the other three backends, shader declarations are authoritative, and these fields serve only as validation and self-documentation.
    /// </summary>
    public uint WorkgroupX = 8, WorkgroupY = 8, WorkgroupZ = 1;

    /// <summary>Validates workgroup-size limits. Called from each backend's CreateComputeKernel entry point. See the WorkgroupX summary.</summary>
    public void ValidateWorkgroupSize()
    {
        if (WorkgroupX == 0 || WorkgroupY == 0 || WorkgroupZ == 0)
            throw new ArgumentException($"ComputeKernel '{Name}': each WorkgroupSize axis must be >= 1, "
                + $"current value is {WorkgroupX}×{WorkgroupY}×{WorkgroupZ}");
        if (WorkgroupX > 128 || WorkgroupY > 128 || WorkgroupZ > 64)
            throw new ArgumentException($"ComputeKernel '{Name}': WorkgroupSize exceeds the shared four-backend limit "
                + $"X<=128/Y<=128/Z<=64, current value is {WorkgroupX}×{WorkgroupY}×{WorkgroupZ}");
        if (WorkgroupX * WorkgroupY * WorkgroupZ > 128)
            throw new ArgumentException($"ComputeKernel '{Name}': WorkgroupSize thread count "
                + $"{WorkgroupX * WorkgroupY * WorkgroupZ} exceeds the shared four-backend limit of 128 "
                + $"(the minimum guaranteed by VK maxComputeWorkGroupInvocations)");
    }
}

/// <summary>
/// Cross-platform opaque compute-kernel handle, aligned with the name-as-handle / wrapped-object model used by RenderTarget.
/// Backend implementations own the actual pipeline object, while the shared layer only keeps Desc and references.
/// </summary>
public abstract class ComputeKernel
{
    public ComputeKernelDesc Desc = null!;

    public abstract void Dispose();
}

/// <summary>Cross-platform opaque storage-buffer handle, resident on the GPU and consumed by particles, SDFGI, and GPU culling.</summary>
public abstract class StorageBuffer
{
    public uint SizeInBytes;

    public abstract void Dispose();
}

/// <summary>
/// Resource reference for dispatch. Exactly one of the three forms is used: storage texture by name through the backend texture dictionary, a storage-buffer handle, or an offscreen render target.
/// TextureName follows the same path as Sprite2D sampling, so once a compute output texture is registered into the backend texture dictionary, controls can consume it unchanged.
/// Target means an offscreen render target used as compute input, with the plane selected by binding type and any sampler-state conversion handled internally by backend DispatchCompute:
/// - SampledTexture slot: offscreen color RT with an SRV, for example bloom in the AfterScene phase using SceneColor as input. This was wired up in 2-1 Step A.
///   Backbuffer and MSAA wrapper forms have no SRV, so resolution failure skips dispatch for the current frame.
/// - DepthTexture slot: depth SRV from a depth-only RT, wired up in 2-2 Step A, with SSAO using FrameSchedule.SceneDepth as its source.
/// 1-8 adds no new field for 3D textures. They still use TextureName, and the binding type, SampledTexture3D or StorageTexture3DWrite, decides whether the backend looks in the 3D dictionary or the 2D dictionary,
/// so same-named 3D and 2D textures never interfere with each other.
/// </summary>
public struct ComputeResourceRef
{
    public string? TextureName;

    public StorageBuffer? Buffer;

    public RenderTarget? Target;

    public static implicit operator ComputeResourceRef(string textureName) => new() { TextureName = textureName };

    public static implicit operator ComputeResourceRef(StorageBuffer buffer) => new() { Buffer = buffer };

    public static implicit operator ComputeResourceRef(RenderTarget target) => new() { Target = target };
}

/// <summary>
/// Arguments for one dispatch call. This is a ref struct so Params and Resources can borrow stack data or cached arrays with zero hot-path allocation.
/// Params length must equal Bindings[0].SizeInBytes, and Resources must align with binding declaration order while skipping the Params slot.
/// Note that ComputeResourceRef arrays containing string references cannot be stackalloc'd, so effect classes should cache and reuse readonly array fields.
/// </summary>
public ref struct ComputeDispatchArgs
{
    public ComputeKernel Kernel;

    public ReadOnlySpan<byte> Params;

    public ReadOnlySpan<ComputeResourceRef> Resources;

    public uint GroupsX, GroupsY, GroupsZ;
}

/// <summary>
/// In-frame compute phase. This follows the strictest platform contract that dispatch may occur only outside render passes:
/// Vulkan hard-forbids dispatch inside a pass, and on Metal the compute encoder and render encoder are mutually exclusive, so all four backends follow this rule.
/// - FrameStart: after BeginFrame and before the first render pass, for particle simulation, SDFGI propagation, GPU culling, and procedural textures;
/// - AfterScene: after the Scene pass and before the Post pass, for bloom downsampling and SSAO, which need SceneColor after it has been written.
/// </summary>
public enum ComputePhase
{
    FrameStart,
    AfterScene,
}

/// <summary>
/// Base class for compute effects, shared by built-in engine effects under Effects/ and third-party custom effects.
/// Lifecycle: FrameSchedule.RegisterCompute → Initialize to compile kernels and create resources, with false meaning registration stops cleanly and leaves no backend residue on failure
/// → per-frame Record called by phase, where only DispatchCompute is allowed and opening/closing passes, drawing, or issuing barriers is forbidden because synchronization is centralized inside backend DispatchCompute
/// → after UnregisterCompute, the effect disposes its own resources.
/// </summary>
public abstract class ComputeEffect
{
    public abstract string Name { get; }

    public abstract ComputePhase Phase { get; }

    /// <summary>Compiles kernels and creates resident resources. Returning false means the current backend has no source or compilation failed, so registration is aborted.</summary>
    public abstract bool Initialize(IGraphics g);

    /// <summary>Records dispatch work every frame, called from the frame-loop thread. Only DispatchCompute is allowed here, with zero allocations.</summary>
    public abstract void Record(IGraphics g);

    /// <summary>Window resize notification, driven by FrameSchedule.ResizeCompute after the GPU becomes idle.
    /// Effects override this to rebuild storage textures in place against the new DeviceResolution.
    /// CreateComputeTexture has been upgraded so a size mismatch triggers in-place recreation while preserving the C# object identity, which keeps Sprite2D AddRef references valid.
    /// Kernels do not need rebuilding. Fixed-size effects such as DepthView, SceneColorCopy, and Plasma can keep the default no-op implementation.</summary>
    public virtual void OnResize(IGraphics g) { }
}
