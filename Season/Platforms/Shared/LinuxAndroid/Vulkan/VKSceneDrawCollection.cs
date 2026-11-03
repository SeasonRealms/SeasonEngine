// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>Three draw command forms: 3D primitive, sprite quad, text batch. All three go through a descriptor set + vertex buffer.</summary>
internal enum VKSceneDrawKind : byte
{
    Primitive,
    Quad,
    Text,
}

/// <summary>
/// A single captured draw item. It only stores immutable handles (descriptor set, vertex/index/instance buffers, counts),
/// and does not store the control, nor the delegate for the deferred <c>Control.Draw</c> call.
///
/// The only difference from the DX version: VK's rotating ring is inherently exclusive to an "owner + frame slot"
/// (<c>PrimitiveData.MatrixBuffers[fi]</c>, <c>VKSpriteQuad._matrixBuffers[fi]</c>,
/// text each has its own per-frame-slot instance/parameter buffer), and the direct draw path only hands the handles to the command buffer,
/// no one within the frame overwrites another's slot, so here we do not need the DX-style deep copy of upload page bytes.
/// </summary>
internal unsafe struct VKSceneDrawCommand : ISceneDrawCommand
{
    internal VKSceneDrawKind Kind;
    internal PipelineMode Mode;
    internal CullModeFlags Cull;
    internal bool DepthWrite;

    // Primitive
    internal PrimitiveData? Primitive;

    // Shared by all three: descriptor set + vertex/index/instance buffer
    internal DescriptorSet Set;
    // View set 2 (main view frame slot set or screen space set), determined at collection time.
    internal DescriptorSet View;
    internal VkBuffer Vertex;
    internal VkBuffer Index;
    internal VkBuffer Instances;
    internal IndexType IndexKind;
    internal uint IndexCount;
    internal uint InstanceCount;
    internal uint FirstInstance;
    // A7 main view: the GPU-written args replace IndexCount/InstanceCount/FirstInstance, and InstanceOffset selects the
    // main-view segment of the reordered stream. Only handles are stored, as for every other field: the args and the
    // stream belong to the group pinned through RetainOwner.
    internal VkBuffer IndirectArgs;
    internal ulong IndirectOffset;
    internal ulong InstanceOffset;

    public void Draw()
    {
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetPipeline(cmd, Mode, Cull, DepthWrite);

        switch (Kind)
        {
            case VKSceneDrawKind.Primitive:
                // Texture readiness and descriptor refresh are already done in the collection phase:
                // those operations include layout transitions and can only be called outside the render pass.
                Pipeline.DrawPrimitiveCore(cmd, Primitive!, Vertex, Index, Set, IndexCount,
                    Instances, InstanceCount, FirstInstance, prepared: true, View,
                    IndirectArgs, IndirectOffset, InstanceOffset);
                break;

            case VKSceneDrawKind.Quad:
            {
                var vb = Vertex;
                ulong offset = 0;
                Device.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);
                var set = Set;
                Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Pipeline.PipelineLayout, 0, 1, &set, 0, null);
                Pipeline.BindViewSet(cmd, View);
                if (Pipeline.SceneFogSamplingBaked && Mode != PipelineMode.Opaque)
                {
                    Pipeline.PushSceneFog(cmd);
                    Pipeline.BindFogSet(cmd);
                }
                Device.Vk.CmdDraw(cmd, 6, 1, 0, 0);
                break;
            }

            case VKSceneDrawKind.Text:
            {
                var vb0 = Vertex;
                var vb1 = Instances;
                var vertexBuffers = stackalloc VkBuffer[2] { vb0, vb1 };
                var offsets = stackalloc ulong[2] { 0, 0 };
                Device.Vk.CmdBindVertexBuffers(cmd, 0, 2, vertexBuffers, offsets);
                Device.Vk.CmdBindIndexBuffer(cmd, Index, 0, IndexKind);
                var set = Set;
                Device.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, Pipeline.PipelineLayout, 0, 1, &set, 0, null);
                Pipeline.BindViewSet(cmd, View);
                if (Pipeline.SceneFogSamplingBaked && Mode != PipelineMode.Opaque)
                {
                    Pipeline.PushSceneFog(cmd);
                    Pipeline.BindFogSet(cmd);
                }
                Device.Vk.CmdDrawIndexed(cmd, 6, InstanceCount, 0, 0, 0);
                break;
            }
        }
    }

    // The resource references are held by the snapshot page of <see cref="VKSceneDrawCollection"/> until the GPU retires this frame.
    public void Release() { }
}

/// <summary>
/// Vulkan main-thread scene collector. No draws are submitted during collection:
/// <c>Pipeline.SetPipeline</c> only records mode/cull/depth write, and the actual PSO binding happens at replay time
/// (only the replaying pass knows which PSO family to use: main scene, velocity MRT, Overlay, OutlineMask).
/// </summary>
internal sealed unsafe class VKSceneDrawCollection
{
    readonly SceneDrawQueue<VKSceneDrawCommand> _queue = new();
    readonly VKDrawSnapshotArena _arena = new();

    ulong _stamp;
    bool _live;

    /// <summary>The collection being collected; null outside the collection period, and replay will not be captured by itself again.</summary>
    internal static VKSceneDrawCollection? Active { get; private set; }

    // Primitives actually drawn this frame/last frame, deduplicated by reference.
    // binding 16-20 are only safe to rewrite before the command buffer begins (otherwise the validation layer judges the whole command buffer as invalid),
    // but scene collection happens after begin, so the frame-start pre-refresh can only be based on the previous frame's draw collection.
    // See Pipeline.RefreshSceneBindingsForFrameStart.
    static HashSet<PrimitiveData> _drawnThisFrame = new();
    static HashSet<PrimitiveData> _drawnLastFrame = new();
    static HashSet<VKSpriteQuad> _quadThisFrame = new();
    static HashSet<VKSpriteQuad> _quadLastFrame = new();

    /// <summary>The set of primitives actually drawn last frame, for the frame-start pre-refresh to iterate.</summary>
    internal static IReadOnlyCollection<PrimitiveData> PreviousFramePrimitives => _drawnLastFrame;

    /// <summary>The set of sprites actually drawn last frame, also for the frame-start pre-refresh to iterate.</summary>
    internal static IReadOnlyCollection<VKSpriteQuad> PreviousFrameQuads => _quadLastFrame;

    /// <summary>
    /// Records a primitive rendered this frame, as the basis for the next frame's frame-start pre-refresh.
    /// Both the main pass collection and the shadow pass call it: the shadow's draw collection is its own culling result and may not be fully covered by the main pass,
    /// while both refresh the same set of descriptor sets. Calling it outside the collection period is also safe.
    /// </summary>
    internal static void NotePrimitiveDrawn(PrimitiveData primitive) => _drawnThisFrame.Add(primitive);

    /// <summary>Records a sprite drawn this frame, with the same purpose as <see cref="NotePrimitiveDrawn"/>.</summary>
    internal static void NoteQuadDrawn(VKSpriteQuad quad) => _quadThisFrame.Add(quad);

    // The fog sampling switch only logs one line when the state flips, making it convenient to confirm on a real device "whether the transparent phase actually samples fog".
    static bool? _reportedFog;

    /// <summary>Current state: the pipeline state recorded by the most recent SetPipeline.</summary>
    internal VKSceneDrawCommand State;

    internal int OpaqueCount => _queue.OpaqueCount;

    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (_live || Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("Scene collection must be opened by the frame scheduler, and the previous frame must have been released.");
        _queue.BeginCollection();
        _stamp = Device.GetCurrentRetireFenceValue();
        _arena.BeginFrame(_stamp);
        _live = true;
        State = default;
        Active = this;
    }

    internal void Seal()
    {
        Validate();
        if (Active != this) throw new InvalidOperationException("Scene collection has been sealed.");
        _queue.Seal();
        Active = null;
    }

    void Validate()
    {
        if (!_live || _stamp != Device.GetCurrentRetireFenceValue())
            throw new InvalidOperationException("Scene draw items can only be consumed in the same frame as the collection.");
    }

    internal void DrawOpaque()
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneOpaque)
            throw new InvalidOperationException("The opaque queue's pass is invalid.");
        // The opaque variant has no fog sampling code, but SceneFogParams is cross-frame static state, so it must be explicitly zeroed.
        Pipeline.SceneFogParams = default;
        _queue.DrawOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneTransparent)
            throw new InvalidOperationException("The transparent queue's pass is invalid.");

        bool fog = false;
        if (sampleFog && Pipeline.SceneFogSamplingBaked
            && VKFogCompositePipeline.TryGetTransparentBinding(out var frame, out var frameRange,
                out var integrated, out var local))
        {
            // The fog volumetric texture is a resource produced only in this frame's composite phase, its handle is not available during collection,
            // so the three-segment binding can only be completed inside the transparent pass.
            // What is written is an independent set 1 (one per frame slot), not the draw item's own set 0:
            // set 0 has already been bound and used in this frame's earlier shadow pass,
            // and a set that does not declare UPDATE_AFTER_BIND will invalidate the whole buffer if rewritten after being referenced by the command buffer currently being recorded.
            // The order is still "finish writing first, then start drawing".
            Pipeline.PrepareTransparentFog(frame, frameRange, integrated, local);
            fog = true;
        }

        // S5: fog transparent bypass -- when this view has no fog, fogParams is all zeros and weather is turned off accordingly,
        // no longer checking the independent FrameSchedule.WeatherComposited bit (to avoid a secondary view mistakenly sampling the main view's weather residue).
        // The beam brightness is supplied independently on the shader side by S1 (emissiveFactor × alpha), independent of fogParams.
        Pipeline.SceneFogParams = new Vector4(fog ? 1 : 0, fog && FrameSchedule.WeatherComposited ? 1 : 0, 0, 0);
        var app = DeviceServices.BaseApp;
        if (app != null && _reportedFog != fog)
        {
            _reportedFog = fog;
            app.AddLog(Season.Basic.LogType.Backend,
                $"{DateTime.UtcNow} [VK Scene] transparent-phase fog sampling={(fog ? "on" : "off")} " +
                $"transparent={_queue.TransparentCount} baked={Pipeline.SceneFogSamplingBaked} requested={sampleFog}");
        }
        try { _queue.DrawTransparent(); }
        finally
        {
            Pipeline.SceneFogParams = default;
            // set 1 returns to the default set: the direct draw path cannot keep binding this frame's fog resources, they will be rewritten next frame.
            Pipeline.CurrentFogSet = default;
        }
    }

    internal void Release()
    {
        if (Active == this) Active = null;
        try { _queue.Release(); }
        finally
        {
            _live = false; State = default;
            // Swap rather than copy: this frame's draw collection becomes the basis for the next frame's frame-start pre-refresh.
            (_drawnThisFrame, _drawnLastFrame) = (_drawnLastFrame, _drawnThisFrame);
            _drawnThisFrame.Clear();
            (_quadThisFrame, _quadLastFrame) = (_quadLastFrame, _quadThisFrame);
            _quadThisFrame.Clear();
        }
    }

    /// <summary>Records the state requested by this SetPipeline, for reselecting the PSO at replay time.</summary>
    internal void RecordPipeline(PipelineMode mode, CullModeFlags cull, bool depthWrite)
        => State = new VKSceneDrawCommand { Kind = VKSceneDrawKind.Primitive, Mode = mode, Cull = cull, DepthWrite = depthWrite };

    /// <summary>
    /// Pins an owner still referenced by the command buffer during replay (primitive group / sprite / text state).
    /// The shared hull template and instance buffer are both attached to the group, so pinning the group covers them.
    /// It is a no-op outside the collection period.
    /// </summary>
    internal static void RetainOwner(object owner) => Active?._arena.Retain(owner);

    /// <summary>The projection distance along the camera forward, with the same formula as DX's <c>DXSceneDrawCollection.Depth</c>.</summary>
    internal static float Depth(Vector3 center, Matrix4x4 world)
        => RenderView.SortDepth(Vector3.Transform(center, world));

    /// <summary>Restores the four row-major rows of the instance stream into a world matrix, for transparent sorting to compute depth.</summary>
    internal static Matrix4x4 InstanceWorld(in InstanceTransformData instance) => new(
        instance.Row0.X, instance.Row0.Y, instance.Row0.Z, instance.Row0.W,
        instance.Row1.X, instance.Row1.Y, instance.Row1.Z, instance.Row1.W,
        instance.Row2.X, instance.Row2.Y, instance.Row2.Z, instance.Row2.W,
        instance.Row3.X, instance.Row3.Y, instance.Row3.Z, instance.Row3.W);

    /// <summary>Reads the world matrix from the primitive matrix UBO (it was transposed when stored, so it is transposed back here).</summary>
    static Matrix4x4 PrimitiveWorld(PrimitiveData primitive, int frameIndex)
    {
        var mapped = primitive.MappedMatrixBuffers[frameIndex];
        if (mapped == null) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(Unsafe.Read<MatrixBuffer>(mapped).World);
    }

    internal void CapturePrimitive(CommandBuffer cmd, PrimitiveData primitive, VkBuffer vertex, VkBuffer index,
        DescriptorSet set, uint indexCount, VkBuffer instanceBuffer, uint instanceCount, uint firstInstance,
        float sortDepth, VkBuffer indirectArgs = default, ulong indirectOffset = 0, ulong instanceOffset = 0)
    {
        // An indirect draw's instance count is only known on the GPU, so a zero CPU count does not mean an empty draw.
        bool indirect = indirectArgs.Handle != 0;
        if (indexCount == 0 || (instanceCount == 0 && !indirect)) return;

        // Texture readiness and the refresh of bindings 16-20 must be done outside the pass, see the notes on PreparePrimitive.
        Pipeline.PreparePrimitive(cmd, primitive, set);
        _arena.Retain(primitive);
        _drawnThisFrame.Add(primitive);

        var draw = State;
        draw.Kind = VKSceneDrawKind.Primitive;
        draw.Primitive = primitive;
        draw.Set = set;
        // The view slot was already chosen by FrameSchedule.PrepareView during collection, so what is recorded here is this view's set 2.
        draw.View = Pipeline.ActiveViewSet;
        draw.Vertex = vertex;
        draw.Index = index;
        draw.Instances = instanceBuffer;
        draw.IndexKind = primitive.Use32BitIndices ? IndexType.Uint32 : IndexType.Uint16;
        draw.IndexCount = indexCount;
        draw.IndirectArgs = indirectArgs;
        draw.IndirectOffset = indirectOffset;
        draw.InstanceOffset = instanceOffset;

        if (!float.IsNaN(sortDepth))
        {
            draw.InstanceCount = instanceCount;
            draw.FirstInstance = firstInstance;
            _queue.Add(draw, draw.Mode != PipelineMode.Opaque, FrameSchedule.SceneDrawOrder.Current,
                sortDepth);
            return;
        }

        // Instance split: the transparent phase must draw per instance so that the two instances in front and behind blend colors correctly;
        // the opaque phase's whole-batch instanced draw stays as is (drawing the same instance repeatedly is meaningless).
        // An indirect draw is never split: its instances are only known on the GPU. The caller keeps it opaque, so the
        // depth below is the same primitive-level depth the direct opaque draw gets and the opaque queue order is unchanged.
        int frameIndex = (int)Device.FrameIndex;
        var world = PrimitiveWorld(primitive, frameIndex);
        bool split = instanceBuffer.Handle != 0 && draw.Mode != PipelineMode.Opaque && !indirect;
        uint slices = split ? instanceCount : 1;
        for (uint i = 0; i < slices; i++)
        {
            draw.InstanceCount = split ? 1u : instanceCount;
            draw.FirstInstance = firstInstance + i;
            // The per-instance depth on the split path needs the instance matrix, but the instance stream is a GPU-side resource,
            // so it is given if the caller can give it (the sortDepth parameter), otherwise it falls back to primitive-level depth:
            // items at the same depth are stably sorted by the queue in insertion order, and the caller's traversal order is itself already sorted.
            _queue.Add(draw, draw.Mode != PipelineMode.Opaque, FrameSchedule.SceneDrawOrder.Current,
                Depth(primitive.LocalBoundsCenter, world));
        }
    }

    internal void CaptureQuad(DescriptorSet set, VkBuffer vertex, Matrix4x4 world, bool screenSpace)
    {
        var draw = new VKSceneDrawCommand
        {
            Kind = VKSceneDrawKind.Quad,
            Mode = PipelineMode.Transparent,
            Cull = CullModeFlags.BackBit,
            // The Transparent variant does not write depth itself, consistent with DX keeping it zero at capture time.
            DepthWrite = false,
            Set = set,
            View = screenSpace ? Pipeline.ScreenViewSet : Pipeline.ActiveViewSet,
            Vertex = vertex,
            IndexCount = 6,
            InstanceCount = 1,
        };
        _queue.Add(draw, transparent: true, FrameSchedule.SceneDrawOrder.Current, Depth(Vector3.Zero, world));
    }

    internal void CaptureText(DescriptorSet set, VkBuffer vertex, VkBuffer index, VkBuffer instances,
        uint instanceCount, float sortDepth)
    {
        if (instanceCount == 0) return;
        var draw = new VKSceneDrawCommand
        {
            Kind = VKSceneDrawKind.Text,
            Mode = PipelineMode.Transparent,
            Cull = CullModeFlags.None,
            DepthWrite = false,
            Set = set,
            View = Pipeline.ScreenViewSet,
            Vertex = vertex,
            Index = index,
            Instances = instances,
            IndexKind = IndexType.Uint16,
            IndexCount = 6,
            InstanceCount = instanceCount,
        };
        _queue.Add(draw, transparent: true, FrameSchedule.SceneDrawOrder.Current, sortDepth);
    }
}
