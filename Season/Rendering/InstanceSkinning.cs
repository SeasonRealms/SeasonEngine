// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>
/// P1: which route computes an instanced model's bone palette each frame.
///
/// <see cref="Cpu"/> is the historical route and stays the default, byte for byte: every instance's clip is replayed on the CPU
/// through the shared animation player, the palette is written into the backend's CPU-mapped ring, and a per-frame upload of
/// N x palette-rows x 64 bytes carries it to the GPU. Nothing about that path changed in P1, which is what makes it a usable
/// reference image for the A/B comparison.
///
/// <see cref="Gpu"/> moves the evaluation itself onto the GPU. The clip is resampled once at load time into flat track tables,
/// the CPU keeps only the clock (clip index + playback time per instance, 16 bytes), and a FrameStart kernel walks the joint
/// hierarchy and writes the palette directly into the storage ring the vertex stage already reads (P0). The per-frame upload
/// drops from megabytes to N x 16 bytes.
///
/// The mode is a per-model property, not a global switch, because whether it is worth anything is a property of the model:
/// a model whose animated nodes are all joints and whose primitives hang off static nodes saves the whole evaluation, while
/// anything else has to stay on the CPU (see <c>InstanceSkinBake</c>'s applicability rules).
/// </summary>
public enum InstanceSkinningMode
{
    /// <summary>CPU evaluation plus the CPU palette ring. The original call path, unchanged.</summary>
    Cpu = 0,

    /// <summary>GPU evaluation from the load-time bake. Falls back to <see cref="Cpu"/> per model when the bake is not applicable
    /// or the backend cannot bind a storage buffer into the vertex stage.</summary>
    Gpu = 1,
}

/// <summary>
/// P1: cross-backend constants and registry for GPU instance skinning. Every number here is a contract the four kernel sources
/// and the C# bake both depend on; changing one without the others does not fail to compile, it silently draws the wrong pose.
///
/// Buffer layout (all offsets in bytes, every block 16-byte aligned so the four dialects can read it as float4/vec4 alike):
///
/// <list type="bullet">
/// <item><b>JointTable</b>, <see cref="JointStride"/> per joint in topological order:
/// [0] paletteIndex, [4] parentOrder (0xFFFFFFFF = none), [8] animatedMask, [12] depth,
/// [16..64) rest translation/rotation/scale as three float4,
/// [64..128) the parent's world matrix when the parent is not a joint (static),
/// [128..192) the inverse bind matrix,
/// [192..256) the inverse mesh (bind-node) world matrix.</item>
/// <item><b>TrackTable</b>, <see cref="TrackStride"/> per (clip, joint) pair, clip-major:
/// [0] frameCount, [4] firstRow (this clip's first row in the flattened track plane, i.e. key index x joint count, so joint j
/// of frame f is at firstRow + f * jointCount + j), [8] sampleRate, [12] channelMask (bit0 T, bit1 R, bit2 S; 0 = not driven).</item>
/// <item><b>Tracks</b>, <see cref="KeyStride"/> per key: translation float4, rotation quaternion float4, scale float4.
/// Keys are already blended by the CPU sampler at load time, so the kernel only has to lerp between two neighbours.</item>
/// <item><b>Params</b>, <see cref="ParamStride"/> per instance: uint clip, float time (already wrapped by the CPU clock), uint flags, uint pad.</item>
/// <item><b>Scratch</b>, <see cref="ScratchStride"/> per (instance, joint): the world matrix the kernel computed for that joint,
/// which is what its children read. It is per instance because two instances can be at different times of the same clip.</item>
/// <item><b>Palette</b>: the same ring the vertex stage reads, addressed as instance * paletteStride + paletteIndex.</item>
/// <item><b>PoseTag</b>, P2 L1: palette row <c>instanceSlot</c> itself - one matrix per instance, index equal to the slot, no stride
/// multiplication - of which only the first two lanes of the fourth <em>row</em> are read (<see cref="PoseTagCurrentOffset"/>,
/// <see cref="PoseTagPreviousOffset"/>). A row number of <see cref="PoseTagUnpooled"/> means the instance is not pooled and the vertex
/// stage keeps the historical address above.</item>
/// </list>
/// </summary>
public static class InstanceSkinning
{
    /// <summary>Upper bound on joints per model. Beyond this the bake declines and the model keeps the CPU route: the scratch and
    /// palette blocks grow with the product of this and the instance count, so an arbitrary cap would be a silent memory bomb.</summary>
    public const int MaxJoints = 256;

    /// <summary>Upper bound on animation clips per model. The track table is clips x joints, so this one is cheap.</summary>
    public const int MaxClips = 64;

    /// <summary>Resample rate of the load-time bake, in Hz. Chosen above the usual 30 Hz keyframe cadence so the piecewise-linear
    /// replay stays inside the source curve's own sampling error; it is the accuracy/cost knob, not a magic number.</summary>
    public const int SampleRate = 60;

    /// <summary>Upper bound on resampled keys per clip. A clip longer than this is resampled at a lower effective rate rather than
    /// refused, so a 30-second cutscene still gets a bake instead of silently falling back.</summary>
    public const int MaxKeysPerClip = 2048;

    public const int JointStride = 256;
    public const int TrackStride = 16;
    public const int KeyStride = 48;
    public const int ParamStride = 16;
    public const int ScratchStride = 64;
    public const int KernelParamsBytes = 32;

    // ── P2 L1 / P3 L2: the pose-pool tag ─────────────────────────────────────────────────────────────────────────────────────
    //
    // Every instance's palette block ends with one extra row that holds nothing but the tag: block <c>i</c> owns rows
    // <c>i * stride</c> through <c>i * stride + stride - 1</c>, the first <c>jointCount</c> of which are joints and the last of
    // which is the tag. <c>stride</c> is therefore <c>jointCount + 1</c>, one row more than the model has joints.
    //
    // The tag cannot live at row <c>i</c>, even though that is the address a structured view would naturally index by instance
    // slot. <c>g_InstanceBoneMatrices[i]</c> resolves to row <c>i</c> of the flat palette, which belongs to instance
    // <c>i / stride</c> at joint <c>i % stride</c> - a live joint of some other instance as soon as <c>stride > 1</c>. Reading
    // there is harmless only while every such row holds zero in the two tag lanes, which is exactly today's state and exactly why
    // the defect stayed invisible: the decode returns <see cref="PoseTagUnpooled"/> and the historical address is taken. Writing a
    // tag there would corrupt a joint, so the row moved to one no vertex ever multiplies by.
    //
    // The two lanes themselves are bytes 48 and 52, M41 and M42. An affine palette guarantees they are zero: it holds
    // transpose(IBM * world * inverseMesh) with every factor affine, so the product's last column is (0, 0, 0, 1) and the
    // transpose moves that column into the last row. The vertex stage multiplies row vectors (v * P * W), so a non-zero M41
    // would displace the vertex by v.w times it - a constraint on every joint row rather than free space. The extra tag row is not
    // a joint row at all, so nothing multiplies by it and the two lanes are plain storage there.
    //
    // This is the same technique the A7 gather already uses on M14 of the instance world, and all four backends read the same
    // two bytes - but they do not spell them the same way. HLSL indexes a float4x4 by row, so <c>m[3]</c> starts at byte 12 and
    // the reserved lanes are <c>m[0].w</c> and <c>m[1].w</c>. GLSL, MSL and WGSL index by column, so <c>m[3]</c> already starts
    // at byte 48 and the lanes are <c>m[3].x</c> and <c>m[3].y</c>. A disassembly of the DX vertex stage confirms l(48) and l(52).

    /// <summary>Palette rows a tag occupies, appended to each instance's block after its joints. The vertex stage finds it at
    /// <c>instanceSlot * paletteStride + paletteStride - 1</c>, so the joints keep their historical
    /// <c>instanceSlot * paletteStride + jointIndex</c> address and only the tag lookup moved.</summary>
    public const int PoseTagRows = 1;

    /// <summary>Byte offset of the current-frame block row inside the tag matrix: M41, spelled <c>m[0].w</c> in HLSL and
    /// <c>m[3].x</c> in GLSL/MSL/WGSL because only the former indexes by row. The value is the first row of the block this instance
    /// draws its joints from - its own <c>instanceSlot * paletteStride</c> when unpooled, a pool block otherwise - and the vertex
    /// stage addresses joint <c>j</c> at that row plus <c>j</c>.</summary>
    public const int PoseTagCurrentOffset = 48;

    /// <summary>Byte offset of the previous-frame pooled row: M42, spelled <c>m[1].w</c> in HLSL and <c>m[3].y</c> in the other
    /// three. Kept next to the current one so the motion-vector path never has to re-derive history from this frame's row.</summary>
    public const int PoseTagPreviousOffset = 52;

    /// <summary>
    /// The row number that means "this instance is not pooled". Zero is special on purpose rather than incidental: it is what the
    /// tag row holds when nothing writes it - the CPU route clears it on every upload - so a palette produced by the historical
    /// path decodes to <see cref="PoseTagUnpooled"/> and the vertex stage takes the
    /// <c>instanceSlot * paletteStride + jointIndex</c> address. That is what let the decode ship first and change no pixel.
    /// </summary>
    public const uint PoseTagUnpooled = 0;

    /// <summary>Threads per dispatch group. One thread evaluates one whole instance sequentially, because the joints have to be
    /// visited in topological order and a per-thread local array of world matrices would not fit in registers.</summary>
    public const uint GroupSize = 64;

    /// <summary>
    /// A/B switch. Set <c>SEASON_INSTANCE_SKINNING=gpu</c> before launch to force every applicable model onto the GPU route and
    /// <c>=cpu</c> to force the reference route, without touching a single property. A model's own
    /// <see cref="Season.Controls.InstancedModel.SkinningMode"/> still decides what an app ships; this only decides what is measured.
    /// An unrecognised value leaves the per-model property in charge.
    /// </summary>
    public static InstanceSkinningMode? Override { get; set; } = ParseOverride();

    static InstanceSkinningMode? ParseOverride()
    {
        var raw = Environment.GetEnvironmentVariable("SEASON_INSTANCE_SKINNING");
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Equals("gpu", StringComparison.OrdinalIgnoreCase) || raw == "1") return InstanceSkinningMode.Gpu;
        if (raw.Equals("cpu", StringComparison.OrdinalIgnoreCase) || raw == "0") return InstanceSkinningMode.Cpu;
        return null;
    }

    static readonly List<IInstanceSkinningTarget> _targets = new();

    /// <summary>Groups that asked for GPU skinning this frame. Registration is idempotent and lives as long as the group does.</summary>
    public static List<IInstanceSkinningTarget> Targets => _targets;

    /// <summary>True once <see cref="InstanceSkinningEffect"/> compiled its kernel on this backend. When false every model keeps the
    /// CPU route regardless of its property, so a backend without the capability degrades to the historical path instead of
    /// drawing an unwritten palette.</summary>
    public static bool Enabled { get; internal set; }

    /// <summary>
    /// How many targets the effect actually dispatched for in the most recent FrameStart. This is the only per-frame evidence that the GPU
    /// route is the one drawing: a run whose A/B switch asked for it but whose counter is zero fell back somewhere, and its image would
    /// otherwise look exactly like a correct one.
    /// </summary>
    public static int DispatchedTargets { get; internal set; }

    /// <summary>
    /// P3 L2: shared pose groups the most recent FrameStart actually pooled, summed over every target. Zero on a frame where no two
    /// instances agreed on a (clip, tick) pair - the image such a frame produces is identical to the historical one, so without this
    /// counter a passing capture cannot distinguish "the pool worked" from "the pool was never asked to".
    /// </summary>
    public static int PooledGroups { get; internal set; }

    public static void Register(IInstanceSkinningTarget target)
    {
        if (target == null || _targets.Contains(target)) return;
        _targets.Add(target);
    }

    public static void Unregister(IInstanceSkinningTarget target)
    {
        if (target != null) _targets.Remove(target);
    }

    /// <summary>Clears the registry when the effect itself goes away, so a re-registration does not dispatch into disposed buffers.</summary>
    internal static void UnregisterAll()
    {
        _targets.Clear();
        Enabled = false;
    }
}

/// <summary>
/// P1: the buffer set one group hands the skinning kernel for a single frame. Produced by
/// <see cref="IInstanceSkinningTarget.PrepareSkinningFrame"/>, which is also where the per-instance 16-byte parameter block is
/// uploaded: the frame's params change every frame, the three bake blocks do not.
/// </summary>
public struct InstanceSkinResources
{
    public StorageBuffer? JointTable;
    public StorageBuffer? TrackTable;
    public StorageBuffer? Tracks;
    public StorageBuffer? Params;
    public StorageBuffer? Scratch;
    public StorageBuffer? Palette;

    public int InstanceCount;
    public int JointCount;
    public int ClipCount;

    /// <summary>P3 L2: pool blocks reserved after the instance blocks, one per shared (clip, tick) group. Zero means the frame
    /// pools nothing and the palette keeps the historical <c>InstanceCount * PaletteStride</c> shape.</summary>
    public int PoolRows;

    /// <summary>P3 L2: how many of those blocks this frame actually used, i.e. how many groups had more than one member. Reported
    /// through <see cref="InstanceSkinning.PooledGroups"/> so a capture can tell "pooled and identical" apart from "never pooled",
    /// which are the same image and only one of them exercises the path.</summary>
    public int PooledGroups;

    /// <summary>P3 L2: palette rows at the end of every instance block reserved for the pose-pool tag. Zero on a backend whose
    /// vertex stage does not read the tag yet, which also tells the kernel to leave the block alone.</summary>
    public int TagRows;

    /// <summary>Palette rows per instance, i.e. the total joint count of every skin in the model. Comes from
    /// <see cref="InstanceBoneStorage.Stride"/> so the kernel writes exactly where the vertex stage reads.</summary>
    public uint PaletteStride;
}

/// <summary>
/// P1: what an instanced group exposes to GPU skinning. Mirrors <see cref="IInstanceCullTarget"/>, including the phase rule, which
/// is the whole reason the two callbacks exist:
///
/// a CPU-to-GPU upload has to be recorded while the command list is open and outside any Pass, and the control's Update is neither.
/// The main loop runs App.Update before Device.BeforeRender, so an upload recorded during Update lands in a command list that the
/// previous frame already closed: D3D12 faults, Vulkan drops it through its IsRecording check, and the palette the vertex stage reads
/// stays whatever it was. <see cref="InstanceSkinningEffect"/> therefore calls back from inside Record, at FrameStart. This is also
/// where P0's palette staging had to move: it was called from Update, which is exactly the mistake this contract documents.
/// </summary>
public interface IInstanceSkinningTarget
{
    /// <summary>False when the group has nothing to skin or has already fallen back to the CPU this frame. A target that returns
    /// false from <see cref="PrepareSkinningFrame"/> is skipped entirely.</summary>
    bool PrepareSkinningFrame(IGraphics g, out InstanceSkinResources resources);

    /// <summary>
    /// Called after the dispatch. The implementer records the compute-write to vertex-read handoff on the palette slot it just had
    /// written (<see cref="IGraphics.TryPrepareVertexShaderRead"/>) and latches whether the draw may read it.
    ///
    /// This has to happen here, not in DrawShadow or Draw: by then a Pass is already open, and PassDesc forbids switching resource
    /// states inside a pass body (a Vulkan render-pass instance cannot contain a barrier). Same constraint, same landing point as
    /// <see cref="IInstanceCullTarget.PrepareCullDraw"/>.
    /// </summary>
    bool PrepareSkinningDraw();
}

/// <summary>
/// P1: the one implementation of <see cref="IInstanceSkinningTarget"/> the four backends share. Each backend fills in what only it
/// knows - the palette ring it latched, the bake its asset owns, how many instances it actually submitted, and the 16-byte parameter
/// row per instance - and this class owns everything that must be identical across them: when the bake is uploaded, how the
/// parameter and scratch blocks grow, and what the draw is allowed to assume.
///
/// Keeping it out of the backends is not tidiness. The failure mode of getting any of it wrong is a palette that is one frame stale
/// or one instance short, which renders as a plausible pose rather than an error, so there should be exactly one copy.
/// </summary>
internal sealed class InstanceSkinningTarget : IInstanceSkinningTarget
{
    /// <summary>The palette ring this frame draws from. Set by the backend from its own latch, null when it fell back to the CPU.</summary>
    public InstanceBoneStorage? Palette;

    public InstanceSkinBake? Bake;

    /// <summary>Instances actually submitted this frame, i.e. the compacted write index, which is what the kernel's thread count and
    /// the parameter block length both use.</summary>
    public int InstanceCount;

    public int JointCount;

    public int ClipCount;

    /// <summary>Palette rows per instance; equals the bake's <c>PaletteStride</c> and the stride the backend draws with.</summary>
    public uint PaletteStride;

    /// <summary>P3 L2: palette rows at the end of every block reserved for the pose-pool tag. Zero on a backend whose vertex stage
    /// still indexes the block by instance slot alone, which also means the kernel must not write a tag - a tag row it did not
    /// reserve would land on a live joint.</summary>
    public int TagRows;

    /// <summary>P3 L2: shared groups the backend pooled in the parameter block it just wrote. Counted, not inferred: it is the only
    /// evidence that the pool path ran at all on a frame whose output is supposed to look unchanged.</summary>
    public int PooledGroups;

    /// <summary>True once <see cref="PrepareSkinningDraw"/> recorded the handoff. Read by the backend's SRV getters at draw time,
    /// which is strictly after this point in the frame.</summary>
    public bool Live { get; private set; }

    byte[] _paramScratch = Array.Empty<byte>();
    StorageBuffer? _params;
    StorageBuffer? _scratch;

    /// <summary>
    /// Latches the frame's palette source and bake. False means the caller keeps the historical CPU route in full this frame: no
    /// storage buffer is bound, no kernel runs, and the palette is produced and uploaded exactly the way it was before P1.
    ///
    /// The two identity checks are not paranoia. The kernel addresses the palette as <c>instance * PaletteStride + paletteIndex</c>
    /// while the vertex stage reads it as <c>instance * BonePaletteStride + jointIndex</c>, so a draw stride narrower than the bake's
    /// joint table would make two instances share rows and still render a plausible pose - the exact class of bug the A/B comparison
    /// exists to catch, and the reason the test lives here instead of in each backend. A wider one is legal: the Web stage hardcodes a
    /// 100-row stride, so a model with fewer joints still hands the kernel a 100-row block and only the first rows are ever read back.
    /// </summary>
    public bool Begin(InstanceBoneStorage? palette, InstanceSkinBake? bake, int paletteStride, int clipCount, int tagRows = 0)
    {
        Palette = null;
        Bake = null;
        InstanceCount = 0;
        TagRows = 0;
        PooledGroups = 0;
        if (palette == null || bake == null || paletteStride <= 0 || tagRows < 0) return false;
        if (bake.PaletteStride > paletteStride || bake.ClipCount != clipCount) return false;
        // The tag rows are extra rows, not part of the joints, so the block must be wide enough to hold both.
        if (tagRows > 0 && (uint)(bake.PaletteStride + tagRows) > (uint)paletteStride) return false;

        Palette = palette;
        Bake = bake;
        PaletteStride = (uint)paletteStride;
        JointCount = bake.JointCount;
        ClipCount = bake.ClipCount;
        TagRows = tagRows;
        return true;
    }

    /// <summary>
    /// Points the ring at this frame's slots and grows it to the shape the draw needs. The slot count comes from the caller because
    /// it is the backend's frame-ring depth: a ring of a different depth would alias the slots and hand the vertex stage the pose of a
    /// frame the GPU may not have finished with.
    /// </summary>
    public bool PreparePalette(IGraphics g, int slots, int currentSlot, int previousSlot, int instanceCount)
    {
        var palette = Palette;
        if (g == null || palette == null || instanceCount <= 0 || slots < 2) return false;

        palette.CurrentSlot = currentSlot;
        palette.PreviousSlot = previousSlot;

        // P3 L2: the ring is sized for one pool block per instance on top of the instance blocks. The group count is only known
        // once the frame's clocks are read, and this call precedes that read, so the reservation is the worst case rather than the
        // frame's actual need: no frame can share more groups than it has instances, and an instance block is the unit.
        int capacity = Math.Max(palette.Capacity, instanceCount);
        if (palette.Slots.Length != slots || palette.Stride != (int)PaletteStride
            || palette.Capacity != capacity || palette.PoolRows != capacity)
        {
            if (!palette.Resize(g, slots, (int)PaletteStride, capacity, capacity)) return false;
        }
        // A ring shallower than two slots has no previous frame to point at, and the previous-slot read would then alias the slot
        // this frame writes.
        return palette.Slots.Length >= 2 && palette.Ready;
    }

    /// <summary>
    /// Sizes this frame's parameter block and clears it, so a row whose trailing fields nobody writes stays deterministic instead of
    /// carrying the previous frame's bits into the kernel. Returns false when the count is unusable.
    /// </summary>
    public bool BeginFrame(int instanceCount)
    {
        InstanceCount = instanceCount;
        int need = instanceCount * InstanceSkinning.ParamStride;
        if (need <= 0) return false;
        if (_paramScratch.Length < need) _paramScratch = new byte[need];
        _paramScratch.AsSpan(0, need).Clear();
        return true;
    }

    /// <summary>
    /// Writes one instance's 16 bytes: its clip index and its playback time, already normalized by the CPU clock, plus the two
    /// fields the pose pool uses (P3 L2). This is the entire per-frame cost of the GPU route on the CPU side, and the only part of
    /// the parameter block that changes between two frames.
    ///
    /// <paramref name="evaluate"/> false means another instance already evaluates this frame's pose into <paramref name="poolRow"/>
    /// and this thread only has to publish its tag. <paramref name="poolRow"/> is the first palette row of that shared block, or 0
    /// for an instance that owns its own block - which is also the only state a frame with no shared groups can produce, so the
    /// kernel's output is then byte for byte what the historical path wrote.
    ///
    /// Both fields default to the unpooled case on purpose. A row written without them decodes as "evaluate, own block", which is
    /// what every caller written before P3 L2 meant, and the wrong answer here would be a row that silently skips its own
    /// evaluation.
    /// </summary>
    public void SetInstance(int instanceIndex, uint clip, float time, bool evaluate = true, uint poolRow = 0)
    {
        int offset = instanceIndex * InstanceSkinning.ParamStride;
        if ((uint)offset + InstanceSkinning.ParamStride > (uint)_paramScratch.Length) return;

        var row = _paramScratch.AsSpan(offset, InstanceSkinning.ParamStride);
        BitConverter.TryWriteBytes(row, clip);
        BitConverter.TryWriteBytes(row[4..], time);
        BitConverter.TryWriteBytes(row[8..], evaluate ? 1u : 0u);
        BitConverter.TryWriteBytes(row[12..], poolRow);
    }

    public bool PrepareSkinningFrame(IGraphics g, out InstanceSkinResources resources)
    {
        resources = default;
        Live = false;
        if (g == null || Palette == null || Bake == null) return false;
        if (InstanceCount <= 0 || JointCount <= 0 || ClipCount <= 0) return false;
        if (!Palette.Ready) return false;
        if (!Bake.EnsureUploaded(g)) return false;

        int paramBytes = InstanceCount * InstanceSkinning.ParamStride;
        if (_paramScratch.Length < paramBytes) return false;
        if (_params == null || _params.SizeInBytes < (uint)paramBytes)
        {
            _params?.Dispose();
            _params = g.CreateStorageBuffer((uint)paramBytes);
            if (_params == null) return false;
        }

        // P3 L2: a representative evaluates into the pool block's index space, so the scratch carries one block per pool block as
        // well. The pool blocks sit after the instance blocks, which is the same numbering the palette uses - one index space.
        int poolRows = Math.Max(0, Palette.PoolRows);
        long scratchBytes = (long)(InstanceCount + poolRows) * JointCount * InstanceSkinning.ScratchStride;
        if (scratchBytes > int.MaxValue) return false;
        if (_scratch == null || _scratch.SizeInBytes < (uint)scratchBytes)
        {
            _scratch?.Dispose();
            _scratch = g.CreateStorageBuffer((uint)scratchBytes);
            if (_scratch == null) return false;
        }

        // One upload of N x 16 bytes, which is the entire per-frame cost of the GPU route on the CPU side.
        if (!g.TryUpdateStorageBuffer(_params, _paramScratch.AsSpan(0, paramBytes))) return false;

        resources.JointTable = Bake.JointTable;
        resources.TrackTable = Bake.TrackTable;
        resources.Tracks = Bake.Tracks;
        resources.Params = _params;
        resources.Scratch = _scratch;
        resources.Palette = Palette.Current;
        resources.InstanceCount = InstanceCount;
        resources.JointCount = JointCount;
        resources.ClipCount = ClipCount;
        resources.PoolRows = poolRows;
        resources.PooledGroups = PooledGroups;
        resources.TagRows = TagRows;
        resources.PaletteStride = PaletteStride;
        return true;
    }

    public bool PrepareSkinningDraw()
    {
        var current = Palette?.Current;
        Live = current != null && Graphics.Instance.TryPrepareVertexShaderRead(current);
        return Live;
    }

    /// <summary>Drops the target from the scheduler and releases the two blocks it owns. The palette ring belongs to the backend
    /// (or to the app), never to this type.</summary>
    public void Dispose()
    {
        InstanceSkinning.Unregister(this);
        _params?.Dispose();
        _scratch?.Dispose();
        _params = null;
        _scratch = null;
        Live = false;
        Palette = null;
        Bake = null;
        InstanceCount = 0;
    }
}

/// <summary>
/// P1: the skinning scheduler. FrameStart and App scope for the same reason as culling: dispatch has to be outside every Pass
/// (Vulkan forbids it inside one, and Metal's compute and render encoders are mutually exclusive), and the palette is consumed by the
/// one and only scene pass, so there is nothing to repeat per view.
///
/// One dispatch per target per frame, one thread per instance. Registration returning false is the graceful degradation path: the
/// backend had no kernel source, no compute, or cannot bind a storage buffer into the vertex stage, <see cref="InstanceSkinning.Enabled"/>
/// stays false, every model keeps its CPU palette ring, and no GPU resource was ever allocated.
/// </summary>
public sealed partial class InstanceSkinningEffect : ComputeEffect, IDisposable
{
    public override string Name => "instanceSkinning";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    readonly uint[] _kernelParams = new uint[InstanceSkinning.KernelParamsBytes / 4];
    readonly ComputeResourceRef[] _refs = new ComputeResourceRef[6];

    IGraphics? _graphics;
    ComputeKernel? _kernel;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("Instance skinning is already initialized.");
        // VertexShaderStorageReadSupported is the hard gate, not an optimisation: without it the kernel's output could be written
        // but never read by the vertex stage, and the model would draw with a palette nobody can see.
        if (!g.ComputeSupported || !g.VertexShaderStorageReadSupported) return false;
        _graphics = g;
        try
        {
            _kernel = g.CreateComputeKernel(CreateSkinningKernel());
            if (_kernel == null) { Dispose(); return false; }
            InstanceSkinning.Enabled = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[InstanceSkinning] Initialization failed: {ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        InstanceSkinning.DispatchedTargets = 0;
        InstanceSkinning.PooledGroups = 0;
        if (!ReferenceEquals(g, _graphics) || _kernel == null) return;
        if (InstanceSkinning.Targets.Count == 0) return;

        for (int i = 0; i < InstanceSkinning.Targets.Count; i++)
        {
            var target = InstanceSkinning.Targets[i];
            if (!target.PrepareSkinningFrame(g, out var r)) continue;
            if (!Validate(in r)) continue;
            Dispatch(g, in r);
            target.PrepareSkinningDraw();
            InstanceSkinning.DispatchedTargets++;
            InstanceSkinning.PooledGroups += r.PooledGroups;
        }
    }

    /// <summary>
    /// Every block is checked against what the kernel will address before anything is recorded. A short buffer here does not fail
    /// loudly: the kernel would read past it into whatever follows and write a plausible-looking but wrong pose, which is exactly
    /// the class of bug the A/B comparison exists to catch and the reason the checks live in the shared layer instead of each backend.
    /// </summary>
    static bool Validate(in InstanceSkinResources r)
    {
        if (r.JointTable == null || r.TrackTable == null || r.Tracks == null
            || r.Params == null || r.Scratch == null || r.Palette == null) return false;
        if (r.InstanceCount <= 0 || r.JointCount <= 0 || r.ClipCount <= 0) return false;
        if (r.JointCount > InstanceSkinning.MaxJoints || r.ClipCount > InstanceSkinning.MaxClips) return false;
        if (r.PaletteStride < (uint)r.JointCount) return false;
        if (r.PoolRows < 0 || r.TagRows < 0) return false;

        if (r.JointTable.SizeInBytes < (uint)(r.JointCount * InstanceSkinning.JointStride)) return false;
        if (r.TrackTable.SizeInBytes < (uint)(r.ClipCount * r.JointCount * InstanceSkinning.TrackStride)) return false;
        if (r.Params.SizeInBytes < (uint)(r.InstanceCount * InstanceSkinning.ParamStride)) return false;
        // Scratch and palette are per block now, so their size is the product that has to be checked, not just the stride. The pool
        // blocks are real blocks in both buffers, so they are counted here rather than assumed to fit.
        long blockCount = (long)r.InstanceCount + r.PoolRows;
        if (r.Scratch.SizeInBytes < (ulong)blockCount * (uint)r.JointCount * InstanceSkinning.ScratchStride) return false;
        if (r.Palette.SizeInBytes < (ulong)blockCount * r.PaletteStride * (uint)StorageBuffer.VertexShaderReadElementBytes) return false;
        return true;
    }

    void Dispatch(IGraphics g, in InstanceSkinResources r)
    {
        _kernelParams[0] = (uint)r.JointCount;
        _kernelParams[1] = (uint)r.InstanceCount;
        _kernelParams[2] = r.PaletteStride;
        // P3 L2: rows reserved for the tag at the end of every block. Zero keeps the kernel from touching them at all, which is what
        // a backend whose vertex stage does not read the tag yet passes.
        _kernelParams[3] = (uint)r.TagRows;
        // Strides travel as parameters rather than living in the four kernel sources, for the same reason InstanceCull threads its
        // accumulator offset through: a stride written four times is four chances for the dialects to drift apart.
        _kernelParams[4] = InstanceSkinning.JointStride;
        _kernelParams[5] = InstanceSkinning.TrackStride;
        _kernelParams[6] = InstanceSkinning.KeyStride;
        _kernelParams[7] = InstanceSkinning.ScratchStride;

        _refs[0] = r.JointTable!;
        _refs[1] = r.TrackTable!;
        _refs[2] = r.Tracks!;
        _refs[3] = r.Params!;
        _refs[4] = r.Scratch!;
        _refs[5] = r.Palette!;

        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _kernel!,
            Params = MemoryMarshal.AsBytes(_kernelParams.AsSpan()),
            Resources = _refs,
            GroupsX = ((uint)r.InstanceCount + InstanceSkinning.GroupSize - 1) / InstanceSkinning.GroupSize,
            GroupsY = 1,
            GroupsZ = 1,
        });
    }

    public override void OnUnregistered()
    {
        InstanceSkinning.UnregisterAll();
    }

    public void Dispose()
    {
        InstanceSkinning.Enabled = false;
        _kernel?.Dispose();
        _kernel = null;
        _graphics = null;
    }
}
