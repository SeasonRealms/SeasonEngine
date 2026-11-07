// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct InstanceTransformData
{
    public Vector4 Row0;
    public Vector4 Row1;
    public Vector4 Row2;
    public Vector4 Row3;
    public Vector4 MorphWeights;

    public static InstanceTransformData FromWorld(Matrix4x4 world)
    {
        return new InstanceTransformData
        {
            Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
            Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
            Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
            Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
            MorphWeights = Vector4.Zero,
        };
    }
}

/// <summary>
/// One slot of the generic per-instance extras stream (set 0, binding 26), addressed by the same <b>compacted</b> slot as
/// the instance matrices: <c>boneBase + gl_InstanceIndex</c> on a direct draw, the index gather's M14 stamp on an A7
/// indirect draw.
/// <para>
/// It lives outside the 80-byte <see cref="InstanceTransformData"/> on purpose. That stream is bit-identical to the A7 cull
/// input and to the IA instance buffer, and it is also what the text path reinterprets, so growing its stride would move
/// culling, gather and text at once. A separate stream costs one vertex-stage storage read instead.
/// </para>
/// <para>
/// <see cref="Color"/> is the per-instance tint the PBR path multiplies its albedo by, gated per primitive through
/// <c>MaterialParams.TintMode</c> so a hull sharing an instance with a tinted sail keeps its authored look.
/// <see cref="Extra"/> is unused today and is the reason this type is 32 bytes: a later per-instance scalar (wind weight,
/// decal index, morph bias) then costs no new binding and no new descriptor.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InstanceExtras
{
    public Vector4 Color;
    public Vector4 Extra;
}

/// <summary>
/// Base class for GPU-instanced primitive groups (Vulkan).
/// Unifies instance-buffer management, transparent sorting,
/// and three-bucket grouped drawing.
/// Derived classes: VKInstancedModel (glTF template cloning) /
/// VKInstancedMesh3D (code-generated geometry).
///
/// A7c note -- why this backend still uploads its own copy of the cull input every frame instead of aliasing the IA instance buffer the way D3D12 does:
/// a VkBuffer's usage flags are fixed at vkCreateBuffer time, while the instance buffer is created through CreateVertexBuffer (VERTEX_BUFFER_BIT only).
/// Aliasing would mean baking STORAGE_BUFFER_BIT into that creation, which on the host-visible memory Android uses risks an allocation failure, and having
/// the GPU read host-visible memory is slower than reading the device-local copy it would replace. The bytes are the same either way, so the culling
/// result is identical to the D3D12 path; only the transport differs.
/// </summary>
internal unsafe abstract class VKInstancedPrimitiveGroup : VKPrimitiveGroup, Season.Rendering.IInstanceCullTarget
{
    // The primitive buckets, the per-instance world matrices, the transparent instance order, the wireframe /
    // Outline2D activation state and the merged cull box all live in PrimitiveGroupCore.Instanced.cs: they are
    // CPU-side bookkeeping that every instance backend keeps identical, and the transparent sort reads the world
    // matrices, so the two cannot be separated. Only the Vulkan instance stream below is backend state.
    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();

    /// <summary>This frame's IA instance buffer: <see cref="UploadInstanceStream"/> points it at the ring slot the frame writes.</summary>
    protected BufferResource _instanceBuffer;
    /// <summary>Instance stream ring, one slot per frame in flight. The buffer is written through host-visible memory
    /// every frame, so a single buffer would let frame k overwrite the bytes frame k-2 is still reading on the GPU.</summary>
    protected BufferResource[] _instanceBuffers = Array.Empty<BufferResource>();
    protected int _instanceCapacity;

    /// <summary>Ring slot this frame writes and reads.</summary>
    protected static int InstanceFrameSlot => (int)(Device.FrameIndex % Device.frameCount);

    protected VKInstancedPrimitiveGroup(string name)
    {
        Name = name;
    }

    // ============================================================
    // A7: per-instance GPU culling
    // ============================================================
    // Derived classes call UpdateCullUpload after uploading their own instances; groups that never
    // call it stay all-null, so the shared layer bails out early and shadows fall back to CPU
    // whole-batch culling.
    Season.Rendering.StorageBuffer? _cullSource;
    /// <summary>One reorder stream per gather stream (see <see cref="Season.Rendering.IInstanceCullTarget.CullStreamCount"/>): length 1 for a
    /// group whose primitives share one instance stream, one per opaque primitive when <see cref="CullPerPrimitiveStreams"/>. Empty means no A7.</summary>
    Season.Rendering.StorageBuffer?[] _cullSorted = Array.Empty<Season.Rendering.StorageBuffer?>();
    /// <summary>Per-primitive-stream gather sources, parallel to <see cref="_cullSorted"/>. Empty for a single-stream group, whose one gather
    /// source is the cull input <see cref="_cullSource"/> itself -- uploading the same bytes twice would buy nothing.</summary>
    Season.Rendering.StorageBuffer?[] _cullStreamSources = Array.Empty<Season.Rendering.StorageBuffer?>();
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    int _cullCapacity = -1;
    /// <summary>Number of primitive-info entries already uploaded to the GPU. When it differs from <see cref="_cullPrimInfoPending"/>, this frame must re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>CPU-side prebuilt primitive-info bytes: building can happen at any time, but the upload
    /// must wait until the command list enters the recording state
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>Number of CPU-side primitive-info entries; -1 means it has never been built.</summary>
    int _cullPrimInfoPending = -1;
    /// <summary>A7b: the arg-buffer image composed from <see cref="_cullPrimInfoBytes"/>. It exists for the same reason as that table --
    /// its content only changes with the primitive set, so the per-frame path is one "is it stale" comparison, and the per-frame reset
    /// dispatch it replaces is gone.</summary>
    byte[]? _cullArgsTemplate;
    bool _cullRegistered;
    bool _cullDrawReady;
    /// <summary>The owner's effective draw path is <see cref="Season.Controls.InstanceDrawPath.Indirect"/>, latched in Update so the cull dispatch and the draw see the same answer.</summary>
    bool _cullMainView;

    /// <summary>
    /// A7 multi-stream hook. False (default): every primitive reads the one group instance stream, which is also the cull input.
    /// True: opaque primitive i reads its own instance stream i (a glTF model, whose stream is "node world × instance world" plus that
    /// primitive's morph weights), so gather materializes one reordered stream per opaque primitive from the shared index table, and
    /// the derived class supplies each stream's bytes through <see cref="GetCullDrawStream"/>. The cull input stays the base
    /// <see cref="_instanceData"/>, which the derived class fills with the root instance worlds.
    /// </summary>
    protected virtual bool CullPerPrimitiveStreams => false;

    /// <summary>CPU bytes of per-primitive stream <paramref name="stream"/> (index into <see cref="_opaquePrimitives"/>), in the same
    /// instance order as <see cref="_instanceData"/>. Only called when <see cref="CullPerPrimitiveStreams"/> is true.</summary>
    protected virtual ReadOnlySpan<InstanceTransformData> GetCullDrawStream(int stream) => default;

    /// <summary>
    /// The index range the indirect args must replay for <paramref name="primitive"/>. <see cref="Season.Rendering.InstanceCull.BuildArgsTemplate"/> writes
    /// it into every slot's args and the indirect draw no longer passes a range of its own, so it must come from the same source as the direct draw's
    /// vkCmdDrawIndexed arguments -- a group drawing a sub-range of a shared index buffer overrides this, otherwise its indirect
    /// draws would replay the whole buffer from index 0. The default mirrors the indexCount every direct call site passes.
    /// </summary>
    protected virtual void GetCullPrimitiveRange(PrimitiveData primitive, out uint indexCount, out uint startIndex, out int baseVertex)
    {
        indexCount = (uint)primitive.Indices.Length;
        startIndex = 0u;
        baseVertex = 0;
    }

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceCapacity == count && _instanceBuffers.Length == (int)Device.frameCount)
            return;

        for (int i = 0; i < _instanceBuffers.Length; i++)
        {
            // Never destroy a vertex buffer that a frame in flight may still reference. A capacity change happens at most
            // one frame after the previous one, so the old buffer is still bound by pending command buffers; destroying it
            // here is VUID-vkDestroyBuffer-buffer-00922, and on Android tilers it can read freed pages.
            var retired = _instanceBuffers[i];
            if (retired.Buffer.Handle != 0)
                Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
        }

        int slots = (int)Device.frameCount;
        var buffers = new BufferResource[slots];
        for (int i = 0; i < slots; i++)
            buffers[i] = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);

        _instanceBuffers = buffers;
        _instanceCapacity = count;
    }

    /// <summary>
    /// Uploads this frame's instance stream into this frame's ring slot and makes <see cref="_instanceBuffer"/> point at it.
    /// Derived Update methods call this at the end, so every draw this frame binds the bytes they just produced.
    /// </summary>
    protected void UploadInstanceStream(InstanceTransformData[] data, int count)
    {
        int slot = InstanceFrameSlot;
        if ((uint)slot >= (uint)_instanceBuffers.Length)
            return;

        _instanceBuffer = _instanceBuffers[slot];
        if (_instanceBuffer.Buffer.Handle == 0 || count <= 0)
            return;

        Device.ResourceManager.UpdateBuffer(_instanceBuffer, data);
    }

    // ============================================================
    // Generic per-instance extras (binding 26)
    // ============================================================

    /// <summary>This frame's extras records, one per compacted instance slot, filled by the derived Update alongside
    /// <see cref="_instanceData"/>.</summary>
    protected InstanceExtras[] _instanceExtras = Array.Empty<InstanceExtras>();
    BufferResource[] _instanceExtrasBuffers = Array.Empty<BufferResource>();
    // Pointer-sized handles, not byte*: a pointer cannot be a generic argument, so the mapped addresses are stored
    // as IntPtr and cast where they are written.
    IntPtr[] _mappedInstanceExtrasBuffers = Array.Empty<IntPtr>();
    int _instanceExtrasCapacity;

    /// <summary>
    /// Closes the loop between the descriptor set and the extras ring: the sets are written at load, when the instance
    /// count is still unknown, so they start out on the shared white record. Whenever the ring is (re)created this
    /// republishes every set, exactly as a bone-capacity growth does, because a set still pointing at the 1-element
    /// placeholder would be read out of bounds at slot 1 and beyond.
    /// </summary>
    protected void EnsureInstanceExtrasCapacity(int count)
    {
        if (count <= 0 || count <= _instanceExtrasCapacity)
            return;

        for (int i = 0; i < _instanceExtrasBuffers.Length; i++)
        {
            var retired = _instanceExtrasBuffers[i];
            if (retired.Memory.Handle == 0)
                continue;
            if (i < _mappedInstanceExtrasBuffers.Length && _mappedInstanceExtrasBuffers[i] != IntPtr.Zero)
                Device.Vk.UnmapMemory(Device.LogicalDevice, retired.Memory);
            // Same rule as the instance stream: a frame in flight may still read the old buffer through binding 26.
            Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
        }

        int n = (int)Device.frameCount;
        ulong size = (ulong)(count * Unsafe.SizeOf<InstanceExtras>());
        var buffers = new BufferResource[n];
        var mapped = new IntPtr[n];

        for (int i = 0; i < n; i++)
        {
            buffers[i] = Device.ResourceManager.CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* ptr;
            if (Device.Vk.MapMemory(Device.LogicalDevice, buffers[i].Memory, 0, size, 0, &ptr) != Result.Success)
                throw new Exception("vkMapMemory (InstanceExtras) failed");
            mapped[i] = (IntPtr)ptr;

            // White, not zero: a slot no instance claimed this frame must multiply to itself, and a zeroed record would
            // turn every tinted part of it black for one frame instead.
            var records = (InstanceExtras*)ptr;
            for (int r = 0; r < count; r++)
                records[r] = new InstanceExtras { Color = Vector4.One, Extra = Vector4.Zero };
        }

        _instanceExtrasBuffers = buffers;
        _mappedInstanceExtrasBuffers = mapped;
        _instanceExtrasCapacity = count;

        for (int i = 0; i < _primitives.Count; i++)
            RewriteDescriptorSets(_primitives[i]);
        RewriteShellDescriptorSets();
    }

    /// <summary>Copies this frame's extras records into this frame's ring slot. Contrast with the instance stream, this
    /// one is not a vertex buffer: it is only ever read through binding 26.</summary>
    protected void UploadInstanceExtras(int count)
    {
        int slot = InstanceFrameSlot;
        if (count <= 0 || (uint)slot >= (uint)_mappedInstanceExtrasBuffers.Length)
            return;
        if (_mappedInstanceExtrasBuffers[slot] == IntPtr.Zero || count > _instanceExtras.Length)
            return;

        fixed (InstanceExtras* src = _instanceExtras)
            Unsafe.CopyBlock((byte*)_mappedInstanceExtrasBuffers[slot], src, (uint)(count * Unsafe.SizeOf<InstanceExtras>()));
    }

    /// <summary>Whether binding 26 for this frame slot points at the group's own ring (false means the white placeholder).</summary>
    protected override DescriptorBufferInfo GetInstanceExtrasBufferInfo(int fi)
    {
        if (_instanceExtrasCapacity > 0 && (uint)fi < (uint)_instanceExtrasBuffers.Length
            && _instanceExtrasBuffers[fi].Buffer.Handle != 0)
            return new DescriptorBufferInfo
            { Buffer = _instanceExtrasBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

        return base.GetInstanceExtrasBufferInfo(fi);
    }

    /// <summary>
    /// Main pass for the instance group. Overrides the core entry rather than hiding it, so a
    /// <see cref="Season.Rendering.Drawables.PrimitiveGroupCore{TPrim, TMat, TTex}"/> reference dispatches here.
    /// The bucket walk, the fade decision and the two-pass transparent split live in
    /// <c>PrimitiveGroupCore.DrawInstancedMainPass</c>.
    /// </summary>
    public override void Draw() => DrawInstancedMainPass();

    protected override void OnBeginMainPass()
    {
        DrawTrace.Group("vk", "main", Name, _opaquePrimitives.Count);

        // Pin this group on the snapshot page during collection: during replay the instance buffer and the primitive descriptor sets are still referenced.
        VKSceneDrawCollection.RetainOwner(this);
    }

    protected override void DrawOpaqueInstances(PrimitiveData primitive, int streamIndex, bool fadeInsteadOfOpaque)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;

        // A7 main view: the opaque body reads the culled, compacted main-view segment and takes its instance count from the
        // indirect args. Fade stays direct: the scene collector splits a Fade draw per instance for back-to-front order,
        // which a GPU-sized draw cannot be, and a fading group is about to change visibility anyway.
        if (!fadeInsteadOfOpaque && TryGetMainViewSegment(0, out var sortedBuffer, out ulong segmentOffset, out var argsBuffer))
        {
            Pipeline.SetPipeline(cmd, PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                sortedBuffer, 0, 0, float.NaN,
                argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, streamIndex),
                segmentOffset);
            Season.Rendering.InstanceCull.MainViewDraws++;
            return;
        }

        // Use the Opaque PSO when overall Alpha >= 1, otherwise use Fade
        Pipeline.SetPipeline(cmd, fadeInsteadOfOpaque ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
        Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
            primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
            _instanceBuffer.Buffer, (uint)_instanceCount, 0);
    }

    protected override void DrawTransparentInstance(PrimitiveData primitive, int instanceIndex, bool frontFace)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;

        // The instance matrices exist only on the CPU side (_instanceWorlds); the GPU-side
        // instance stream cannot be read back. So the transparent sort depth must be supplied
        // here, otherwise collection can only fall back to primitive-level depth.
        float depth = ComputeTransparentDepth(_instanceWorlds[instanceIndex], primitive.LocalBoundsCenter);

        Pipeline.SetPipeline(cmd, PipelineMode.Transparent, frontFace ? CullModeFlags.FrontBit : CullModeFlags.BackBit);
        Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
            primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
            _instanceBuffer.Buffer, 1, (uint)instanceIndex, depth);
    }

    // Unified highlighting: per-instance bounds boxes plus wireframe shell boxes, drawn after all surfaces.
    protected override void DrawInstanceBoundsBoxes() => DrawBoundsBoxes();

    protected override void DrawInstanceShellBoxes() => DrawShellBoxes(_instanceBuffer);

    /// <summary>
    /// A7: whether the opaque body may draw from the main-view segment right now, and where it is.
    /// Every condition is a reason the segment would be wrong rather than merely slower, so any miss falls back to the direct draw:
    /// the owner did not ask for it; this frame's chain did not run or did not include the main-view slot (its args would still
    /// hold reset's zero instances and the group would vanish); or the view being drawn is not the one the segment was culled for
    /// (mirrors, minimaps).
    /// <paramref name="stream"/> is the gather stream: 0 for a single-stream group, the opaque-primitive index for a per-primitive one.
    /// </summary>
    protected bool TryGetMainViewSegment(int stream, out VkBuffer sortedBuffer, out ulong segmentOffset, out VkBuffer argsBuffer)
    {
        sortedBuffer = default;
        segmentOffset = 0;
        argsBuffer = default;
        if (!_cullMainView || !_cullDrawReady || !Season.Rendering.InstanceCull.MainViewCulled || _cullCapacity < _instanceCount)
            return false;
        if (!ReferenceEquals(RenderView.CurrentOrNull, DeviceServices.BaseApp?.MainView))
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not VKStorageBuffer sorted || sorted.Buffer.Buffer.Handle == 0
            || _cullArgs is not VKStorageBuffer args || args.Buffer.Buffer.Handle == 0)
            return false;

        sortedBuffer = sorted.Buffer.Buffer;
        segmentOffset = (ulong)(Season.Rendering.InstanceCull.MainViewSlot * _cullCapacity) * Season.Rendering.InstanceCull.WorldStride;
        argsBuffer = args.Buffer.Buffer;
        return true;
    }

    /// <summary>
    /// A7 shadow: when this frame's culling chain is ready, the slot segment of gather stream <paramref name="stream"/> for the shadow
    /// view being drawn (<see cref="Season.Rendering.CascadedShadow.ActiveSlot"/>) and the args buffer its counts come from. Any miss
    /// falls back to the direct whole-batch shadow draw, never to an empty one.
    /// The offset goes on the binding rather than in the args: firstInstance would shift the starting point of gl_InstanceIndex.
    /// </summary>
    protected bool TryGetShadowSegment(int stream, out VkBuffer sortedBuffer, out ulong instanceOffset, out VkBuffer argsBuffer, out int slot)
    {
        sortedBuffer = default;
        instanceOffset = 0;
        argsBuffer = default;
        slot = Season.Rendering.CascadedShadow.ActiveSlot;
        if (!_cullDrawReady || _cullCapacity < _instanceCount)
            return false;
        // Not a range test against MaxSlots: the far slot and the local cube slots sit inside that range, but the chain never writes their
        // arg rows, so accepting one here would submit an empty batch where the whole-batch draw below would have been correct.
        if (!Season.Rendering.InstanceCull.ShadowSlotActive(slot))
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not VKStorageBuffer sorted || sorted.Buffer.Buffer.Handle == 0
            || _cullArgs is not VKStorageBuffer args || args.Buffer.Buffer.Handle == 0)
            return false;

        sortedBuffer = sorted.Buffer.Buffer;
        instanceOffset = (ulong)(slot * _cullCapacity) * Season.Rendering.InstanceCull.WorldStride;
        argsBuffer = args.Buffer.Buffer;
        return true;
    }

    /// <summary>
    /// 1-5 Shadow pass: draw the full set of opaque primitives against the full set of instances
    /// in one batch (no culling, no sorting; transparent buckets are skipped).
    /// Shares the group-level Matrix UBO + instance buffer and uses the same source as the main pass.
    /// Mirrors DX 1:1.
    /// The loop and the skip accounting live in <c>PrimitiveGroupCore.DrawInstancedShadowPass</c>.
    /// </summary>
    public override void DrawShadow() => DrawInstancedShadowPass();

    protected override void OnBeginShadowPass()
    {
        DrawTrace.Group("vk", "shadow", Name, _opaquePrimitives.Count);
        // Binding-9 palette refresh. Vulkan reports no shadow-material sharing, so the core's bind flag is unused.
        OnBeforeDraw();
    }

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginInstancePrimitive(PrimitiveData primitive) => true;

    protected override void DrawShadowInstance(PrimitiveData primitive, int streamIndex, bool bindMaterial)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;

        // A7: when this frame's cull chain is ready, the instance stream instead reads this slot's reordered segment, with index count and instance count supplied by the indirect args.
        if (TryGetShadowSegment(0, out var sortedBuffer, out ulong instanceOffset, out var argsBuffer, out int slot))
        {
            Pipeline.DrawShadowPrimitiveIndirect(cmd, primitive,
                primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi],
                sortedBuffer, instanceOffset,
                argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(slot, streamIndex));
            return;
        }

        Pipeline.DrawShadowPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
            primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
            _instanceBuffer.Buffer, (uint)_instanceCount, 0);
    }

    /// <summary>
    /// Outline2D mask: rendered through the instance stream
    /// (host-level activation -> one full batch; per-instance activation -> one draw per instance
    /// with instanceCount = 1 by writeIndex slot).
    /// The base non-instanced DrawOutlineMask renders the instance stream with an identity matrix
    /// (causing missing/misaligned instances), so this must be overridden.
    /// Transparent buckets are skipped, matching the base mask semantics.
    /// The loop and the slot guard live in <c>PrimitiveGroupCore.DrawInstancedOutlineMask</c>.
    /// </summary>
    public override void DrawOutlineMask() => DrawInstancedOutlineMask();

    /// <summary>FS push constant: one colour for the whole group. Several colours may occur in one frame;
    /// the value is the colour fixed during Update (instance colour or host colour).</summary>
    protected override void OnBeginOutlineMaskPass()
        => Pipeline.SetOutlineMaskColor(Device.GraphicsCommandBuffer, _outline2DColor);

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginOutlineMaskPrimitive(PrimitiveData primitive)
    {
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
            primitive.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
        // Binding-9 palette refresh: the mask reads the same instance palette as the main pass.
        OnBeforeDraw();
        return true;
    }

    protected override void DrawOutlineMaskBatch(PrimitiveData primitive)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;
        Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
            primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
            _instanceBuffer.Buffer, (uint)_instanceCount, 0);
    }

    protected override void DrawOutlineMaskInstance(PrimitiveData primitive, int instanceIndex, int slot)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;
        // The per-slot mask fetches this instance's own OutlineColor.
        Pipeline.SetOutlineMaskColor(cmd, _outline2DInstanceColors[slot]);
        Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
            primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
            _instanceBuffer.Buffer, 1, (uint)instanceIndex);
    }

    // ============================================================
    // A7: per-instance GPU culling -- resources and handoff
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => _instanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _opaquePrimitives.Count;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds => _cullSource;

    int Season.Rendering.IInstanceCullTarget.CullStreamCount => _cullSorted.Length;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSource(int stream)
        => _cullStreamSources.Length == 0 ? (stream == 0 ? _cullSource : null)
            : (uint)stream < (uint)_cullStreamSources.Length ? _cullStreamSources[stream] : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSorted(int stream)
        => (uint)stream < (uint)_cullSorted.Length ? _cullSorted[stream] : null;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullArgs => _cullArgs;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullVisible => _cullVisible;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalCenter => _cullLocalCenter;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalExtents => _cullLocalExtents;

    bool Season.Rendering.IInstanceCullTarget.CullMainView => _cullMainView;

    /// <summary>Derived classes call this after instance upload finishes: prepares the cull resources.
    /// The matrix stream upload is deferred to the Record phase, see
    /// <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>.</summary>
    protected void UpdateCullUpload(Season.Controls.InstancedMesh3DBase owner)
    {
        _cullDrawReady = false;
        _cullMainView = owner.EffectiveDrawPath == Season.Controls.InstanceDrawPath.Indirect;
        EnsureCullResources(_instanceCount);
    }

    /// <summary>Capacity is aligned to 64 so small instance-count jitter does not recreate the five buffers every frame.
    /// A per-primitive-stream group also rebuilds when its opaque-primitive count changes, since each of those owns a stream pair; a
    /// group with more primitives than <see cref="Season.Rendering.InstanceCull.MaxPrimitives"/>, or with no opaque primitive at all,
    /// cannot join and releases instead, so it keeps the direct path rather than registering buffers nothing will read.</summary>
    void EnsureCullResources(int count)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // An instance count of 0 does not mean we should release: hiding and re-showing would then recreate the five buffers every frame.
        if (count <= 0)
            return;

        bool perPrimitive = CullPerPrimitiveStreams;
        int streams = perPrimitive ? _opaquePrimitives.Count : 1;
        if (perPrimitive && (streams <= 0 || streams > Season.Rendering.InstanceCull.MaxPrimitives))
        {
            ReleaseCullResources();
            return;
        }

        int capacity = (count + 63) / 64 * 64;
        if (_cullCapacity != capacity || _cullSorted.Length != streams
            || _cullStreamSources.Length != (perPrimitive ? streams : 0))
        {
            ReleaseCullResources();
            var sorted = new Season.Rendering.StorageBuffer?[streams];
            var streamSources = perPrimitive ? new Season.Rendering.StorageBuffer?[streams] : Array.Empty<Season.Rendering.StorageBuffer?>();
            bool complete = true;
            try
            {
                var g = Season.Basic.Graphics.Instance;
                _cullSource = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                // The reordered stream is bound directly as a vertex stream, so it must be declared
                // ComputeAndVertexInput: VertexBufferBit is baked into the creation-time
                // VkBufferUsageFlags and cannot be added afterwards.
                for (int s = 0; s < streams; s++)
                {
                    sorted[s] = g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(capacity),
                        Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
                    if (perPrimitive)
                        streamSources[s] = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                }
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                // A7b: ArgsTotalBytes, not ArgsBytes: the per-slot visible-count accumulator shares this block, which is what removed
                // the need for a per-frame reset dispatch without adding a second buffer or a second upload path per backend.
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsTotalBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] Buffer creation failed: {ex.Message}");
            }

            // Hand the arrays over before checking, so a partial failure is released through the one path below.
            _cullSorted = sorted;
            _cullStreamSources = streamSources;
            foreach (var b in sorted)
                complete &= b != null;
            foreach (var b in streamSources)
                complete &= b != null;

            if (!complete || _cullSource == null || _cullVisible == null
                || _cullArgs == null)
            {
                ReleaseCullResources();
                return;
            }

            _cullCapacity = capacity;
            _cullPrimitiveCount = -1;
        }

        EnsureCullPrimitiveInfo();

        if (!_cullRegistered)
        {
            Season.Rendering.InstanceCull.Register(this);
            _cullRegistered = true;
        }
    }

    /// <summary>
    /// Only builds the primitive-info bytes and never touches the GPU: building and uploading have
    /// different safe timings -- building can happen at any time, while uploading must wait until
    /// the command buffer enters the recording state
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).
    /// Nothing is rebuilt while the primitive set stays unchanged. The range comes from <see cref="GetCullPrimitiveRange"/>, the same
    /// source as the direct draw's, because the arg template writes it into the indirect args and the draw no longer passes one.
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullArgs == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // Explicitly record that there is no primitive-info table to upload: otherwise
            // PrepareCullInputs would repeatedly upload the previous bytes, and the entry count
            // would look right while the contents were the stale copy.
            _cullPrimInfoBytes = null;
            _cullArgsTemplate = null;
            _cullPrimInfoPending = 0;
            _cullPrimitiveCount = 0;
            return;
        }
        if (_cullPrimInfoPending == n)
            return;

        var bytes = new byte[n * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride];
        for (int i = 0; i < n; i++)
        {
            GetCullPrimitiveRange(_opaquePrimitives[i], out uint indexCount, out uint startIndex, out int baseVertex);
            Season.Rendering.InstanceCull.WritePrimitiveInfo(bytes,
                i * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride,
                indexCount, startIndex, baseVertex);
        }

        _cullPrimInfoBytes = bytes;
        _cullPrimInfoPending = n;
        // A7b: the arg image is composed here too -- it is a pure function of this table and the fixed slot layout, i.e. exactly the
        // bytes the reset kernel used to write every frame, built once at the moment its input changes.
        _cullArgsTemplate = Season.Rendering.InstanceCull.BuildArgsTemplate(bytes, n);
    }

    /// <summary>
    /// A7: upload the CPU-side cull inputs to the GPU. InstanceCullEffect calls this before every
    /// dispatch; that phase runs after BeforeRender and outside all passes, and is the only time
    /// the Vulkan command buffer is in the recording state -- during a control's Update phase
    /// IsRecording is false, so uploads would be silently dropped and the buffer would stay all
    /// zeros.
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        if (!Season.Rendering.InstanceCull.Enabled || _instanceCount <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // The primitive-info table follows the geometry, not the frame, so it is re-uploaded only when the entry count changes.
        // A7b: what it writes is frame-invariant (index ranges) or re-derived by the GPU later in the same frame (instance counts,
        // published by gather), which is why this upload could take over from the per-frame reset dispatch:
        // the arg table only has to be framed once per primitive-set change, not once per frame.
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullArgsTemplate == null || _cullArgs == null)
                return false;
            if (!g.TryUpdateStorageBuffer(_cullArgs, _cullArgsTemplate))
                return false;
            _cullPrimitiveCount = _cullPrimInfoPending;
        }

        // The matrix stream changes every frame, so upload only the valid range and no slack.
        if (_cullSource == null || !g.TryUpdateStorageBuffer(_cullSource,
                MemoryMarshal.AsBytes(_instanceData.AsSpan(0, _instanceCount))))
            return false;

        // Per-primitive streams: gather replays the one visibility decision onto each primitive's own stream, so each must hold
        // this frame's instances in the cull input's order. A stream shorter than the instance count would make gather read past
        // what the CPU wrote, so the whole group skips A7 this frame instead.
        for (int s = 0; s < _cullStreamSources.Length; s++)
        {
            var data = GetCullDrawStream(s);
            var source = _cullStreamSources[s];
            if (source == null || data.Length < _instanceCount
                || !g.TryUpdateStorageBuffer(source, MemoryMarshal.AsBytes(data.Slice(0, _instanceCount))))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A7: transition the reordered stream and args buffer written by gather into a
    /// draw-readable state. This must run outside a pass, so InstanceCullEffect calls it after
    /// the three dispatches.
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullSorted.Length == 0)
            return false;
        foreach (var s in _cullSorted)
        {
            if (s is not VKStorageBuffer sb || sb.Buffer.Buffer.Handle == 0)
                return false;
        }
        if (_cullArgs is not VKStorageBuffer args || args.Buffer.Buffer.Handle == 0)
            return false;
        if (_cullCapacity <= 0)
            return false;

        var cmd = Device.GraphicsCommandBuffer;
        if (cmd.Handle == 0 || Device.InRenderPass)
            return false;

        // Vertex attribute reads happen in the VertexInput stage, which cannot substitute for the DrawIndirect stage of TryPrepareIndirectArgs.
        foreach (var s in _cullSorted)
            ((VKStorageBuffer)s!).BarrierToVertexInput(cmd);

        if (!Season.Basic.Graphics.Instance.TryPrepareIndirectArgs(args))
            return false;

        _cullDrawReady = true;
        return true;
    }

    protected void ReleaseCullResources()
    {
        if (_cullRegistered)
        {
            Season.Rendering.InstanceCull.Unregister(this);
            _cullRegistered = false;
        }
        _cullSource?.Dispose(); _cullSource = null;
        foreach (var s in _cullSorted)
            s?.Dispose();
        _cullSorted = Array.Empty<Season.Rendering.StorageBuffer?>();
        foreach (var s in _cullStreamSources)
            s?.Dispose();
        _cullStreamSources = Array.Empty<Season.Rendering.StorageBuffer?>();
        _cullArgs?.Dispose(); _cullArgs = null;
        _cullVisible?.Dispose(); _cullVisible = null;
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimInfoBytes = null;
        _cullArgsTemplate = null;
        _cullPrimInfoPending = -1;
        _cullDrawReady = false;
    }

    /// <summary>Return the owned Primitives collection
    /// for base operations such as SyncAlpha / ReplaceTextureBySlot.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    /// <summary>
    /// Destroy the instance buffer and clear all primitive collections.
    /// Derived classes should release their own GPU resources before calling base.Dispose().
    /// </summary>
    public override void Dispose()
    {
        ReleaseCullResources();

        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();
        _transparentInstanceOrder.Clear();

        for (int i = 0; i < _instanceBuffers.Length; i++)
        {
            if (_instanceBuffers[i].Buffer.Handle != 0)
                Device.ResourceManager.DestroyBuffer(_instanceBuffers[i]);
        }
        _instanceBuffers = Array.Empty<BufferResource>();
        _instanceBuffer = default;
    }
}
