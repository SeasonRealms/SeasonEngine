// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// A7: the resource surface an instanced group exposes when handed to GPU culling. The implementation is
/// each backend's instanced primitive group, since only it knows the instance world matrices, each surface's index range and the current frame's instance count.
///
/// The heart of the contract is "original-index indirection": the cull kernel reads the instance world matrices, and each passing instance writes its **original index** into
/// <see cref="CullVisible"/>; the draw side uses the compressed <c>SV_InstanceID</c> to restore the original index through this table and then fetch
/// the transform. So the instance data itself is never rebuilt and never reordered; each frame only adds a 4-byte index table and a few indirect args.
/// "Original" here means original to the cull input stream, not to the control's <c>Instances</c> list: the stream holds the enabled instances only, in
/// write order, so the tag is exactly the compacted slot every per-instance table (world, bone palette, morph weights, their previous-frame sides) is
/// laid out by, and never the index of an instance in <c>Instances</c>. The two coincide only while every instance is enabled.
/// This is exactly the approach 1-5 deferred in <see cref="Season.Controls.InstancedMesh3DBase"/>: the comment there states
/// "per-instance culling would require rebuilding and uploading the instance buffer for every cascade, violating clause 2 of 1-3", while the index table sidesteps the rebuild.
///
/// All buffers are tiled across <see cref="InstanceCull.MaxSlots"/> slots. The shadow slots keep the layout of
/// <see cref="CascadedShadow"/>: 0..2 are the sun cascades and 3 is the spot light; <see cref="InstanceCull.MainViewSlot"/> follows them
/// and holds the main camera's frustum for the groups that opted into indirect main-view drawing (<see cref="CullMainView"/>).
/// Inactive slots are skipped directly by slotMask,
/// rather than by filling in a "matrix that culls everything" -- that would require constructing a degenerate projection that silently breaks the moment the plane-extraction code changes.
/// </summary>
public interface IInstanceCullTarget
{
    /// <summary>Number of instances participating in culling this frame. 0 means this group is not drawn this frame and the kernel is not dispatched.</summary>
    int CullInstanceCount { get; }

    /// <summary>
    /// Per-instance capacity of the index table and the reordered stream; must be no less than <see cref="CullInstanceCount"/>.
    /// It is kept separate from the instance count for a reason: backend buffers follow a "grow-only" capacity policy (consistent with the instance buffer),
    /// and the cull kernel indexes by capacity while gather also addresses by capacity. If this fell back to the current frame's instance count, then once capacity holds
    /// any extra room the index table would read the wrong locations, showing up as drawing a batch of instances that should not appear.
    /// </summary>
    int CullCapacity { get; }

    /// <summary>Primitive count of this group, i.e. how many indirect arg entries to write per slot. If it exceeds <see cref="InstanceCull.MaxPrimitives"/>, this group falls back to CPU culling.</summary>
    int CullPrimitiveCount { get; }

    /// <summary>The raw world-matrix stream of 80 bytes per instance, bit-identical to the IA instance buffer layout (four-row matrix + quaternion blend weights). This is the cull input and is never rewritten itself.</summary>
    StorageBuffer? CullSourceWorlds { get; }

    /// <summary>
    /// Number of instance streams the gather kernel materializes from the one index table. A group whose primitives all read the same
    /// instance stream (InstancedMesh3D) has one; a glTF InstancedModel has one per drawn primitive, because each primitive's stream is
    /// "its node's world × the instance world" plus its own morph weights, so no single reordered stream could serve two of them.
    /// The visibility decision is still made once per instance from <see cref="CullSourceWorlds"/>; every stream only replays it.
    /// </summary>
    int CullStreamCount { get; }

    /// <summary>The 80-byte-per-instance stream gather reads for stream <paramref name="stream"/>, holding the instances in the same order as
    /// <see cref="CullSourceWorlds"/>. A single-stream group returns <see cref="CullSourceWorlds"/> itself.</summary>
    StorageBuffer? CullStreamSource(int stream);

    /// <summary>
    /// The reordered stream of 80 bytes per instance for stream <paramref name="stream"/>, tiled across <see cref="InstanceCull.MaxSlots"/> slots in capacity-sized segments.
    /// The gather kernel materializes visible instances here according to the index table; the draw side binds it as the instance stream and feeds the instance count via indirect args.
    /// The reason to materialize rather than have the vertex stage index the table itself: the D3D12 root signature has already used 61/64 DWORDs, and attaching two more SRV tables
    /// would exhaust the binding budget for every subsequent Phase, while IA's instance stepping cannot be indexed by original index.
    /// </summary>
    StorageBuffer? CullStreamSorted(int stream);

    /// <summary>
    /// <see cref="InstanceCull.MaxSlots"/> × <see cref="InstanceCull.MaxPrimitives"/> indirect draw args, written by the GPU.
    /// A7b: the block also carries <see cref="InstanceCull.SlotCountsBytes"/> of per-slot visible-count accumulator after the table
    /// (<see cref="InstanceCull.SlotCountsOffset"/>), which is what replaced the per-frame reset dispatch.
    /// </summary>
    StorageBuffer? CullArgs { get; }

    /// <summary><see cref="InstanceCull.MaxSlots"/> × capacity uints; one segment of the original index table per slot, written by the GPU.</summary>
    StorageBuffer? CullVisible { get; }

    /// <summary>Group-local bounding box center. A multi-surface group must pass the merged box, otherwise instances of one surface would be culled incorrectly.</summary>
    Vector3 CullLocalCenter { get; }

    /// <summary>Group-local bounding box half-extents.</summary>
    Vector3 CullLocalExtents { get; }

    /// <summary>
    /// Whether this group draws its opaque main-view body from the <see cref="InstanceCull.MainViewSlot"/> segment this frame.
    /// Only then is the main-view slot added to this target's slot mask: a group that keeps the direct path pays nothing for the extra slot,
    /// and a group whose shadows are reused this frame still gets its main-view segment built.
    /// Latched by the implementer during Update from the owner's effective draw path, so it is stable for the whole frame.
    /// </summary>
    bool CullMainView { get; }

    /// <summary>
    /// Called before each dispatch so the implementer can upload the CPU-side cull inputs (per-instance matrix stream and primitive arg table) to the GPU.
    ///
    /// The only reason this callback exists is "phase": a GPU upload requires the command list to be open and not inside a Pass,
    /// and the control's Update satisfies neither condition. The Windows main loop runs App.Update before Device.BeforeRender,
    /// so during Update the command list is still in the state left after the previous frame closed -- DX12 throws SEH when recording a barrier there,
    /// while Vulkan silently drops it via the IsRecording check (the buffer stays all zero, the whole instance group is culled incorrectly, and the image merely "loses a batch of things").
    /// The two backends do not even fail the same way, which shows the upload timing should not be decided by the control itself but by "whether the command list can record right now".
    /// This callback is invoked by <see cref="InstanceCullEffect"/> inside Record, landing right after BeforeRender and
    /// before the shadow Pass, the one phase all four backends commonly satisfy.
    ///
    /// Returning false means this frame's inputs are unavailable and the caller must skip this target; the draw side falls back to whole-batch CPU culling on its own.
    /// </summary>
    bool PrepareCullInputs();

    /// <summary>
    /// Called after the dispatches so the implementer can transition the reordered stream and indirect args from compute-write state to draw-readable state,
    /// returning whether the indirect path can be taken directly this frame.
    ///
    /// It must be done inside Record and cannot be deferred into DrawShadow: by then we are already inside the shadow Pass,
    /// and PassDesc forbids switching resource states inside a Pass (a Vulkan render pass instance cannot have a barrier inserted into it).
    /// This and <see cref="IGraphics.TryPrepareIndirectArgs"/> are two landing points of the same constraint.
    /// </summary>
    bool PrepareCullDraw();
}

/// <summary>
/// A7: cross-backend constants and registry for per-instance GPU culling. Every number here is a contract that the four backends' shaders and the C# allocations both depend on;
/// changing any one of them requires updating all four kernel sources in sync, otherwise it shows up as "culled results out of place" rather than a compile error.
///
/// A7b/A7c maintenance contract. What follows are invariants rather than choices, and every one of them fails silently -- instances drawn at the wrong
/// position, or culling quietly dropping to the CPU -- instead of raising an error, so none of them is discovered by a compile or a test run:
///
/// (1) Arg layout. <see cref="ArgsTotalBytes"/> is both what a target must allocate and what <see cref="IInstanceCullTarget.CullArgs"/> is checked against.
/// The accumulator offset reaches the kernels as a parameter in all four dialects (cullParams[13] / finalizeParams[1] / gatherParams[4]) and must never be
/// hardcoded there: the accumulator would then land inside the arg table and the atomic add would corrupt the indirect draw arguments themselves.
/// A backend still allocating <see cref="ArgsBytes"/> fails safe -- the guard in <see cref="InstanceCullEffect"/> rejects that target for the frame and it keeps
/// the direct path -- whereas a mismatched offset inside a kernel does not fail at all.
///
/// (2) Accumulator clearing. Only gather clears it, and only on <c>k == 0</c> with <c>clearSlotCounts != 0</c>. That rests on three facts that currently hold:
/// <see cref="GatherGroupsY"/> dispatches a row for every slot cull can write; the last stream of a group is the one that sets the clear flag; and a zero-group
/// indirect dispatch implies every count is already zero, because finalize derives the group count from the maximum over those same rows. Narrowing the Y
/// dimension per target, or making one stream's dispatch conditional, leaves a count above zero across frames -- the next frame's atomic add starts where the
/// last one ended and the draw reads a reordered segment that was never written. Both A/B paths break together, so a pixel comparison cannot catch it.
///
/// (3) Dispatch economy. Removing reset turned one dispatch per target per frame into a one-off upload (<see cref="BuildArgsTemplate"/>). The saving is CPU
/// record/submit time, not GPU work: reset was a single thread group writing this block, and the gather it precedes dwarfs it in ALU terms. A future change
/// argued on "the GPU cost of a dispatch" is arguing from the wrong baseline.
///
/// (4) Why the cull buffers are single-copy under frames-in-flight. <see cref="IInstanceCullTarget.CullSourceWorlds"/>, the arg template and the slot matrices
/// are CPU-authored every frame, yet one copy of each is enough -- unlike the instance/bone/history streams, which had to be ringed. The difference is the
/// delivery path: those streams are written straight into host-visible memory the GPU reads, so frame k+1's CPU write can land while frame k is still reading;
/// these go through <c>TryUpdateStorageBuffer</c>, i.e. a per-frame staging slot plus a buffered copy, and buffered copies execute in submission order. The GPU
/// producers (<see cref="IInstanceCullTarget.CullStreamSorted"/>, <see cref="IInstanceCullTarget.CullVisible"/>, the arg table and the indirect dispatch block) are
/// ordered for the same reason: frame k+1's cull and gather cannot start before frame k's draws have finished. Nothing here needs N copies, and adding them
/// would multiply the largest allocation in the chain (<see cref="SortedBytes"/>, 80 bytes per instance per slot) for no behavioral change.
/// The invariant this rests on is therefore "every CPU-side cull input reaches the GPU as a recorded copy, never as a direct write". Turning one of them into a
/// mapped host-visible write would silently reintroduce exactly the frame k / frame k-2 overlap this note exists to rule out, and it would not fail on any
/// single backend uniformly.
/// </summary>
public static class InstanceCull
{
    /// <summary>
    /// Slot of the main camera's frustum, placed right after the shadow atlas partitions so the shadow slot numbers stay identical to
    /// <see cref="CascadedShadow"/>'s and every shadow-side caller keeps indexing by <see cref="CascadedShadow.ActiveSlot"/> unchanged.
    /// Its reordered stream carries the original-index tag like every other slot's (see the gather kernel and <see cref="InstanceCull.TagMask"/>).
    /// </summary>
    public const int MainViewSlot = CascadedShadow.SlotCount;

    /// <summary>Slot count: the shadow system's atlas partitions (three cascades + one spot light) plus the main view.
    /// It multiplies every per-slot buffer size below, so the backends that allocate through these helpers grow with it automatically.</summary>
    public const int MaxSlots = CascadedShadow.SlotCount + 1;

    /// <summary>
    /// Per-group primitive limit. The indirect arg table is addressed at a fixed stride via <c>slot * MaxPrimitives + primitive</c>;
    /// the fixed rather than packed layout is deliberate: neither the kernel nor the draw side has to read a stride that "may change per frame",
    /// one fewer drift point. 16 covers every surface combination of the current models; groups exceeding it simply do not participate in A7.
    /// </summary>
    public const int MaxPrimitives = 16;

    /// <summary>
    /// Slots whose reordered records get the original instance index written into M14 by gather: all of them.
    /// The main view needs it for motion vectors (t9/t10) and per-instance palettes; the shadow slots need it as soon as a skinned group draws
    /// from them, because its bone palette is laid out by that same index (the compacted slot, see the interface note) and a compacted SV_InstanceID would read another instance's pose.
    /// Tagging a slot that does not need it is free of visible effect: every vertex stage, the shadow variant included, decodes the tag and
    /// restores M14 to 0 before using the matrix, so a tagged record yields bit for bit the world matrix of an untagged one.
    /// </summary>
    public const uint TagMask = (1u << MaxSlots) - 1u;

    /// <summary>Per-instance world-matrix stride, bit-consistent with DX's InstanceTransformData and each backend's IA instance stream.</summary>
    public const uint WorldStride = 80;

    /// <summary>Per-primitive descriptor stride: indexCount / startIndex / baseVertex / reserved.</summary>
    public const uint PrimitiveInfoStride = 16;

    /// <summary>Byte size of one indirect indexed draw arg, same source as <see cref="IndirectDrawIndexedArgs"/>.</summary>
    public const uint ArgsStride = IndirectDrawIndexedArgs.SizeInBytes;

    /// <summary>Shared workgroup size for the cull and fan-out kernels. All four backends use it as <c>numthreads</c> / <c>local_size</c>.</summary>
    public const uint CullGroupSize = 64;

    /// <summary>Byte size of each slot matrix. Four float4 rows, matching System.Numerics' row order.</summary>
    public const uint SlotMatrixStride = 64;

    /// <summary>Total byte size of the indirect arg table: slots × primitives × 20.</summary>
    public const uint ArgsBytes = (uint)(MaxSlots * MaxPrimitives) * ArgsStride;

    /// <summary>
    /// A7b: byte offset of the per-slot visible-count accumulator, placed right after the arg table so it shares the arg buffer.
    ///
    /// Why the counts live apart from the arg table's own InstanceCount: cull accumulates with an atomic add, so the destination has
    /// to already be zero when it starts, and a value that a draw reads cannot be zeroed while the same frame still needs it. Storing
    /// the accumulator in the same block keeps that separation free of a second buffer, a second upload path and a second "did the
    /// allocation succeed" branch in all four backends; the draw side never looks here, so the two roles cannot collide.
    ///
    /// Its only writer is cull and its only reset is gather, at the very end of a run; the conditions that has to stay true are listed on the class.
    /// The least obvious of them is that gather must be dispatched for every slot cull can touch, or a leftover count survives into the next frame.
    /// </summary>
    public const uint SlotCountsOffset = ArgsBytes;

    /// <summary>
    /// A7b: bytes reserved for the accumulator, rounded up to 16 like every other storage-buffer size in this file, so the four
    /// backends' alignment rules all hold without a special case.
    /// </summary>
    public const uint SlotCountsBytes = ((uint)MaxSlots * 4u + 15u) & ~15u;

    /// <summary>
    /// A7b: the size <see cref="IInstanceCullTarget.CullArgs"/> must have. Everything the cull chain touches is inside this one block,
    /// so "the arg buffer is too small" is a single check rather than one per region.
    /// </summary>
    public const uint ArgsTotalBytes = SlotCountsOffset + SlotCountsBytes;

    /// <summary>Total byte size of the reordered instance stream: slots × capacity × 80. Capacity is grow-only, same policy as the instance buffer.</summary>
    public static uint SortedBytes(int capacity) => (uint)(MaxSlots * capacity) * WorldStride;

    /// <summary>
    /// Upper bound on the gather kernel's group count along X; the X dimension is the candidate index within a slot. Computed from capacity rather than instance count:
    /// the index table is compressed, so the CPU has no way to know how many are visible, only the GPU does.
    /// On the indirect dispatch path this dimension is instead written by the finalize kernel as "the group count corresponding to the largest per-slot visible count"; capacity is only an upper bound here.
    /// </summary>
    public static uint GatherGroupsX(int capacity) => (uint)((long)capacity + CullGroupSize - 1) / CullGroupSize;

    /// <summary>Gather kernel's group count along Y, one row per slot. Both dispatch paths use this value: the A/B comparison is only allowed to vary the X dimension.
    /// It is also a correctness constant, not merely a dispatch shape: the accumulator is cleared from inside these rows, so a slot with no row keeps its
    /// count across frames (see the accumulator notes on <see cref="SlotCountsOffset"/> and the class-level A7b contract).</summary>
    public const uint GatherGroupsY = MaxSlots;

    /// <summary>Byte size of the slot matrix buffer.</summary>
    public const uint SlotMatrixBytes = (uint)MaxSlots * SlotMatrixStride;

    /// <summary>
    /// P2: safety band applied to the radius in <see cref="CpuVisible"/>. The GPU kernel already culls a sphere circumscribing the
    /// world-space AABB; the CPU test is widened by this factor on top of that, so it can only ever be more permissive than the kernel.
    ///
    /// Direction matters: a CPU cull that skipped an instance's pose evaluation while the kernel still drew it would freeze a visible
    /// pose in place, which is a defect; a CPU cull that keeps a pose the kernel drops only costs the evaluation it was meant to save.
    /// The band also absorbs the frame between "this pose was skipped" and "the camera moved", where the two tests are not evaluated
    /// against the same frustum.
    /// </summary>
    public const float CpuCullGuardScale = 1.25f;

    /// <summary>
    /// Whether the backend capabilities are in place. Set by a successful <see cref="InstanceCullEffect"/> initialization, cleared back to false on unload or failure;
    /// each backend's group only allocates cull resources at that point, so "a backend without kernels" shows up as falling back to CPU culling rather than drawing from empty buffers.
    /// </summary>
    public static bool Enabled { get; internal set; }

    /// <summary>Cull targets registered for this frame. Added by each backend's group on creation and removed by reference on disposal.</summary>
    public static readonly List<IInstanceCullTarget> Targets = new();

    /// <summary>
    /// Whether this frame's dispatches included <see cref="MainViewSlot"/> for the targets that asked for it. Written by
    /// <see cref="InstanceCullEffect"/> before it dispatches anything, and read by the draw side next to its own "draw ready" flag:
    /// that flag only says the chain ran, and a chain run for the shadow slots alone still resets the main-view args to zero instances,
    /// so drawing from the segment without this check would make the whole group vanish for the frame (for instance when the main
    /// view has not been prepared yet). A stale value from an earlier frame is harmless: the draw-ready flag is cleared every Update
    /// and only set by a dispatch that rewrote this one first.
    /// </summary>
    public static bool MainViewCulled { get; internal set; }

    /// <summary>
    /// Number of indirect-dispatch gathers within the process. For diagnostics; the two numbers together are the only evidence of "which path this A/B run actually took":
    /// comparing two screenshots is not enough -- when both paths are correct they must be identical, but "gather was never dispatched at all" would also make them identical,
    /// and in that case the comparison proves nothing.
    ///
    /// The increment happens in <see cref="InstanceCullEffect"/>'s DispatchTarget, at most once per target per frame however many streams the target
    /// gathers, so its ratio to the frame count is the number of targets actually participating in culling.
    /// </summary>
    public static long IndirectGatherDispatches;

    /// <summary>Number of fixed-group-count gathers within the process; mutually exclusive and exhaustive with <see cref="IndirectGatherDispatches"/>.</summary>
    public static long FixedGatherDispatches;

    /// <summary>
    /// Number of opaque main-view draws within the process that actually took their instances from the <see cref="MainViewSlot"/>
    /// segment. Diagnostics only, for the same reason as the gather counters: <see cref="MainViewCulled"/> only proves the slot was culled,
    /// and a draw side that silently fell back to the direct stream would make the main-view A/B identical while proving nothing.
    /// Incremented by each backend once per primitive draw it issues from the segment.
    /// </summary>
    public static long MainViewDraws;

    /// <summary>Byte offset of the (slot, primitive) entry in the indirect arg table.</summary>
    public static uint ArgsOffset(int slot, int primitive)
        => (uint)(slot * MaxPrimitives + primitive) * ArgsStride;

    /// <summary>uint index of (slot, k-th visible instance) in the original index table.</summary>
    public static uint VisibleIndex(int slot, uint k, int capacity)
        => (uint)(slot * capacity) + k;

    /// <summary>
    /// P2: CPU mirror of the cull kernel's sphere test, for callers that decide something *before* the dispatch and have to reach the
    /// same verdict the kernel will. <paramref name="localCenter"/> / <paramref name="localExtents"/> are the instance's model-space box
    /// (the same pair a target hands the kernel as CullLocalCenter/CullLocalExtents), and <paramref name="instanceWorld"/> is the root
    /// instance world the kernel reads back out of the 80-byte instance stream.
    ///
    /// The math is the kernel's line for line: rows 0..2 of the world matrix weight the box center, the per-axis absolute rows weighted
    /// by the box extents give the world AABB's half-diagonal, and that length is the sphere radius. The plane test is the same six-plane
    /// test, done through <see cref="Frustum.IntersectsSphere"/> because the frustum planes are the kernel's c3±c0 / c3±c1 / c3±c2 in
    /// normalized form -- with one difference: the radius is widened by <see cref="CpuCullGuardScale"/>.
    ///
    /// The frustum must be the one built from the unjittered ViewProjection (<see cref="RenderView.Frustum"/>), the same matrix
    /// <see cref="InstanceCullEffect"/> writes into the main-view slot. A default-constructed frustum answers true for every sphere, so
    /// "no camera prepared yet" degrades to no culling rather than to culling everything.
    /// </summary>
    public static bool CpuVisible(in Vector3 localCenter, in Vector3 localExtents,
                                  in Matrix4x4 instanceWorld, in Frustum frustum)
    {
        var r0 = new Vector3(instanceWorld.M11, instanceWorld.M12, instanceWorld.M13);
        var r1 = new Vector3(instanceWorld.M21, instanceWorld.M22, instanceWorld.M23);
        var r2 = new Vector3(instanceWorld.M31, instanceWorld.M32, instanceWorld.M33);
        var r3 = new Vector3(instanceWorld.M41, instanceWorld.M42, instanceWorld.M43);

        var ext = Vector3.Abs(localExtents);
        var center = localCenter.X * r0 + localCenter.Y * r1 + localCenter.Z * r2 + r3;
        var bound = Vector3.Abs(r0) * ext.X + Vector3.Abs(r1) * ext.Y + Vector3.Abs(r2) * ext.Z;
        return frustum.IntersectsSphere(in center, bound.Length() * CpuCullGuardScale);
    }

    /// <summary>
    /// Writes the four fields of one primitive descriptor. It lives in the shared layer because the offsets of these four fields are
    /// a contract <see cref="BuildArgsTemplate"/> and all four draw backends depend on; writing it once per backend would let one of them drift.
    /// The layout is fixed as indexCount / startIndex / baseVertex / reserved, consistent with <see cref="PrimitiveInfoStride"/>.
    /// </summary>
    public static void WritePrimitiveInfo(Span<byte> dst, int offset, uint indexCount, uint startIndex, int baseVertex)
    {
        var span = MemoryMarshal.Cast<byte, uint>(dst.Slice(offset, (int)PrimitiveInfoStride));
        span[0] = indexCount;
        span[1] = startIndex;
        span[2] = (uint)baseVertex;
        span[3] = 0u;
    }

    /// <summary>
    /// A7b: composes the initial content of a target's arg buffer from the CPU-side primitive table: every slot carries the same
    /// per-primitive index range, InstanceCount starts at zero everywhere, and the slot-count accumulator at
    /// <see cref="SlotCountsOffset"/> is zero.
    ///
    /// This replaces the reset kernel that used to run once per target per frame. The reason it can: every field it wrote is either
    /// frame-invariant (the index range, which only changes when the primitive set does) or re-derived by the GPU later in the same
    /// frame (the instance counts, which gather publishes). What is left is one upload at the moment the primitive set changes,
    /// instead of a dispatch on every frame in between.
    ///
    /// The layout is the contract the cull, finalize and gather kernels address, so it lives in the shared layer next to
    /// <see cref="WritePrimitiveInfo"/> rather than once per backend.
    /// </summary>
    public static byte[] BuildArgsTemplate(ReadOnlySpan<byte> primInfo, int primitives)
    {
        var bytes = new byte[ArgsTotalBytes];
        int n = Math.Min(primitives, MaxPrimitives);
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            for (int prim = 0; prim < n; prim++)
            {
                int src = prim * (int)PrimitiveInfoStride;
                int dst = slot * (int)MaxPrimitives * (int)ArgsStride + prim * (int)ArgsStride;
                // indexCount / startIndex / baseVertex come straight from the primitive table; both instance-count
                // fields and the trailing reserved dword stay zero, exactly what the reset kernel used to write.
                primInfo.Slice(src, (int)PrimitiveInfoStride).CopyTo(bytes.AsSpan(dst, (int)PrimitiveInfoStride));
                int at = dst + 4;
                bytes[at] = 0; bytes[at + 1] = 0; bytes[at + 2] = 0; bytes[at + 3] = 0;
                int tail = dst + 16;
                bytes[tail] = 0; bytes[tail + 1] = 0; bytes[tail + 2] = 0; bytes[tail + 3] = 0;
            }
        }
        // The accumulator region is already zero (a fresh array); it is spelled out here so the dependency is visible:
        // cull's atomic add is only correct on top of zeroes.
        bytes.AsSpan((int)SlotCountsOffset, (int)SlotCountsBytes).Clear();
        return bytes;
    }

    /// <summary>Registers a cull target. Re-registration has no side effect, avoiding the same group being added twice along multiple paths.</summary>
    public static void Register(IInstanceCullTarget target)
    {
        if (target == null || Targets.Contains(target)) return;
        Targets.Add(target);
    }

    /// <summary>Removes by reference; silently returns if not registered, since a failed initialization may never have reached registration.</summary>
    public static void Unregister(IInstanceCullTarget target)
    {
        if (target != null) Targets.Remove(target);
    }
}

/// <summary>
/// A7: the shadow culling scheduler. <see cref="ComputePhase.FrameStart"/> and App scope, because dispatch must be outside any Pass
/// (Vulkan forbids dispatch inside a Pass, and Metal's compute and render encoders are mutually exclusive), and the shadow atlas is a single one per frame,
/// so it should not be culled repeatedly per view.
///
/// Each group does two or three dispatches per frame: cull compresses per slot, finalize (indirect gather path only) reduces the visible counts into gather's dispatch size,
/// and gather materializes the reordered stream by index, publishes the slot counts into the indirect args and fans the instance count out to the remaining primitives.
/// All of them must be outside a Pass, exactly like the particle system's A0.1 chain; the only difference is that what is produced here is
/// "one indirect arg set per slot + one original index table" rather than a single apply command.
///
/// A7b: the arg table no longer needs a per-frame reset dispatch. Its frame-invariant fields are uploaded once from
/// <see cref="InstanceCull.BuildArgsTemplate"/> when the primitive set changes, and the per-slot visible count now accumulates in a
/// region of the same buffer that gather clears at the end of every run, so nothing has to rewrite zeroes at the start of one.
/// That is one dispatch fewer per target per frame -- the fixed cost that used to make small groups unprofitable to cull on the GPU.
///
/// finalize is only recorded when gather takes the indirect dispatch path (<see cref="IndirectGather"/>). Fixed dispatch does not need it:
/// the capacity in the CPU's hands is a sufficient upper bound, and any extra threads dispatched just read one uint each and exit early.
/// </summary>
public sealed partial class InstanceCullEffect : ComputeEffect, IDisposable
{
    public override string Name => "instanceCull";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    /// <summary>
    /// Dispatch-mode switch for A7 gather, and the first real consumer of A0.2.
    ///
    /// When on, gather's group count is determined by the GPU: the finalize kernel folds "the maximum per-slot visible count" into a group count written to a
    /// 12-byte <see cref="IndirectDispatchArgs"/> block, and gather consumes it via a primitive such as ExecuteIndirect / CmdDispatchIndirect.
    /// When off, the CPU dispatches a fixed count based on the capacity upper bound.
    ///
    /// The **thread mapping of both paths is exactly identical** (X = index within a slot, Y = slot); the only variable is the group count of the X dimension,
    /// so the two runs' images must match pixel for pixel -- that is the criterion for the A/B verification. Any difference is a bug in the dispatch contract,
    /// not a "side effect of a performance optimization".
    ///
    /// Off by default, enabled via an environment variable, because the reason it exists is verification rather than speedup. The measured benefit depends on the
    /// gap between capacity and visible count: capacity is aligned to 64, while the visible count is often just a fraction of capacity, and what is saved is that batch of
    /// idle threads that "read one uint and exit" -- each is cheap, but their number is multiplied by the slot count.
    /// </summary>
    public static bool IndirectGather { get; set; } =
        Environment.GetEnvironmentVariable("SEASON_CULL_INDIRECT") == "1";

    readonly Vector4[] _slotMatrices = new Vector4[InstanceCull.MaxSlots * 4];
    readonly uint[] _gatherParams = new uint[8];
    readonly uint[] _finalizeParams = new uint[4];
    readonly float[] _cullParams = new float[16];
    readonly ComputeResourceRef[] _cullRefs = new ComputeResourceRef[4];
    readonly ComputeResourceRef[] _finalizeRefs = new ComputeResourceRef[2];
    readonly ComputeResourceRef[] _gatherRefs = new ComputeResourceRef[4];

    IGraphics? _graphics;
    ComputeKernel? _cull, _finalize, _gather;
    StorageBuffer? _slotBuffer;

    /// <summary>
    /// The 12-byte dispatch arg block, written by the finalize kernel and read by gather.
    ///
    /// Shared by all cull targets, because within a frame each group's four dispatches are recorded back to back: only after the GPU has read this block and finished
    /// gather does the next group's finalize overwrite it. Sharing saves "creating a 12-byte buffer per group",
    /// at the cost that if this is ever changed to record multiple groups in parallel it must be split up -- at that point this field has to become per-target.
    /// </summary>
    StorageBuffer? _dispatchArgs;

    bool _routed;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Instance culling is already initialized.");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _cull = g.CreateComputeKernel(CreateCullKernel());
            _finalize = g.CreateComputeKernel(CreateFinalizeKernel());
            _gather = g.CreateComputeKernel(CreateGatherKernel());
            if (_cull == null || _finalize == null || _gather == null) { Dispose(); return false; }
            _slotBuffer = g.CreateStorageBuffer(InstanceCull.SlotMatrixBytes);
            if (_slotBuffer == null || _slotBuffer.SizeInBytes < InstanceCull.SlotMatrixBytes) { Dispose(); return false; }

            // The dispatch arg block is created whenever the backend supports it, independent of the switch: the switch is "which path this frame wants to take", the buffer is "which path can be taken".
            // Decoupling the two lets the A/B switch frame by frame within the same process without a restart -- exactly what pixel-for-pixel comparison needs.
            // If it cannot be created, fall back to fixed dispatch rather than disabling all of A7: culling itself and "how gather is dispatched" are two different things.
            if (g.DispatchIndirectSupported)
            {
                _dispatchArgs = g.CreateStorageBuffer(Season.Rendering.IndirectDispatchArgs.SizeInBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
                if (_dispatchArgs == null
                    || _dispatchArgs.SizeInBytes < Season.Rendering.IndirectDispatchArgs.SizeInBytes)
                {
                    _dispatchArgs = null;
                    DeviceServices.BaseApp.AddLog(LogType.Backend,
                        "[InstanceCull] Failed to create the indirect dispatch arg block; gather falls back to fixed-group dispatch.");
                }
            }

            InstanceCull.Enabled = true;
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[InstanceCull] Initialization failed: {ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics) || _cull == null || _finalize == null
            || _gather == null || _slotBuffer == null) return;
        if (InstanceCull.Targets.Count == 0) return;

        // The slot mask is determined here rather than letting the kernel guess from an "invalid matrix": inactive slots simply do not participate,
        // their arg table keeps the InstanceCount=0 written by reset, and the draw side then naturally draws nothing.
        //
        // On a shadow-map reuse hit the entire shadow Pass is skipped, so the shadow slots have no consumers this frame and stay out of the mask.
        // That used to end Record outright; it no longer may, because the main-view slot is consumed by the scene Pass, which runs every frame
        // whether or not the shadow atlas is reused or shadows are on at all.
        int shadowMask = 0;
        if (!CascadedShadow.ReuseActive)
        {
            int cascades = CascadedShadow.SunActive ? CascadedShadow.ActiveCascadeCount : 0;
            for (int slot = 0; slot < cascades && slot < CascadedShadow.MaxCascades; slot++)
            {
                WriteSlotMatrix(slot, CascadedShadow.CascadeViewProj[slot]);
                shadowMask |= 1 << slot;
            }
            if (CascadedShadow.SpotActive)
            {
                WriteSlotMatrix(CascadedShadow.SpotSlot, CascadedShadow.SpotViewProj);
                shadowMask |= 1 << CascadedShadow.SpotSlot;
            }
        }

        // The main view's unjittered ViewProjection without the backend NDC adjustment -- the same matrix control-level frustum culling uses,
        // so an instance survives here exactly when its group would survive there. It is already current: every backend prepares the main view
        // at the camera update point, before the shadow cascades are derived from it and long before this FrameStart phase.
        // TAA jitter is sub-pixel and the cull sphere is conservative, so the unjittered planes never cut a visible instance.
        bool mainView = false;
        for (int i = 0; i < InstanceCull.Targets.Count && !mainView; i++)
            mainView = InstanceCull.Targets[i].CullMainView;
        var view = mainView ? DeviceServices.BaseApp?.MainView : null;
        if (view == null || !view.Prepared) mainView = false;
        else WriteSlotMatrix(InstanceCull.MainViewSlot, view.ViewProjection);
        InstanceCull.MainViewCulled = mainView;
        int mainBit = 1 << InstanceCull.MainViewSlot;

        int slotMask = shadowMask | (mainView ? mainBit : 0);
        if (slotMask == 0) return;

        // Clear inactive slots: the matrix buffer is reused across frames; a leftover matrix from the previous frame, if read, would make some slot draw ghost instances.
        for (int slot = 0; slot < InstanceCull.MaxSlots; slot++)
            if ((slotMask & (1 << slot)) == 0) Array.Clear(_slotMatrices, slot * 4, 4);

        if (!g.TryUpdateStorageBuffer(_slotBuffer, MemoryMarshal.AsBytes(_slotMatrices.AsSpan()))) return;

        // Each target gets the shadow slots plus the main-view slot only if it asked for it: a direct-path group would otherwise
        // pay a cull test and a gather row per instance for a segment nobody draws.
        for (int i = 0; i < InstanceCull.Targets.Count; i++)
        {
            var target = InstanceCull.Targets[i];
            int mask = shadowMask | (mainView && target.CullMainView ? mainBit : 0);
            if (mask != 0) DispatchTarget(g, target, mask);
        }
    }

    void WriteSlotMatrix(int slot, in Matrix4x4 vp)
    {
        int o = slot * 4;
        _slotMatrices[o + 0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _slotMatrices[o + 1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _slotMatrices[o + 2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _slotMatrices[o + 3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
    }

    void DispatchTarget(IGraphics g, IInstanceCullTarget target, int slotMask)
    {
        int count = target.CullInstanceCount;
        if (count <= 0) return;

        // The upload is placed before reading CullPrimitiveCount, not after the capacity check:
        // on the Web backend that property publishes the count of "arg table uploaded"; if it is read before the upload, on the first run it is still -1,
        // the check exits early on the spot, and the upload therefore never has a chance to happen -- an ordering that deadlocks itself.
        if (!target.PrepareCullInputs()) return;

        int capacity = target.CullCapacity;
        int primitives = target.CullPrimitiveCount;
        if (capacity < count || primitives <= 0 || primitives > InstanceCull.MaxPrimitives) return;

        var source = target.CullSourceWorlds;
        var args = target.CullArgs;
        var visible = target.CullVisible;
        int streams = target.CullStreamCount;
        if (source == null || args == null || visible == null || streams <= 0) return;

        // The capacity checks live in the shared layer so the four backends do not each repeat them: args is fixed-size, and visible and the reordered stream must hold the full capacity.
        // source only needs to hold this frame's instances -- it is uploaded by instance count, with no spare room reserved.
        if (args.SizeInBytes < InstanceCull.ArgsTotalBytes) return;
        if (visible.SizeInBytes < (uint)(InstanceCull.MaxSlots * capacity) * 4u) return;
        if (source.SizeInBytes < (uint)count * InstanceCull.WorldStride) return;
        // Every stream is checked before anything is dispatched: a stream found short after the cull already ran would leave the args
        // saying "N visible" over a reordered segment that was never written, and the draw side would read last frame's records.
        for (int s = 0; s < streams; s++)
        {
            var streamSource = target.CullStreamSource(s);
            var streamSorted = target.CullStreamSorted(s);
            if (streamSource == null || streamSorted == null) return;
            if (streamSource.SizeInBytes < (uint)count * InstanceCull.WorldStride) return;
            if (streamSorted.SizeInBytes < InstanceCull.SortedBytes(capacity)) return;
        }

        // 1) cull: one thread per instance, testing the sphere against the six clipping planes for each slot.
        //    The passing instances atomic-add into the slot's accumulator at InstanceCull.SlotCountsOffset rather than into the
        //    arg table's own InstanceCount: the accumulator belongs to the GPU chain and gather clears it every run, while the
        //    InstanceCount is the value a draw reads, so it must only ever hold a finished frame's result.
        _cullParams[0] = target.CullLocalCenter.X;
        _cullParams[1] = target.CullLocalCenter.Y;
        _cullParams[2] = target.CullLocalCenter.Z;
        _cullParams[3] = 0f;
        _cullParams[4] = MathF.Abs(target.CullLocalExtents.X);
        _cullParams[5] = MathF.Abs(target.CullLocalExtents.Y);
        _cullParams[6] = MathF.Abs(target.CullLocalExtents.Z);
        _cullParams[7] = 0f;
        // [8..12] are all declared as uint in the four backends' shaders, while Params is a 16-byte-aligned raw byte block,
        // so these five entries must be sent as the raw bit patterns: assigning an int directly would convert it to float first, and the shader would read the
        // float bit pattern of 8f / 4f (1090519040 / 1082130432), slotCount would be read as a billion,
        // and the loop with out-of-bounds addressing would drag the whole frame into never finishing.
        _cullParams[8] = BitConverter.UInt32BitsToSingle((uint)count);
        _cullParams[9] = BitConverter.UInt32BitsToSingle((uint)InstanceCull.MaxSlots);
        _cullParams[10] = BitConverter.UInt32BitsToSingle((uint)primitives);
        _cullParams[11] = BitConverter.UInt32BitsToSingle((uint)capacity);
        _cullParams[12] = BitConverter.UInt32BitsToSingle((uint)slotMask);
        // pad0 carries the accumulator's byte offset: the block is a 16-byte-aligned mix of floats and uints, so the offset
        // has to travel as the raw bit pattern like the five counts above.
        _cullParams[13] = BitConverter.UInt32BitsToSingle(InstanceCull.SlotCountsOffset);
        _cullParams[14] = 0f;
        _cullParams[15] = 0f;

        _cullRefs[0] = _slotBuffer!;
        _cullRefs[1] = source;
        _cullRefs[2] = args;
        _cullRefs[3] = visible;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _cull!,
            Params = MemoryMarshal.AsBytes(_cullParams.AsSpan()),
            Resources = _cullRefs,
            GroupsX = (uint)((count + (int)InstanceCull.CullGroupSize - 1) / (int)InstanceCull.CullGroupSize),
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 2) finalize (indirect path only): reduces the per-slot visible counts into "how many groups to dispatch along X".
        //    The size can only come from the GPU -- the index table is compressed, and the CPU only has capacity as an upper bound.
        //    It is the only [numthreads(1,1,1)] kernel among the four backends: the slot count is at most 5, and reading five uints serially
        //    is far cheaper than opening a workgroup to do a reduction and then synchronize, and it avoids writing a threadsync dialect per backend.
        bool indirect = IndirectGather && _dispatchArgs != null;
        if (indirect)
        {
            // The slot count scanned is exactly gather's Y-dimension group count; it takes MaxSlots directly rather than GatherGroupsY:
            // the two are currently equal but semantically different -- the latter is "how many rows to dispatch", the former is "how many rows are in the arg table".
            _finalizeParams[0] = (uint)InstanceCull.MaxSlots;
            // A7b: the second field used to be the arg-table stride and now points at the slot-count accumulator, which is what
            // the kernel actually reads. It is still a parameter rather than a kernel constant for the same reason: that
            // MAX_PRIMITIVES × 20 multiplication is a contract several kernels depend on, and writing it more times adds drift points.
            _finalizeParams[1] = InstanceCull.SlotCountsOffset;
            _finalizeParams[2] = InstanceCull.CullGroupSize;
            _finalizeParams[3] = 0;
            _finalizeRefs[0] = args;
            _finalizeRefs[1] = _dispatchArgs!;
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _finalize!,
                Params = MemoryMarshal.AsBytes(_finalizeParams.AsSpan()),
                Resources = _finalizeRefs,
                GroupsX = 1,
                GroupsY = 1,
                GroupsZ = 1,
            });
        }

        // 3) gather: materializes visible instances into the slot segments by the index table, publishes the slot's visible count into
        // the indirect args and fans it out to the remaining primitives along the way.
        // The X dimension is the index within a slot, the Y dimension is the slot. This two-dimensional mapping is fully shared by both dispatch paths -- otherwise the A/B comparison would
        // not be about "the dispatch mode" but about two different addressing schemes, and any difference could not be attributed.
        _gatherParams[0] = (uint)capacity;
        _gatherParams[1] = (uint)primitives;
        _gatherParams[2] = (uint)InstanceCull.MaxSlots;
        // Tag mask: every slot's records get the original index written into M14 (see the gather kernel and InstanceCull.TagMask).
        _gatherParams[3] = InstanceCull.TagMask;
        // A7b: where the slot-count accumulator lives inside the arg block, and whether this dispatch is the one that clears it.
        // Only the last stream of a group clears: every stream reads the same counts, so clearing on the first would leave the
        // rest publishing zeroes into the arg table.
        _gatherParams[4] = InstanceCull.SlotCountsOffset;
        _gatherParams[6] = 0u;
        _gatherParams[7] = 0u;
        _gatherRefs[0] = visible;
        _gatherRefs[3] = args;
        // The counters sit at the dispatch point rather than at the branch test: if finalize is ever merged into another kernel in the future, the branch would still be there while gather might
        // no longer be dispatched, and only when the numbers correspond one-to-one with the real dispatches do they have diagnostic value.
        // They count targets, not streams, so the per-frame ratio keeps meaning "targets culled" whatever mix of groups is on screen.
        if (indirect) InstanceCull.IndirectGatherDispatches++;
        else InstanceCull.FixedGatherDispatches++;
        // One gather per stream against the same index table, args and dispatch block: the visibility decision is made once, and each stream
        // only replays it. The repeated fan-out is harmless (it reads primitive 0, which no gather writes), and the dispatch block is only read.
        for (int s = 0; s < streams; s++)
        {
            _gatherParams[5] = (uint)(s == streams - 1 ? 1 : 0);
            _gatherRefs[1] = target.CullStreamSource(s)!;
            _gatherRefs[2] = target.CullStreamSorted(s)!;
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _gather!,
                Params = MemoryMarshal.AsBytes(_gatherParams.AsSpan()),
                Resources = _gatherRefs,
                // On the indirect path this dimension is superseded by the arg block; write 0 rather than a capacity that will never be used:
                // during a frame capture "0 groups" is the shortest hint to "go look at the arg block".
                GroupsX = indirect ? 0u : InstanceCull.GatherGroupsX(capacity),
                GroupsY = InstanceCull.GatherGroupsY,
                GroupsZ = 1,
                Arguments = indirect ? _dispatchArgs : null,
            });
        }

        // The handoff goes here rather than at draw time: draw happens inside the shadow Pass, and resource states cannot be switched inside a Pass.
        target.PrepareCullDraw();
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        InstanceCull.Enabled = false;
        if (_routed)
        {
            InstanceCull.Targets.Clear();
            _routed = false;
        }
        _cull?.Dispose(); _cull = null;
        _finalize?.Dispose(); _finalize = null;
        _gather?.Dispose(); _gather = null;
        _slotBuffer?.Dispose(); _slotBuffer = null;
        _dispatchArgs?.Dispose(); _dispatchArgs = null;
        _graphics = null;
    }

    // ── Kernel declarations ──
    // cull:     [0] Params 64B / [1] StorageBufferRead slotMatrices -> t0 / [2] StorageBufferRead source -> t1
    //           [3] StorageBufferReadWrite args -> u0 / [4] StorageBufferReadWrite visible -> u1
    // finalize: [0] Params 16B / [1] StorageBufferRead args -> t0 / [2] StorageBufferReadWrite dispatch -> u0
    // gather:   [0] Params 32B / [1] StorageBufferRead visible -> t0 / [2] StorageBufferRead source -> t1
    //           [3] StorageBufferReadWrite sorted -> u0 / [4] StorageBufferReadWrite args -> u1
    // The old reset kernel is gone: its frame-invariant output is uploaded from InstanceCull.BuildArgsTemplate when the primitive
    // set changes, and the per-slot counter it used to clear is now cleared by gather at the end of every run.
    // 64B is far below the 128B cross-backend limit of Vulkan push constants and WebGPU uniforms.

    static ComputeKernelDesc CreateCullKernel() => new()
    {
        Name = "instanceCullMain",
        Source = new ShaderSourceSet
        {
            Hlsl = CullHlsl, Glsl = CullGlsl, Msl = CullMsl, Wgsl = CullWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 64 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceCull.CullGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateFinalizeKernel() => new()
    {
        Name = "instanceCullFinalize",
        Source = new ShaderSourceSet
        {
            Hlsl = FinalizeHlsl, Glsl = FinalizeGlsl, Msl = FinalizeMsl, Wgsl = FinalizeWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        // One thread. The reduction is over at most MaxSlots(5) uints; opening a whole workgroup just to synchronize five reads costs far more than the reads themselves,
        // to say nothing of writing a threadsync dialect per backend (GroupMemoryBarrierWithGroupSync / barrier /
        // threadgroup_barrier / workgroupBarrier), which would mean four implementations that can drift.
        WorkgroupX = 1,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateGatherKernel() => new()
    {
        Name = "instanceCullGather",
        Source = new ShaderSourceSet
        {
            Hlsl = GatherHlsl, Glsl = GatherGlsl, Msl = GatherMsl, Wgsl = GatherWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 32 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceCull.CullGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };
}
