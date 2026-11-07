// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.Apple.Metal;

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
/// One slot of the generic per-instance extras stream (VS buffer 12), byte-identical to DX's t20 record and VK's binding 26,
/// addressed by the same <b>compacted</b> slot as the instance matrices: <c>instance_id</c> on a direct draw, the index
/// gather's M14 stamp on an A7 indirect draw.
/// <para>
/// It lives outside the 80-byte <see cref="InstanceTransformData"/> on purpose. That stream is bit-identical to the A7 cull
/// input and to the instance buffer the vertex descriptor feeds, and it is also what the text path reinterprets, so growing
/// its stride would move culling, gather and text at once. A separate stream costs one vertex-stage read instead.
/// </para>
/// <para>
/// <see cref="Color"/> is the per-instance tint the PBR path multiplies its albedo by, gated per primitive through
/// <c>MaterialParams.TintMode</c> so a hull sharing an instance with a tinted sail keeps its authored look.
/// <see cref="Extra"/> is unused today and is the reason this type is 32 bytes: a later per-instance scalar (wind weight,
/// decal index, morph bias) then costs no new binding and no new buffer.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InstanceExtras
{
    public Vector4 Color;
    public Vector4 Extra;
}

/// <summary>
/// Base class for GPU-instanced primitive groups on Metal.
/// It centralizes instance-buffer management, transparent sorting, and three-bucket grouped rendering.
/// Derived types include MTLInstancedModel for glTF template cloning
/// and MTLInstancedMesh3D for generated geometry.
///
/// A7c note -- the cull input is still uploaded every frame on purpose. The instance stream is a frame ring (one slot per
/// frame in flight) and the upload lands in this frame's slot, so an alias would have to be rebuilt and re-validated on every
/// frame boundary, while all it would save is a memcpy that unified memory already makes nearly free. Metal keeps the copy;
/// the bytes are identical, so the picture matches the D3D12 path exactly.
/// </summary>
internal abstract class MTLInstancedPrimitiveGroup : MTLPrimitiveGroup, Season.Rendering.IInstanceCullTarget
{
    // The primitive buckets, the per-instance world matrices, the transparent instance order, the wireframe /
    // Outline2D activation state and the merged cull box all live in PrimitiveGroupCore.Instanced.cs: they are
    // CPU-side bookkeeping that every instance backend keeps identical, and the transparent sort reads the world
    // matrices, so the two cannot be separated. Only the Metal instance stream below is backend state.
    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();

    protected IMTLBuffer _instanceBuffer = null!;
    protected int _instanceCapacity;

    /// <summary>
    /// Contract clause 8(c) of 2-3: the instance stream is a frame ring with one slot per in-flight frame.
    /// The slot at [FrameIndex] is fully rewritten every frame, while the slot at [FrameIndex - 1]
    /// still holds the previous frame's per-instance world transforms and morph weights,
    /// so the prev source at VS buffer(9) is a handle swap rather than a copy.
    /// Two slots are not enough: Device allows frameCount frames in flight, and frame k would overwrite the slot frame k-2
    /// is still reading. When capacity changes, every slot is rebuilt and cleared together,
    /// where r3.w == 0 acts as the "no history" sentinel.
    /// </summary>
    protected IMTLBuffer[] _instanceBuffers = Array.Empty<IMTLBuffer>();

    /// <summary>Ring slot this frame writes and reads.</summary>
    protected static int InstanceFrameSlot => (int)(Device.FrameIndex % Device.frameCount);

    /// <summary>The previous-instance stream for the current frame: the slot the previous frame wrote.
    /// If no history exists yet, it falls back to the write side so the shader uses current values through the r3.w == 0 sentinel.</summary>
    protected IMTLBuffer PrevInstanceBuffer
    {
        get
        {
            var prev = _instanceBuffers.Length > 0 ? _instanceBuffers[PrevFrameIndex] : null;
            return prev ?? _instanceBuffer;
        }
    }

    // === A7 per-instance GPU culling ===
    // Corresponds item-by-item to DXInstancedPrimitiveGroup / VKInstancedPrimitiveGroup: the five buffers are rebuilt only when capacity changes, "grow-only".
    // Compared with the other two backends, two things are missing here: Metal has no buffer state machine (read-after-write is honored by the driver's hazard tracking),
    // and it has no usage bits that must be baked in at creation time, so sorted declaring ComputeAndVertexInput is only for the four-backend validation symmetry.

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
    /// <summary>Number of parameter-table entries already uploaded to the GPU. When it differs from <see cref="_cullPrimInfoPending"/>, this frame needs a re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>Parameter-table bytes already constructed on the CPU side: construction can happen at any time, but the upload must wait until a blit encoder can be opened
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>Number of entries in the CPU-side parameter table; -1 means it has never been constructed.</summary>
    int _cullPrimInfoPending = -1;
    /// <summary>A7b: the arg-buffer image composed from <see cref="_cullPrimInfoBytes"/>. It exists for the same reason as that table --
    /// its content only changes with the primitive set, so the per-frame path is one "is it stale" comparison and the per-frame reset
    /// dispatch it replaces is gone.</summary>
    byte[]? _cullArgsTemplate;
    bool _cullRegistered;
    bool _cullDrawReady;
    /// <summary>The owner's effective draw path is <see cref="Season.Controls.InstanceDrawPath.Indirect"/>, latched in Update so the cull dispatch and the draw see the same answer.</summary>
    bool _cullMainView;

    protected MTLInstancedPrimitiveGroup(string name)
    {
        Name = name;
    }

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
    /// drawIndexedPrimitives arguments -- a group drawing a sub-range of a shared index buffer overrides this, otherwise its indirect
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

        if (_instanceCapacity == count && _instanceBuffers.Length == Device.frameCount)
            return;

        // On capacity changes, rebuild and clear every slot together.
        // After clearing, r3.w == 0 makes the shader fall back to the current world,
        // preventing stale indices on the old side from generating fake velocity when instance counts change.
        nuint size = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * count);
        // The old ring may be empty (first build) or shorter (a device whose frameCount shrank), so index it only
        // where it actually reaches.
        var previous = _instanceBuffers;
        var buffers = new IMTLBuffer[Device.frameCount];
        for (int i = 0; i < buffers.Length; i++)
        {
            if ((uint)i < (uint)previous.Length)
                previous[i]?.Dispose();
            buffers[i] = Device.ResourceManager.CreateBuffer(size);
            unsafe
            {
                new Span<byte>((void*)buffers[i].Contents, (int)size).Clear();
            }
        }

        _instanceBuffers = buffers;
        _instanceCapacity = count;
    }

    /// <summary>
    /// Uploads this frame's instance stream into this frame's ring slot and makes <see cref="_instanceBuffer"/> point at it.
    /// Derived Update methods call this at the end, after which <see cref="PrevInstanceBuffer"/> is the slot the previous
    /// frame wrote -- a handle swap, not a second upload.
    /// </summary>
    protected void UploadInstanceStream(InstanceTransformData[] data, int count)
    {
        int slot = InstanceFrameSlot;
        if ((uint)slot >= (uint)_instanceBuffers.Length)
            return;

        _instanceBuffer = _instanceBuffers[slot];
        if (_instanceBuffer == null || count <= 0)
            return;

        Device.ResourceManager.UpdateBuffer(_instanceBuffer, new ReadOnlySpan<InstanceTransformData>(data, 0, count));
    }

    // ============================================================
    // Generic per-instance extras (VS buffer 12)
    // ============================================================

    /// <summary>This frame's extras records, one per compacted instance slot, filled by the derived Update alongside
    /// <see cref="_instanceData"/>.</summary>
    protected InstanceExtras[] _instanceExtras = Array.Empty<InstanceExtras>();
    IMTLBuffer[] _instanceExtrasBuffers = Array.Empty<IMTLBuffer>();
    int _instanceExtrasCapacity;

    /// <summary>
    /// This frame's extras buffer, or null before the first Update. Metal binds per draw rather than through a descriptor
    /// set, so nothing has to be republished when the ring is rebuilt - a stale handle cannot outlive the frame that
    /// recorded it, because <see cref="Pipeline.DrawPrimitive"/> hands the handle to the replay queue.
    /// </summary>
    protected IMTLBuffer? CurrentInstanceExtras
    {
        get
        {
            int slot = InstanceFrameSlot;
            return (uint)slot < (uint)_instanceExtrasBuffers.Length ? _instanceExtrasBuffers[slot] : null;
        }
    }

    protected void EnsureInstanceExtrasCapacity(int count)
    {
        if (count <= 0 || count == _instanceExtrasCapacity)
            return;

        nuint size = (nuint)(Unsafe.SizeOf<InstanceExtras>() * count);
        var previous = _instanceExtrasBuffers;
        var buffers = new IMTLBuffer[Device.frameCount];
        for (int i = 0; i < buffers.Length; i++)
        {
            if ((uint)i < (uint)previous.Length)
                previous[i]?.Dispose();
            buffers[i] = Device.ResourceManager.CreateBuffer(size);
            // White, not zero: a slot no instance claimed this frame must multiply to itself, and a zeroed record would
            // turn every tinted part of it black for one frame instead.
            unsafe
            {
                var records = (InstanceExtras*)buffers[i].Contents;
                for (int r = 0; r < count; r++)
                    records[r] = new InstanceExtras { Color = Vector4.One, Extra = Vector4.Zero };
            }
        }

        _instanceExtrasBuffers = buffers;
        _instanceExtrasCapacity = count;
    }

    /// <summary>Copies this frame's extras records into this frame's ring slot. Not a vertex buffer like the instance
    /// stream: it is only ever read through buffer(12).</summary>
    protected void UploadInstanceExtras(int count)
    {
        var buffer = CurrentInstanceExtras;
        if (buffer == null || count <= 0 || count > _instanceExtras.Length)
            return;

        Device.ResourceManager.UpdateBuffer(buffer, new ReadOnlySpan<InstanceExtras>(_instanceExtras, 0, count));
    }

    /// <summary>
    /// Bones stay gated per primitive on Metal: only a skinned primitive has a palette to read at all, and publishing
    /// the bit for a non-skinned one would send the shader looking for a palette that was never uploaded. DX and
    /// Vulkan publish it for the whole group and let the shader decide, which is why this is the one override.
    /// </summary>
    protected override bool PrevBonesApplies(PrimitiveData primitive) => primitive.MaterialParams.IsSkinned != 0;

    /// <summary>
    /// Main pass for the instance group. Overrides the core entry rather than hiding it, so a
    /// <see cref="Season.Rendering.Drawables.PrimitiveGroupCore{TPrim, TMat, TTex}"/> reference dispatches here.
    /// The bucket walk, the fade decision and the two-pass transparent split live in
    /// <c>PrimitiveGroupCore.DrawInstancedMainPass</c>.
    /// </summary>
    public override void Draw() => DrawInstancedMainPass();

    protected override void OnBeginMainPass()
        => DrawTrace.Group("mtl", "main", Name, _opaquePrimitives.Count);

    protected override void DrawOpaqueInstances(PrimitiveData primitive, int streamIndex, bool fadeInsteadOfOpaque)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;

        // A7 main view: the opaque body reads the culled, compacted main-view segment and takes its instance count from the
        // indirect args. Fade stays direct: a GPU-sized draw cannot be split per instance, and a fading group is about to
        // change visibility anyway.
        if (!fadeInsteadOfOpaque && TryGetMainViewSegment(0, out var sortedBuffer, out nuint segmentOffset, out var argsBuffer))
        {
            Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                sortedBuffer, segmentOffset, 0, 0,
                PrevInstanceBuffer,
                indirectBuffer: argsBuffer,
                indirectOffset: Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, streamIndex),
                instanceExtrasBuffer: CurrentInstanceExtras);
            Season.Rendering.InstanceCull.MainViewDraws++;
            return;
        }

        // Use the Opaque PSO when overall alpha is at least 1, otherwise use Fade.
        Pipeline.SetPipeline(enc, fadeInsteadOfOpaque ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
        Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
            primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
            LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
            primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            _instanceBuffer, 0, (nuint)_instanceCount, 0,
            PrevInstanceBuffer, instanceExtrasBuffer: CurrentInstanceExtras);
    }

    protected override void DrawTransparentInstance(PrimitiveData primitive, int instanceIndex, bool frontFace)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * instanceIndex);

        Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
        enc.SetCullMode(frontFace ? MTLCullMode.Front : MTLCullMode.Back);
        Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
            primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
            LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
            primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            _instanceBuffer, instOffset, 1, 0,
            PrevInstanceBuffer,
            // The back-facing pass has never bound the extras buffer on this path; kept as it was so the encoder traffic stays identical.
            instanceExtrasBuffer: frontFace ? CurrentInstanceExtras : null);
    }

    // Unified highlighting: per-instance bounds boxes plus wireframe shell boxes, drawn after all surfaces.
    protected override void DrawInstanceBoundsBoxes() => DrawBoundsBoxes();

    protected override void DrawInstanceShellBoxes() => DrawShellBoxes(_instanceBuffer, PrevInstanceBuffer);

    /// <summary>
    /// A7: whether the opaque body may draw from the main-view segment right now, and where it is.
    /// Every condition is a reason the segment would be wrong rather than merely slower, so any miss falls back to the direct draw:
    /// the owner did not ask for it; this frame's chain did not run or did not include the main-view slot (its args would still
    /// hold reset's zero instances and the group would vanish); or the view being drawn is not the one the segment was culled for
    /// (mirrors, minimaps).
    /// <paramref name="stream"/> is the gather stream: 0 for a single-stream group, the opaque-primitive index for a per-primitive one.
    /// </summary>
    protected bool TryGetMainViewSegment(int stream, out IMTLBuffer sortedBuffer, out nuint segmentOffset, out IMTLBuffer argsBuffer)
    {
        sortedBuffer = null!;
        segmentOffset = 0;
        argsBuffer = null!;
        if (!_cullMainView || !_cullDrawReady || !Season.Rendering.InstanceCull.MainViewCulled || _cullCapacity < _instanceCount)
            return false;
        if (!ReferenceEquals(RenderView.CurrentOrNull, DeviceServices.BaseApp?.MainView))
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not MTLStorageBuffer sorted || sorted.Buffer == null
            || _cullArgs is not MTLStorageBuffer args || args.Buffer == null)
            return false;

        sortedBuffer = sorted.Buffer;
        segmentOffset = (nuint)Season.Rendering.InstanceCull.MainViewSlot * (nuint)_cullCapacity
            * Season.Rendering.InstanceCull.WorldStride;
        argsBuffer = args.Buffer;
        return true;
    }

    /// <summary>
    /// A7 shadow: when this frame's culling chain is ready, the slot segment of gather stream <paramref name="stream"/> for the shadow
    /// view being drawn (<see cref="Season.Rendering.CascadedShadow.ActiveSlot"/>) and the args buffer its counts come from. Any miss
    /// falls back to the direct whole-batch shadow draw, never to an empty one.
    /// The segment is selected by the SetVertexBuffer offset, not by baseInstance, matching the main-view segment.
    /// </summary>
    protected bool TryGetShadowSegment(int stream, out IMTLBuffer sortedBuffer, out nuint instanceOffset, out IMTLBuffer argsBuffer, out int slot)
    {
        sortedBuffer = null!;
        instanceOffset = 0;
        argsBuffer = null!;
        slot = Season.Rendering.CascadedShadow.ActiveSlot;
        if (!_cullDrawReady || _cullCapacity < _instanceCount)
            return false;
        // Not a range test against MaxSlots: the far slot and the local cube slots sit inside that range, but the chain never writes their
        // arg rows, so accepting one here would submit an empty batch where the whole-batch draw below would have been correct.
        if (!Season.Rendering.InstanceCull.ShadowSlotActive(slot))
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not MTLStorageBuffer sorted || sorted.Buffer == null
            || _cullArgs is not MTLStorageBuffer args || args.Buffer == null)
            return false;

        sortedBuffer = sorted.Buffer;
        instanceOffset = (nuint)slot * (nuint)_cullCapacity * Season.Rendering.InstanceCull.WorldStride;
        argsBuffer = args.Buffer;
        return true;
    }

    /// <summary>
    /// Instanced shadow rendering for 1-5, drawing all instances in one draw call.
    /// Only opaque buckets are rendered because true BLEND materials do not cast shadows under contract clause 7.
    /// The shadow PSO is already bound by RenderShadowPass, so this path neither switches PSOs nor sorts.
    /// The loop and the shared-material accounting live in <c>PrimitiveGroupCore.DrawInstancedShadowPass</c>.
    /// </summary>
    public override void DrawShadow() => DrawInstancedShadowPass();

    protected override void OnBeginShadowPass()
        => DrawTrace.Group("mtl", "shadow", Name, _opaquePrimitives.Count);

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginInstancePrimitive(PrimitiveData primitive) => true;

    protected override void DrawShadowInstance(PrimitiveData primitive, int streamIndex, bool bindMaterial)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;

        // A7: when the whole group takes the indirect path, index count / instance count / baseVertex all come from the GPU-side parameter buffer,
        // and the instance stream is replaced by the reorder block produced by gather -- segmented by slot, so the offset is set on SetVertexBuffer.
        if (TryGetShadowSegment(0, out var sortedBuffer, out nuint instanceOffset, out var argsBuffer, out int slot))
        {
            Pipeline.DrawShadowPrimitiveIndirect(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                sortedBuffer, instanceOffset, argsBuffer,
                Season.Rendering.InstanceCull.ArgsOffset(slot, streamIndex),
                bindMaterial: bindMaterial);
            return;
        }

        Pipeline.DrawShadowPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
            primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
            primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            (nuint)primitive.Indices.Length, primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
            _instanceBuffer, 0, (nuint)_instanceCount, 0,
            bindMaterial: bindMaterial);
    }

    /// <summary>
    /// Phase 4 Outline2D mask rendering.
    /// This is the default implementation for the instanced base class and is reused directly by MTLInstancedMesh3D,
    /// which uses the base-class _instanceBuffer.
    /// Host activation uses the full mask; otherwise rendering is per instance through _outline2DInstances.
    /// Only opaque buckets are drawn.
    /// The mask PSO and DSS are routed by SetPipeline from ActivePassId, mirroring VKInstancedPrimitiveGroup.DrawOutlineMask.
    /// </summary>
    public override void DrawOutlineMask() => DrawInstancedOutlineMask();

    protected override void OnBeginOutlineMaskPass()
        => Pipeline.SetOutlineMaskColor(Device.GraphicsEncoder, _outline2DColor);

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginOutlineMaskPrimitive(PrimitiveData primitive)
    {
        var enc = Device.GraphicsEncoder;
        Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
        enc.SetDepthStencilState(Pipeline.OutlineMaskDepthState);
        return true;
    }

    protected override void DrawOutlineMaskBatch(PrimitiveData primitive)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
            primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
            LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
            primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            _instanceBuffer, 0, (nuint)_instanceCount, 0,
            PrevInstanceBuffer);
    }

    protected override void DrawOutlineMaskInstance(PrimitiveData primitive, int instanceIndex, int slot)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        // The per-slot mask fetches this instance's own OutlineColor.
        Pipeline.SetOutlineMaskColor(enc, _outline2DInstanceColors[slot]);
        nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * instanceIndex);
        Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
            primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
            LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
            primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            _instanceBuffer, instOffset, 1, 0,
            PrevInstanceBuffer, instanceExtrasBuffer: CurrentInstanceExtras);
    }

    /// <summary>Returns the owned primitive collection for base-class operations such as SyncAlpha and ReplaceTextureBySlot.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    // ============================================================
    // A7 per-instance GPU culling
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

    /// <summary>
    /// Derived classes call this after uploading the instance stream, to prepare resources only. The matrix stream upload is deferred to the Record phase: during a control's Update stage
    /// the render encoder may still be open, in which case CreateBlitCommandEncoder fails outright, the upload is discarded, and only one log line remains.
    /// </summary>
    protected void UpdateCullUpload(Season.Controls.InstancedMesh3DBase owner)
    {
        _cullDrawReady = false;
        _cullMainView = owner.EffectiveDrawPath == Season.Controls.InstanceDrawPath.Indirect;
        if (_instanceCount <= 0)
            return;

        EnsureCullResources(_instanceCount);
    }

    /// <summary>
    /// Prepare cull resources according to the instance count. Capacity is aligned to 64, so a small jitter in instance count does not rebuild the five buffers every frame;
    /// but the capacity reported through the interface must be the true capacity, otherwise the index table would be addressed by this frame's instance count and become misaligned with the kernel's writes.
    /// A per-primitive-stream group also rebuilds when its opaque-primitive count changes, since each of those owns a stream pair; a
    /// group with more primitives than <see cref="Season.Rendering.InstanceCull.MaxPrimitives"/>, or with no opaque primitive at all,
    /// cannot join and releases instead, so it keeps the direct path rather than registering buffers nothing will read.
    /// </summary>
    void EnsureCullResources(int count)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // An instance count dropping to 0 does not mean release: hiding and re-showing would otherwise rebuild the five buffers every frame.
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
                for (int s = 0; s < streams; s++)
                {
                    sorted[s] = g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(capacity),
                        Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
                    if (perPrimitive)
                        streamSources[s] = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                }
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                // A7b: ArgsTotalBytes, not ArgsBytes -- the per-slot visible-count accumulator lives in the tail of the same block, which
                // is what lets the per-frame reset dispatch go away without a second buffer or a second upload path behind it.
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
    /// Construct the parameter-table bytes only, without touching the GPU -- the safe timing for construction and upload differs: construction can happen at any time,
    /// while the upload must wait until a blit encoder can be opened (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).
    /// If the primitive set is unchanged, do not rebuild. The index range and the draw side must share the same source: the arg image
    /// carries this range into the indirect parameters, and the draw side no longer supplies it itself -- hence <see cref="GetCullPrimitiveRange"/>.
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullArgs == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // Explicitly record "there is no parameter table to upload": otherwise PrepareCullInputs would repeatedly upload the previous bytes,
            // whose count looks matching but whose contents are the stale copy.
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
        // A7b: the arg image is composed here too -- a pure function of this table plus the fixed slot layout, i.e. exactly the bytes
        // the reset kernel used to write every frame, now built once when its input changes.
        _cullArgsTemplate = Season.Rendering.InstanceCull.BuildArgsTemplate(bytes, n);
    }

    /// <summary>
    /// A7: upload the cull CPU-side inputs to the GPU. Called back by InstanceCullEffect before each dispatch,
    /// that phase is outside all Passes -- Metal's compute and render encoders are mutually exclusive,
    /// and opening a blit encoder while a render encoder is open is bound to fail, so this is this backend's only reliable upload timing.
    /// The input is bit-for-bit identical to the IA instance stream (both are InstanceTransformData, stride 80 matching WorldStride),
    /// so the same array is reused directly without storing an extra copy. A per-primitive-stream group additionally uploads each
    /// primitive's own stream as that stream's gather source (see <see cref="CullPerPrimitiveStreams"/>).
    /// A7b: what the arg slot no longer carries is the reset kernel's per-frame output -- the template upload above only happens when the
    /// primitive set changes, which is the entire dispatch saving.
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        if (!Season.Rendering.InstanceCull.Enabled || _instanceCount <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // The parameter table follows the geometry rather than the frame, so it is re-uploaded only when the entry count changes.
        // A7b: what it carries is frame-invariant (the index ranges) or re-derived by the GPU later in the same frame (the instance
        // counts, published by gather), which is exactly why this once-per-primitive-set upload can stand in for a per-frame reset dispatch.
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullArgsTemplate == null || _cullArgs == null)
                return false;
            if (!g.TryUpdateStorageBuffer(_cullArgs, _cullArgsTemplate))
                return false;
            _cullPrimitiveCount = _cullPrimInfoPending;
        }

        // The matrix stream changes every frame, so only the valid segment is uploaded, with no slack left.
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
    /// A7: hand over the reorder stream written by gather and the parameter buffer to a draw-readable state. Must run outside a Pass,
    /// so it is called back by InstanceCullEffect after the three dispatches, rather than waiting until DrawShadow.
    /// On the Metal backend there is nothing to record: read-after-write dependencies are honored by the driver's hazard tracking,
    /// so here it only verifies the handles and runs the unified admission check once; the "handover" is the "admission".
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullSorted.Length == 0)
            return false;
        foreach (var s in _cullSorted)
        {
            if (s is not MTLStorageBuffer sorted || sorted.Buffer == null)
                return false;
        }
        if (_cullArgs is not MTLStorageBuffer args || args.Buffer == null)
            return false;
        if (_cullCapacity <= 0)
            return false;

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

    /// <summary>
    /// Releases the instance buffers and clears all primitive collections.
    /// Derived classes should clean up their own GPU resources before calling base.Dispose().
    /// </summary>
    public override void Dispose()
    {
        ReleaseCullResources();
        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();
        _transparentInstanceOrder.Clear();

        // For 2-3, both sides of the double-buffered instance stream are owned by this class,
        // and _instanceBuffer is only an alias of the current write side.
        for (int i = 0; i < _instanceBuffers.Length; i++)
        {
            _instanceBuffers[i]?.Dispose();
            _instanceBuffers[i] = null!;
        }

        _instanceBuffer = null!;

        for (int i = 0; i < _instanceExtrasBuffers.Length; i++)
        {
            _instanceExtrasBuffers[i]?.Dispose();
            _instanceExtrasBuffers[i] = null!;
        }

        _instanceExtrasBuffers = Array.Empty<IMTLBuffer>();
        _instanceExtrasCapacity = 0;
    }
}
