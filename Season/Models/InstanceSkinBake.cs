// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;
using Season.Rendering;

namespace Season.Models;

/// <summary>
/// P1: the load-time bake that moves an instanced model's clip replay onto the GPU.
///
/// The idea is to spend the CPU's expensive part once, while nobody is watching, and keep the cheap part per frame. At load time
/// every clip is replayed through the very same <see cref="GLTFAnimationPlayer"/> the CPU route uses, sampled at
/// <see cref="InstanceSkinning.SampleRate"/>, and the resulting joint TRS values are flattened into three immutable tables. At run
/// time the only thing left on the CPU is a clock: each instance's clip index and playback time, 16 bytes, uploaded in one block.
/// The kernel then walks the joint hierarchy, applies the parent chain, composes the inverse-bind and inverse-mesh matrices, and
/// writes the palette straight into the storage ring the vertex stage already reads.
///
/// Resampling through the player rather than re-implementing the sampler is deliberate. The clips carry Step, Linear and
/// CubicSpline interpolations and the player is the only thing that knows what each one means; baking by replaying it means the
/// bake cannot disagree with the reference path about the curve, only about how often it is sampled. That residual, at 60 Hz
/// against a source that is itself sampled at 30-60 Hz, is the one documented difference between the two routes.
///
/// Applicability. The bake is only sound when every animated node is a joint:
/// <list type="bullet">
/// <item>A non-joint animated node would move rigidly, and the GPU route has no way to feed that back into the per-primitive
/// instance streams, which the CPU builds every frame from <c>OwnerNode.WorldTransform</c>. Those would keep the rest pose.</item>
/// <item>A morph-weight channel writes into per-instance vertex data, which lives in the same CPU-built streams. GPU skinning does
/// not touch morph targets at all, so a model that animates them keeps the CPU route.</item>
/// <item>A skin's bind node must not be animated either, because it enters the palette as a single inverse-mesh matrix baked once.
/// If it moved, every bone of that skin would be off by the same amount.</item>
/// </list>
/// Any violation declines the whole model. Partial baking would be worse than none: half the joints would move and the rest would
/// freeze, and nothing in the image would say which half was which.
/// </summary>
internal sealed class InstanceSkinBake
{
    /// <summary>Hard ceiling on the three immutable tables combined. A 2048-key, 256-joint, 64-clip model would otherwise reach
    /// 1.6 GB; the bake declines rather than allocates that, and the model keeps the CPU route.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>Joints in topological order. Equal to <see cref="PaletteStride"/>: a joint shared by two skins would need two
    /// palette slots while contributing one world matrix, so that shape is declined instead of half-written.</summary>
    public int JointCount { get; private set; }

    public int ClipCount { get; private set; }

    /// <summary>Palette rows per instance, which is the flattened joint count of every skin (see
    /// <see cref="GltfAsset.GetSkinPaletteOffset"/>). The kernel writes <c>instance * PaletteStride + paletteIndex</c>.</summary>
    public int PaletteStride { get; private set; }

    /// <summary>Duration of each clip, computed the same way the player computes it. The CPU clock normalizes the playback time
    /// with it before handing the kernel a time, so the kernel never has to know about looping or clamping.</summary>
    public float[] ClipDurations { get; private set; } = Array.Empty<float>();

    byte[] _jointTable = Array.Empty<byte>();
    byte[] _trackTable = Array.Empty<byte>();
    byte[] _tracks = Array.Empty<byte>();
    float[] _tracksFloat = Array.Empty<float>();

    StorageBuffer? _jointBuffer;
    StorageBuffer? _trackBuffer;
    StorageBuffer? _tracksBuffer;

    /// <summary>Set once the three tables are resident. Upload happens on the first FrameStart, never at load: the command list is
    /// not open during loading, and a barrier recorded there fails exactly the way an Update-phase upload does.</summary>
    public bool Uploaded { get; private set; }

    InstanceSkinBake() { }

    /// <summary>
    /// The one place the four backends ask for a bake, so the eligibility test cannot drift between them: only when the effective
    /// mode asks for the GPU route and this backend can actually read a compute-written buffer from the vertex stage. Called from
    /// each backend's LoadInstancedModel, after the node tree and the animation player are initialized and before the rest pose is
    /// disturbed by anything.
    /// </summary>
    internal static void TryBake(GltfAsset asset, InstanceSkinningMode mode)
    {
        if (asset == null || asset.SkinBake != null) return;
        if ((InstanceSkinning.Override ?? mode) != InstanceSkinningMode.Gpu) return;
        var g = Graphics.Instance;
        if (g == null || !g.ComputeSupported || !g.VertexShaderStorageReadSupported) return;
        if (TryCreate(asset, out var bake)) asset.SkinBake = bake;
    }

    /// <summary>
    /// Builds the tables for <paramref name="asset"/>, or returns false when the model is not eligible. Never throws for an
    /// ineligible model: eligibility is a property of the asset, not an error.
    /// </summary>
    public static bool TryCreate(GltfAsset asset, out InstanceSkinBake bake)
    {
        bake = null;
        if (asset == null || asset._animations.Count == 0) return false;

        var nodes = asset.gltfNodes;
        if (nodes.Count == 0) return false;

        var skins = asset.GetAllSkins();
        if (skins.Count == 0) return false;
        if (asset._animations.Count > InstanceSkinning.MaxClips) return false;

        // Flattened palette order, exactly as GLTFAnimationPlayer.UpdateBoneMatrices writes it. A node appearing in two skins would
        // occupy two slots with one world matrix; that shape is declined rather than written twice.
        var joints = new List<GltfNodeBase>();
        var slot = new Dictionary<GltfNodeBase, int>(nodes.Count);
        var jointSkin = new Dictionary<GltfNodeBase, GLTFSkin>(nodes.Count);
        for (int s = 0; s < skins.Count; s++)
        {
            var skin = skins[s];
            if (skin?.Joints == null || skin.InverseBindMatrices == null) return false;
            if (skin.Joints.Count != skin.InverseBindMatrices.Count) return false;
            for (int j = 0; j < skin.Joints.Count; j++)
            {
                var joint = skin.Joints[j];
                if (joint == null) return false;
                if (slot.ContainsKey(joint)) return false;
                slot.Add(joint, joints.Count);
                jointSkin.Add(joint, skin);
                joints.Add(joint);
            }
        }

        int jointCount = joints.Count;
        if (jointCount == 0 || jointCount > InstanceSkinning.MaxJoints) return false;

        // Parent links come from the node tree; glTF has no parent pointer, only child lists.
        var parentOf = new Dictionary<GltfNodeBase, GltfNodeBase>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            var children = nodes[i].Children;
            if (children == null) continue;
            for (int c = 0; c < children.Count; c++)
                if (children[c] != null) parentOf[children[c]] = nodes[i];
        }

        // Applicability: every channel target must be a joint, and no channel may animate a morph or a skin's bind node.
        var bindNodes = new HashSet<GltfNodeBase>();
        for (int s = 0; s < skins.Count; s++)
            if (skins[s]?.BindNode != null) bindNodes.Add(skins[s].BindNode);

        for (int a = 0; a < asset._animations.Count; a++)
        {
            var channels = asset._animations[a]?.Channels;
            if (channels == null) continue;
            for (int c = 0; c < channels.Count; c++)
            {
                var channel = channels[c];
                var node = channel?.Target?.Node;
                if (node == null || channel.Sampler == null) continue;
                if (channel.Target.Path == AnimationTargetPath.Weights) return false;
                if (!slot.ContainsKey(node)) return false;
                if (bindNodes.Contains(node)) return false;
            }
        }

        // Rest world matrix of every node. Needed for the static part of a joint's parent chain: a parent that is not a joint
        // cannot move, because every animated node is a joint, so its own local transform and everything below the nearest joint
        // ancestor are constant.
        //
        // The walk goes up the parent chain per node rather than forward over the node list. glTF does not require a parent to be
        // declared before its children, and a forward pass that reads restWorld[parent] would then silently drop the whole ancestor
        // chain of every node listed before its parent. On the robot asset the skin's bind node is one of those, so its inverse
        // came out without the armature's own transform and scaled every joint of the model - the whole figure shrank and the limbs
        // landed in the wrong place while the CPU route, which reads the loader's WorldTransform, stayed correct.
        var restWorld = new Dictionary<GltfNodeBase, Matrix4x4>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;

            var world = LocalOf(node.InitialScale, node.InitialRotation, node.InitialTranslation);
            var cursor = parentOf.TryGetValue(node, out var parent) ? parent : null;
            while (cursor != null)
            {
                world = world * LocalOf(cursor.InitialScale, cursor.InitialRotation, cursor.InitialTranslation);
                cursor = parentOf.TryGetValue(cursor, out var up) ? up : null;
            }
            restWorld[node] = world;
        }

        // Topological order: depth first from the roots, so a joint is always emitted after its joint ancestors.
        var order = new List<GltfNodeBase>(jointCount);
        var seen = new HashSet<GltfNodeBase>(nodes.Count);
        var stack = new Stack<GltfNodeBase>();
        for (int i = nodes.Count - 1; i >= 0; i--)
            if (nodes[i] != null && !parentOf.ContainsKey(nodes[i])) stack.Push(nodes[i]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node == null || !seen.Add(node)) continue;
            if (slot.ContainsKey(node)) order.Add(node);
            var children = node.Children;
            if (children == null) continue;
            for (int c = children.Count - 1; c >= 0; c--) stack.Push(children[c]);
        }
        if (order.Count != jointCount) return false;

        var orderIndex = new Dictionary<GltfNodeBase, int>(jointCount);
        for (int i = 0; i < order.Count; i++) orderIndex[order[i]] = i;

        // Clip durations, computed the same way the player does: the last input of the sampler that reaches furthest.
        var durations = new float[asset._animations.Count];
        for (int a = 0; a < durations.Length; a++)
        {
            var channels = asset._animations[a]?.Channels;
            float max = 0f, first = float.MaxValue;
            if (channels != null)
            {
                for (int c = 0; c < channels.Count; c++)
                {
                    var inputs = channels[c]?.Sampler?.Inputs;
                    if (inputs == null || inputs.Count == 0) continue;
                    float last = inputs[inputs.Count - 1];
                    if (last > max) max = last;
                    if (inputs[0] < first) first = inputs[0];
                }
            }
            // The player measures a clip from zero to the last input, not from the first, so the bake does the same.
            durations[a] = max;
        }

        // Key counts per clip. A clip with no duration still gets two keys so the kernel never divides by a zero frame count.
        var keysPerClip = new int[durations.Length];
        long totalKeys = 0;
        for (int a = 0; a < keysPerClip.Length; a++)
        {
            float raw = durations[a] * InstanceSkinning.SampleRate + 1f;
            int keys = raw >= InstanceSkinning.MaxKeysPerClip ? InstanceSkinning.MaxKeysPerClip : (int)MathF.Ceiling(raw);
            if (keys < 2) keys = 2;
            keysPerClip[a] = keys;
            totalKeys += keys;
        }

        long jointBytes = (long)jointCount * InstanceSkinning.JointStride;
        long trackBytes = (long)keysPerClip.Length * jointCount * InstanceSkinning.TrackStride;
        long keyBytes = totalKeys * jointCount * InstanceSkinning.KeyStride;
        if (jointBytes + trackBytes + keyBytes > MaxBytes) return false;

        var result = new InstanceSkinBake
        {
            JointCount = jointCount,
            ClipCount = keysPerClip.Length,
            PaletteStride = jointCount,
            ClipDurations = durations,
            _jointTable = new byte[jointBytes],
            _trackTable = new byte[trackBytes],
            _tracksFloat = new float[keyBytes / 4],
        };

        // --- joint table ---
        var jt = MemoryMarshal.Cast<byte, float>(result._jointTable.AsSpan());
        const int jointFloats = InstanceSkinning.JointStride / 4;
        for (int i = 0; i < order.Count; i++)
        {
            var node = order[i];
            int b = i * jointFloats;

            // The parent chain splits into "the nearest joint ancestor, read from scratch" and "everything between it and this
            // node, a constant matrix". Both cases collapse into one formula in the kernel: parent = staticChain * (porder >= 0 ?
            // scratch[porder] : identity). A joint whose direct parent is a joint therefore stores an identity static chain.
            Matrix4x4 staticChain = Matrix4x4.Identity;
            int parentOrder = -1;
            var cursor = parentOf.TryGetValue(node, out var p0) ? p0 : null;
            while (cursor != null)
            {
                if (orderIndex.TryGetValue(cursor, out int pi)) { parentOrder = pi; break; }
                staticChain = staticChain * LocalOf(cursor.InitialScale, cursor.InitialRotation, cursor.InitialTranslation);
                cursor = parentOf.TryGetValue(cursor, out var up) ? up : null;
            }

            jt[b + 0] = BitConverter.UInt32BitsToSingle((uint)slot[node]);
            jt[b + 1] = BitConverter.UInt32BitsToSingle(unchecked((uint)parentOrder));
            jt[b + 2] = 0f; // animatedMask, filled after the pass over the clips
            jt[b + 3] = 0f;

            jt[b + 4] = node.InitialTranslation.X;
            jt[b + 5] = node.InitialTranslation.Y;
            jt[b + 6] = node.InitialTranslation.Z;
            jt[b + 7] = 0f;
            jt[b + 8] = node.InitialRotation.X;
            jt[b + 9] = node.InitialRotation.Y;
            jt[b + 10] = node.InitialRotation.Z;
            jt[b + 11] = node.InitialRotation.W;
            jt[b + 12] = node.InitialScale.X;
            jt[b + 13] = node.InitialScale.Y;
            jt[b + 14] = node.InitialScale.Z;
            jt[b + 15] = 0f;

            WriteMatrix(jt, b + 16, staticChain);

            var skin = jointSkin[node];
            int jointInSkin = skin.Joints.IndexOf(node);
            var inverseBind = jointInSkin >= 0 ? skin.InverseBindMatrices[jointInSkin] : Matrix4x4.Identity;
            WriteMatrix(jt, b + 32, inverseBind);

            Matrix4x4 inverseMesh = Matrix4x4.Identity;
            if (skin.BindNode != null && restWorld.TryGetValue(skin.BindNode, out var bindWorld))
            {
                if (!Matrix4x4.Invert(bindWorld, out inverseMesh))
                    inverseMesh = Matrix4x4.Identity;
            }
            WriteMatrix(jt, b + 48, inverseMesh);
        }

        // animatedMask: which channels any clip animates on this joint. Diagnostic today, and the field a later revision would read
        // to skip the track fetch entirely for a joint no clip touches - without changing the layout.
        var animatedMask = new uint[jointCount];
        for (int a = 0; a < asset._animations.Count; a++)
        {
            var channels = asset._animations[a]?.Channels;
            if (channels == null) continue;
            for (int c = 0; c < channels.Count; c++)
            {
                var channel = channels[c];
                var node = channel?.Target?.Node;
                if (node == null || !orderIndex.TryGetValue(node, out int oi)) continue;
                animatedMask[oi] |= channel.Target.Path switch
                {
                    AnimationTargetPath.Translation => 1u,
                    AnimationTargetPath.Rotation => 2u,
                    AnimationTargetPath.Scale => 4u,
                    _ => 0u,
                };
            }
        }
        for (int i = 0; i < jointCount; i++)
            jt[i * jointFloats + 2] = BitConverter.UInt32BitsToSingle(animatedMask[i]);

        // --- track table + keys ---
        var tt = MemoryMarshal.Cast<byte, uint>(result._trackTable.AsSpan());
        const int trackUints = InstanceSkinning.TrackStride / 4;
        const int keyFloats = InstanceSkinning.KeyStride / 4;
        // Keys are laid out [clip][key][joint]; every track of a clip shares that clip's first row, which the track table stores
        // already multiplied by jointCount (see the note next to tt[at + 1]).
        int keyCursor = 0;

        var player = asset._animationPlayer;
        bool savedLoop = player.Loop;
        int savedClip = player.CurrentAnimationIndex;
        try
        {
            // Clamp instead of wrap while baking, so the final sample lands exactly on the clip end rather than on key zero.
            player.Loop = false;
            // Per clip, per joint: which of T/R/S this clip actually animates. Two readers depend on it. It is the channelMask
            // the layout contract puts at track[12], and it decides which components the keys below are taken from - a component
            // the clip does not animate is written at its rest value, so every stored track is a complete local TRS.
            //
            // The second half is not load-bearing today: ApplyAnimation's first statement is ResetNodesToInitialState, so the
            // untouched component already reads as its rest value and the two spellings agree. That is exactly why the table
            // should not rely on it - the kernel only ever asks "does this track exist" and then consumes all three components,
            // so spelling the rest values out keeps the data correct if that reset ever moves or a sampler is ever added that
            // writes a subset. A joint with at least one channel is driven; for a joint no channel reaches, tt[0] = 0 skips it.
            var drivenMask = new uint[jointCount];
            for (int a = 0; a < result.ClipCount; a++)
            {
                var animation = asset._animations[a];
                int keys = keysPerClip[a];
                float duration = durations[a];
                int clipBaseKey = keyCursor;

                Array.Clear(drivenMask);
                var channels = animation?.Channels;
                if (channels != null)
                {
                    for (int c = 0; c < channels.Count; c++)
                    {
                        var channel = channels[c];
                        var node = channel?.Target?.Node;
                        if (node == null || !orderIndex.TryGetValue(node, out int oi)) continue;
                        drivenMask[oi] |= channel.Target.Path switch
                        {
                            AnimationTargetPath.Translation => 1u,
                            AnimationTargetPath.Rotation => 2u,
                            AnimationTargetPath.Scale => 4u,
                            _ => 0u,
                        };
                    }
                }

                for (int k = 0; k < keys; k++)
                {
                    float t = duration * k / (keys - 1);
                    player.Evaluate(a, t, nodes);
                    int baseFloat = (clipBaseKey + k) * jointCount * keyFloats;
                    for (int j = 0; j < jointCount; j++)
                    {
                        var node = order[j];
                        uint mask = drivenMask[j];
                        int at = baseFloat + j * keyFloats;

                        var translation = (mask & 1u) != 0u ? node.Translation : node.InitialTranslation;
                        var rotation = (mask & 2u) != 0u ? node.Rotation : node.InitialRotation;
                        var scale = (mask & 4u) != 0u ? node.Scale : node.InitialScale;

                        result._tracksFloat[at + 0] = translation.X;
                        result._tracksFloat[at + 1] = translation.Y;
                        result._tracksFloat[at + 2] = translation.Z;
                        result._tracksFloat[at + 3] = 0f;
                        result._tracksFloat[at + 4] = rotation.X;
                        result._tracksFloat[at + 5] = rotation.Y;
                        result._tracksFloat[at + 6] = rotation.Z;
                        result._tracksFloat[at + 7] = rotation.W;
                        result._tracksFloat[at + 8] = scale.X;
                        result._tracksFloat[at + 9] = scale.Y;
                        result._tracksFloat[at + 10] = scale.Z;
                        result._tracksFloat[at + 11] = 0f;
                    }
                }

                // sampleRate = (keys - 1) / duration is what turns the normalized time into a fractional key index. For a
                // zero-length clip the track is never driven (no channel reaches it) and the rate is left at zero.
                //
                // firstKey is a row index, not a key index: it points at this clip's first row in the (key, joint) plane the
                // tracks buffer is flattened into, so the kernel reaches joint j of frame f at firstKey + f * jointCount + j.
                // Storing the un-multiplied key number here read the right rows only for clip 0, because that is the one clip
                // whose base is zero - every later clip sampled a block jointCount times too small, which is the limbs-on-wrong-
                // joints image. The four dialects all spell the lookup the same way; this is the side that was wrong.
                float rate = duration > 0f ? (keys - 1) / duration : 0f;
                int tb = a * jointCount * trackUints;
                for (int j = 0; j < jointCount; j++)
                {
                    int at = tb + j * trackUints;
                    tt[at + 0] = drivenMask[j] != 0u ? (uint)keys : 0u;
                    tt[at + 1] = drivenMask[j] != 0u ? (uint)(clipBaseKey * jointCount) : 0u;
                    tt[at + 2] = BitConverter.SingleToUInt32Bits(rate);
                    tt[at + 3] = drivenMask[j];
                }

                keyCursor += keys;
            }
        }
        finally
        {
            player.Loop = savedLoop;
            // Replaying the saved clip at time zero leaves the tree in the same rest pose the CPU route snapshots at load, rather
            // than in whatever frame the bake happened to stop on.
            player.Evaluate(savedClip, 0f, nodes);
            RestoreInitialPose(nodes, parentOf);
        }

        result._tracks = MemoryMarshal.AsBytes(result._tracksFloat.AsSpan()).ToArray();

#if DEBUG
        SelfCheck(asset, result, nodes, order, slot, parentOf, restWorld, jt, tt, jointFloats, trackUints, keyFloats);
#endif

        result._tracksFloat = Array.Empty<float>();

        bake = result;
        return true;
    }

#if DEBUG
    /// <summary>
    /// Load-time verification of everything the kernel will later recompute from these tables. The tables are pure data, so the
    /// only way a wrong pose can survive to the screen is if they disagree with the reference route, and a disagreement is much
    /// easier to read as a joint index and an error magnitude than as a distorted limb.
    ///
    /// Two things are checked. First the static inputs: the rest world matrices, which are built by walking each node's parent
    /// chain and must therefore agree with the loader's independent recursive walk, and the inverse-mesh matrix every joint
    /// multiplies by that comes from them - a mismatch there scales the whole figure at once instead of bending one limb.
    /// Second a straight CPU replay of the kernel's arithmetic - rest or sampled TRS, parent chain, inverse bind, inverse mesh,
    /// transpose - compared against <c>GLTFAnimationPlayer.UpdateBoneMatrices</c> at three probe times of every clip.
    /// </summary>
    static void SelfCheck(GltfAsset asset, InstanceSkinBake bake, List<GltfNodeBase> nodes, List<GltfNodeBase> order,
        Dictionary<GltfNodeBase, int> slot, Dictionary<GltfNodeBase, GltfNodeBase> parentOf,
        Dictionary<GltfNodeBase, Matrix4x4> restWorld, Span<float> jt, Span<uint> tt, int jointFloats, int trackUints, int keyFloats)
    {
        var log = DeviceServices.BaseApp;
        var skins = asset.GetAllSkins();

        // 1) Cross-check the rest world matrices against the loader's, which were built by a recursive walk at load and are
        // therefore independent of the file order. A bind node is where a mismatch hurts most: its inverse scales every joint
        // of the skin at once, so an error there shrinks the whole figure instead of bending one limb.
        int orderViolations = 0;
        for (int i = 0; i < skins.Count; i++)
        {
            var bind = skins[i]?.BindNode;
            if (bind == null || !restWorld.TryGetValue(bind, out var baked)) continue;
            float delta = MaxDelta(baked, bind.WorldTransform);
            if (delta > 1e-3f)
            {
                orderViolations++;
                log?.AddLog(LogType.Error,
                    $"[Bake] bind node '{bind.Name}' rest world differs by {delta:F4} from the loader's WorldTransform");
            }
        }

        // 2) Replay the kernel on the CPU and compare against the reference palette, for every clip.
        //
        // Every clip, not just the first, is the point. A table error that scales with the clip's base offset - the track
        // plane's row origin, which is zero exactly for clip 0 - is invisible to a probe that never leaves that clip, and a
        // bake that is right about one clip out of twenty is not "nearly right", it is wrong for nineteen of them.
        //
        // Two kinds of probe, measured separately because they answer different questions. A time that lands on a baked key
        // (the first and the last) must reproduce the reference to float precision: at a == 0 the kernel reads the very value
        // the sampler produced, so anything above rounding is a table defect. A time halfway between two keys is the one place
        // the two routes are allowed to disagree - the kernel lerps between 60 Hz samples while the reference interpolates the
        // original curve - so its residual is reported but never treated as a failure.
        var player = asset._animationPlayer;
        bool savedLoop = player.Loop;
        float worst = 0f;           // on-key: a real defect if large
        float worstBetween = 0f;    // mid-key: the documented resampling residual
        int worstJoint = -1;
        int worstClip = -1;
        float worstProbe = -1f;
        var scratch = new Matrix4x4[bake.JointCount];
        try
        {
            player.Loop = false;
            for (int clip = 0; clip < bake.ClipCount; clip++)
            for (int phase = 0; phase < 3; phase++)
            {
                float duration = bake.ClipDurations[clip];
                float probeTime = phase == 0 ? 0f : phase == 1 ? duration : duration * 0.5f;
                bool onKey = phase != 2;
                player.Evaluate(-1, 0f, nodes);          // the reference resets to rest before every sample
                player.Evaluate(clip, probeTime, nodes);
                player.UpdateBoneMatrices(skins);
                var reference = player.GetBoneMatricesArray();

                for (int j = 0; j < bake.JointCount; j++)
                {
                    int jb = j * jointFloats;
                    int paletteIndex = (int)BitConverter.SingleToUInt32Bits(jt[jb + 0]);
                    int tb = (clip * bake.JointCount + j) * trackUints;
                    uint frameCount = tt[tb + 0];

                    var rt = new Vector3(jt[jb + 4], jt[jb + 5], jt[jb + 6]);
                    var rq = new Quaternion(jt[jb + 8], jt[jb + 9], jt[jb + 10], jt[jb + 11]);
                    var rs = new Vector3(jt[jb + 12], jt[jb + 13], jt[jb + 14]);
                    if (frameCount != 0)
                    {
                        uint firstRow = tt[tb + 1];
                        float rate = BitConverter.UInt32BitsToSingle(tt[tb + 2]);
                        float ft = probeTime * rate;
                        int f0 = Math.Min((int)ft, (int)frameCount - 1);
                        int f1 = Math.Min(f0 + 1, (int)frameCount - 1);
                        float a = ft - f0;
                        int i0 = (int)(firstRow + (uint)f0 * (uint)bake.JointCount + (uint)j) * keyFloats;
                        int i1 = (int)(firstRow + (uint)f1 * (uint)bake.JointCount + (uint)j) * keyFloats;
                        var tr = bake._tracksFloat;
                        rt = Vector3.Lerp(new(tr[i0], tr[i0 + 1], tr[i0 + 2]), new(tr[i1], tr[i1 + 1], tr[i1 + 2]), a);
                        rs = Vector3.Lerp(new(tr[i0 + 8], tr[i0 + 9], tr[i0 + 10]), new(tr[i1 + 8], tr[i1 + 9], tr[i1 + 10]), a);
                        rq = Quaternion.Slerp(new(tr[i0 + 4], tr[i0 + 5], tr[i0 + 6], tr[i0 + 7]),
                                              new(tr[i1 + 4], tr[i1 + 5], tr[i1 + 6], tr[i1 + 7]), a);
                    }

                    var local = LocalOf(rs, rq, rt);
                    var chain = ReadMatrix(jt, jb + 16);
                    int porder = (int)BitConverter.SingleToUInt32Bits(jt[jb + 1]);
                    var parent = porder >= 0 ? chain * scratch[porder] : chain;
                    var world = local * parent;
                    scratch[j] = world;

                    var bone = Matrix4x4.Transpose(ReadMatrix(jt, jb + 32) * world * ReadMatrix(jt, jb + 48));

                    float d = MaxDelta(bone, reference[paletteIndex]);
                    if (!onKey)
                    {
                        if (d > worstBetween) worstBetween = d;
                        continue;
                    }
                    if (d > worst)
                    {
                        worst = d;
                        worstJoint = j;
                        worstClip = clip;
                        worstProbe = probeTime;
                    }
                }
            }
        }
        finally
        {
            player.Loop = savedLoop;
            player.Evaluate(-1, 0f, nodes);
            RestoreInitialPose(nodes, parentOf);
        }

        if (worst > 1e-3f)
        {
            string joint = worstJoint >= 0 && worstJoint < order.Count ? order[worstJoint].Name : "?";
            log?.AddLog(LogType.Error,
                $"[Bake] kernel replay differs from the reference palette by {worst:F4} at joint {worstJoint} ('{joint}'), clip {worstClip}, t={worstProbe:F3}");
        }
        else
        {
            log?.AddLog(LogType.Error, $"[Bake] tables verified against the reference palette (worst {worst:E2} over {bake.ClipCount} clips, {worstBetween:E2} between keys, {orderViolations} order issues)");
        }
    }

    static Matrix4x4 ReadMatrix(Span<float> src, int offset) => new(
        src[offset], src[offset + 1], src[offset + 2], src[offset + 3],
        src[offset + 4], src[offset + 5], src[offset + 6], src[offset + 7],
        src[offset + 8], src[offset + 9], src[offset + 10], src[offset + 11],
        src[offset + 12], src[offset + 13], src[offset + 14], src[offset + 15]);

    static float MaxDelta(in Matrix4x4 a, in Matrix4x4 b)
    {
        Span<float> x = stackalloc float[16];
        x[0] = a.M11; x[1] = a.M12; x[2] = a.M13; x[3] = a.M14;
        x[4] = a.M21; x[5] = a.M22; x[6] = a.M23; x[7] = a.M24;
        x[8] = a.M31; x[9] = a.M32; x[10] = a.M33; x[11] = a.M34;
        x[12] = a.M41; x[13] = a.M42; x[14] = a.M43; x[15] = a.M44;

        Span<float> y = stackalloc float[16];
        y[0] = b.M11; y[1] = b.M12; y[2] = b.M13; y[3] = b.M14;
        y[4] = b.M21; y[5] = b.M22; y[6] = b.M23; y[7] = b.M24;
        y[8] = b.M31; y[9] = b.M32; y[10] = b.M33; y[11] = b.M34;
        y[12] = b.M41; y[13] = b.M42; y[14] = b.M43; y[15] = b.M44;

        float max = 0f;
        for (int i = 0; i < 16; i++)
        {
            float d = MathF.Abs(x[i] - y[i]);
            if (d > max) max = d;
        }
        return max;
    }
#endif

    /// <summary>
    /// Makes the three immutable tables resident. Called from <see cref="IInstanceSkinningTarget.PrepareSkinningFrame"/>, which runs
    /// inside the FrameStart effect's Record: that is the first moment the command list is open and outside a Pass, and therefore
    /// the first moment an upload of this size can be recorded safely on all four backends.
    /// </summary>
    public bool EnsureUploaded(IGraphics g)
    {
        if (Uploaded) return true;
        if (g == null) return false;

        try
        {
            _jointBuffer ??= g.CreateStorageBuffer((uint)_jointTable.Length);
            _trackBuffer ??= g.CreateStorageBuffer((uint)_trackTable.Length);
            _tracksBuffer ??= g.CreateStorageBuffer((uint)_tracks.Length);
            if (_jointBuffer == null || _trackBuffer == null || _tracksBuffer == null) return false;

            // Order matters only for readability: all three are read by the same dispatch, and the backend orders the uploads
            // before it records the kernel.
            if (!g.TryUpdateStorageBuffer(_jointBuffer, _jointTable)) return false;
            if (!g.TryUpdateStorageBuffer(_trackBuffer, _trackTable)) return false;
            if (!g.TryUpdateStorageBuffer(_tracksBuffer, _tracks)) return false;
            Uploaded = true;
            return true;
        }
        catch (Exception ex)
        {
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[InstanceSkinning] Bake upload failed: {ex.Message}");
            return false;
        }
    }

    public StorageBuffer? JointTable => Uploaded ? _jointBuffer : null;

    public StorageBuffer? TrackTable => Uploaded ? _trackBuffer : null;

    public StorageBuffer? Tracks => Uploaded ? _tracksBuffer : null;

    /// <summary>Releases the three resident tables. Only the backend that owns the group calls this, and only after the group has
    /// stopped being submitted.</summary>
    public void Dispose()
    {
        _jointBuffer?.Dispose();
        _trackBuffer?.Dispose();
        _tracksBuffer?.Dispose();
        _jointBuffer = null;
        _trackBuffer = null;
        _tracksBuffer = null;
        Uploaded = false;
    }

    /// <summary>
    /// Normalizes a playback time the same way <c>GLTFAnimationPlayer.Evaluate</c> does, so the kernel receives a time that is
    /// already inside [0, duration] and never has to know about looping. Shared by the four backends rather than reimplemented in
    /// each, because a mismatch here is a silent one-frame stutter rather than an error.
    /// </summary>
    public float NormalizeTime(int clip, float time, bool loop)
    {
        if (clip < 0 || clip >= ClipDurations.Length) return 0f;
        float duration = ClipDurations[clip];
        if (duration <= 0f) return 0f;
        if (!loop) return Math.Clamp(time, 0f, duration);
        float wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }

    static Matrix4x4 LocalOf(Vector3 scale, Quaternion rotation, Vector3 translation)
        => Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

    /// <summary>Writes a row-major <see cref="Matrix4x4"/> verbatim. Every dialect reads the same bytes; what differs is only which
    /// index a matrix turns into (row in HLSL, column in GLSL/MSL/WGSL), which the kernel sources account for.</summary>
    static void WriteMatrix(Span<float> dst, int offset, in Matrix4x4 m)
    {
        dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
        dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
        dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
        dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
    }

    /// <summary>Puts every node back where loading left it. The bake evaluates thousands of frames through the shared node tree;
    /// leaving the last one in place would leak into picking, into the per-primitive streams and into the CPU route's own rest pose.</summary>
    static void RestoreInitialPose(List<GltfNodeBase> nodes, Dictionary<GltfNodeBase, GltfNodeBase> parentOf)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;
            node.Translation = node.InitialTranslation;
            node.Rotation = node.InitialRotation;
            node.Scale = node.InitialScale;
            if (node.InitialWeights.Length == 0)
            {
                node.Weights = Array.Empty<float>();
            }
            else if (node.Weights.Length != node.InitialWeights.Length)
            {
                node.Weights = (float[])node.InitialWeights.Clone();
            }
            else
            {
                Array.Copy(node.InitialWeights, node.Weights, node.InitialWeights.Length);
            }
            node.WeightsVersion = 0;
        }

        // Same shape as the rest-world build above, and for the same reason: the node list is in file order, which glTF does
        // not require to be parent-first. A forward pass here would compose a child against a parent whose WorldTransform has
        // not been rewritten yet, i.e. against the last frame the bake happened to sample - so the tree would be left in a
        // pose that is neither the rest pose nor any baked key, and every consumer of WorldTransform (picking, the
        // per-primitive streams, a later CPU-route replay) would read it.
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;

            var world = node.LocalTransform;
            var cursor = parentOf.TryGetValue(node, out var parent) ? parent : null;
            while (cursor != null)
            {
                world = world * cursor.LocalTransform;
                cursor = parentOf.TryGetValue(cursor, out var up) ? up : null;
            }
            node.WorldTransform = world;
        }
    }
}
