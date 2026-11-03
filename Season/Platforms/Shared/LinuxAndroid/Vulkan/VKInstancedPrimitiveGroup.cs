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
/// Base class for GPU-instanced primitive groups (Vulkan).
/// Unifies instance-buffer management, transparent sorting,
/// and three-bucket grouped drawing.
/// Derived classes: VKInstancedModel (glTF template cloning) /
/// VKInstancedMesh3D (code-generated geometry).
/// </summary>
internal unsafe abstract class VKInstancedPrimitiveGroup : VKPrimitiveGroup, Season.Rendering.IInstanceCullTarget
{
    protected readonly List<PrimitiveData> _primitives = new();
    protected readonly List<PrimitiveData> _opaquePrimitives = new();
    protected readonly List<PrimitiveData> _transparentPrimitives = new();
    protected readonly List<int> _transparentInstanceOrder = new();

    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    protected BufferResource _instanceBuffer;
    protected int _instanceCapacity;
    protected int _instanceCount;

    /// <summary>Whether any instance enabled Wireframe highlighting in this frame
    /// (written during Update and used as a zero-cost gate during Draw;
    /// reset at the start of each derived Update).</summary>
    protected bool _wireframeActive;

    // Phase 4: per-instance Outline2D collection
    // (host activation uses the full mask and ignores the per-instance list)

    /// <summary>Compressed writeIndex slot list for instances that enabled Outline2D in this frame
    /// (rebuilt every Update).</summary>
    protected readonly List<int> _outline2DInstances = new();

    /// <summary>Per-instance outline colors aligned with _outline2DInstances
    /// (rebuilt every frame; the per-slot mask fetches color slot by slot).</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    /// <summary>Outline color/width of the first active instance
    /// (the composited frame color uses the first active instance,
    /// matching the per-instance panel color written by ObjectPicker).</summary>
    protected Vector4 _outline2DInstanceColor;

    /// <summary>Outline width of the first active instance.</summary>
    protected float _outline2DInstanceWidth;

    /// <summary>Host-level (non-instanced) Outline2D activation.
    /// When true, DrawOutlineMask renders the full batch at once.</summary>
    protected bool _outline2DHostActive;

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
    Season.Rendering.StorageBuffer? _cullPrimInfo;
    int _cullCapacity = -1;
    /// <summary>Number of primitive-info entries already uploaded to the GPU. When it differs from <see cref="_cullPrimInfoPending"/>, this frame must re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>CPU-side prebuilt primitive-info bytes: building can happen at any time, but the upload
    /// must wait until the command list enters the recording state
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>Number of CPU-side primitive-info entries; -1 means it has never been built.</summary>
    int _cullPrimInfoPending = -1;
    bool _cullRegistered;
    bool _cullDrawReady;
    /// <summary>The owner's effective draw path is <see cref="Season.Controls.InstanceDrawPath.Indirect"/>, latched in Update so the cull dispatch and the draw see the same answer.</summary>
    bool _cullMainView;
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;

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
    /// The index range the indirect args must replay for <paramref name="primitive"/>. The reset kernel writes it into every slot's
    /// args and the indirect draw no longer passes a range of its own, so it must come from the same source as the direct draw's
    /// vkCmdDrawIndexed arguments -- a group drawing a sub-range of a shared index buffer overrides this, otherwise its indirect
    /// draws would replay the whole buffer from index 0. The default mirrors the indexCount every direct call site passes.
    /// </summary>
    protected virtual void GetCullPrimitiveRange(PrimitiveData primitive, out uint indexCount, out uint startIndex, out int baseVertex)
    {
        indexCount = (uint)primitive.Indices.Length;
        startIndex = 0u;
        baseVertex = 0;
    }

    /// <summary>
    /// Override the cull box with a caller-computed one. A glTF model must: its primitives' local boxes live in their own node spaces
    /// and ignore animation, while the kernel transforms the box by the root instance world, so the box has to be in model space and
    /// cover every pose the clips can reach (see <see cref="Season.Controls.InstancedModel.TemplateCullBounds"/>).
    /// </summary>
    protected void SetCullLocalBounds(Season.Rendering.Bounds3D bounds)
    {
        _cullLocalCenter = bounds.Center;
        _cullLocalExtents = bounds.Extents;
    }

    protected void RebuildPrimitiveBuckets()
    {
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        foreach (var primitive in _primitives)
        {
            if (primitive.IsTransparent)
                _transparentPrimitives.Add(primitive);
            else
                _opaquePrimitives.Add(primitive);
        }

        UpdateCullLocalBounds();
    }

    /// <summary>
    /// Merge the local bounding boxes of all primitives into a group-level combined box. The cull
    /// kernel takes only one box, so passing a single surface's box would wrongly cull the
    /// instances of the other surfaces in one batch when they fall outside it.
    /// </summary>
    void UpdateCullLocalBounds()
    {
        if (_primitives.Count == 0)
        {
            _cullLocalCenter = Vector3.Zero;
            _cullLocalExtents = Vector3.Zero;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var primitive in _primitives)
        {
            min = Vector3.Min(min, primitive.LocalBoundsCenter - primitive.LocalBoundsExtents);
            max = Vector3.Max(max, primitive.LocalBoundsCenter + primitive.LocalBoundsExtents);
        }

        _cullLocalCenter = (min + max) * 0.5f;
        _cullLocalExtents = (max - min) * 0.5f;
    }

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceBuffer.Buffer.Handle != 0 && _instanceCapacity == count)
            return;

        if (_instanceBuffer.Buffer.Handle != 0)
        {
            // Never destroy a vertex buffer that a frame in flight may still reference. A capacity change happens at most
            // one frame after the previous one, so the old buffer is still bound by pending command buffers; destroying it
            // here is VUID-vkDestroyBuffer-buffer-00922, and on Android tilers it can read freed pages.
            var retired = _instanceBuffer;
            Device.EnqueueDeferredRelease(() => Device.ResourceManager.DestroyBuffer(retired));
        }

        _instanceBuffer = Device.ResourceManager.CreateVertexBuffer<InstanceTransformData>((uint)count);
        _instanceCapacity = count;
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        // Pin this group on the snapshot page during collection: during replay the instance buffer and the primitive descriptor sets are still referenced.
        VKSceneDrawCollection.RetainOwner(this);

        var cmd = Device.GraphicsCommandBuffer;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = (int)Device.FrameIndex;

        // 1. Opaque
        // Use the Opaque PSO when overall Alpha >= 1, otherwise use Fade
        // A7 main view: the opaque body reads the culled, compacted main-view segment and takes its instance count from the
        // indirect args. Fade stays direct: the scene collector splits a Fade draw per instance for back-to-front order,
        // which a GPU-sized draw cannot be, and a fading group is about to change visibility anyway.
        if (!forceFadeByAlpha && TryGetMainViewSegment(0, out var sortedBuffer, out ulong segmentOffset, out var argsBuffer))
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var primitive = _opaquePrimitives[i];
                Pipeline.SetPipeline(cmd, PipelineMode.Opaque, primitive.DoubleSided);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    sortedBuffer, 0, 0, float.NaN,
                    argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i),
                    segmentOffset);
                Season.Rendering.InstanceCull.MainViewDraws++;
            }
        }
        else
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var primitive = _opaquePrimitives[i];
                Pipeline.SetPipeline(cmd, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    _instanceBuffer.Buffer, (uint)_instanceCount, 0);
            }
        }

        // 2. Transparent
        // True BLEND materials
        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            BuildTransparentInstanceOrder(primitive);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                // The instance matrices exist only on the CPU side (_instanceWorlds); the GPU-side
                // instance stream cannot be read back. So the transparent sort depth must be supplied
                // here, otherwise collection can only fall back to primitive-level depth.
                float depth = ComputeTransparentDepth(_instanceWorlds[instanceIndex], primitive.LocalBoundsCenter);
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
                    Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                        primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                        _instanceBuffer.Buffer, 1, (uint)instanceIndex, depth);
                }

                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
                Pipeline.DrawPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                    _instanceBuffer.Buffer, 1, (uint)instanceIndex, depth);
            }
        }

        // Unified highlighting: per-instance highlights
        // (Bounds boxes + Wireframe shell boxes; for instances enabled in this frame,
        // transparent faces use 2-pass rendering plus opaque edges,
        // finalized after all surfaces)
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
            DrawShellBoxes(_instanceBuffer);
    }

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
        if (slot < 0 || slot >= Season.Rendering.InstanceCull.MaxSlots)
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
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeforeDraw();

        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;

        // Same contract as VKPrimitiveGroup / VKInstancedModel.DrawShadow: bindings 16-20 must be
        // aligned before the first bind of this frame, otherwise the main pass's PreparePrimitive
        // patches a set already recorded this frame, and sets without UPDATE_AFTER_BIND declared
        // then invalidate the entire command buffer.
        // Instanced groups bypass scene collection and the main pass will not register them either,
        // so here we also hand the drawn primitives to the next frame's frame-start pre-refresh.
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            VKSceneDrawCollection.NotePrimitiveDrawn(primitive);
            Pipeline.RefreshSceneBindings(primitive.DescriptorSets[fi],
                ref primitive.EnvCubeViewVersions[fi],
                ref primitive.DdgiAtlasViewVersions[fi],
                ref primitive.DdgiDepthViewVersions[fi],
                ref primitive.CloudNoiseViewVersions[fi],
                ref primitive.AerialLutViewVersions[fi],
                ref primitive.SkyViewViewVersions[fi]);
        }

        // A7: when this frame's cull chain is ready, the instance stream instead reads this slot's reordered segment, with index count and instance count supplied by the indirect args.
        if (TryGetShadowSegment(0, out var sortedBuffer, out ulong instanceOffset, out var argsBuffer, out int slot))
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var primitive = _opaquePrimitives[i];
                Pipeline.DrawShadowPrimitiveIndirect(cmd, primitive,
                    primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                    primitive.DescriptorSets[fi],
                    sortedBuffer, instanceOffset,
                    argsBuffer, Season.Rendering.InstanceCull.ArgsOffset(slot, i));
            }
            return;
        }

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.DrawShadowPrimitive(cmd, primitive, primitive.VertexBuffer.Buffer, primitive.IndexBuffer.Buffer,
                primitive.DescriptorSets[fi], (uint)primitive.Indices.Length,
                _instanceBuffer.Buffer, (uint)_instanceCount, 0);
        }
    }

    /// <summary>
    /// Outline2D mask: rendered through the instance stream
    /// (host-level activation -> one full batch; per-instance activation -> one draw per instance
    /// with instanceCount = 1 by writeIndex slot).
    /// The base non-instanced DrawOutlineMask renders the instance stream with an identity matrix
    /// (causing missing/misaligned instances), so this must be overridden.
    /// Transparent buckets are skipped, matching the base mask semantics.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        // Write outline color per group through the FS push constant.
        // For multi-color cases in the same frame, use the color fixed during Update
        // (instance color or host color).
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetOutlineMaskColor(cmd, _outline2DColor);

        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var p = _opaquePrimitives[i];
            Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
                p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
            OnBeforeDraw();
            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
                    p.DescriptorSets[fi], (uint)p.Indices.Length,
                    _instanceBuffer.Buffer, (uint)_instanceCount, 0);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor slot by slot
                    // (the per-slot mask fetches color per slot)
                    Pipeline.SetOutlineMaskColor(cmd, _outline2DInstanceColors[k]);
                    Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
                        p.DescriptorSets[fi], (uint)p.Indices.Length,
                        _instanceBuffer.Buffer, 1, (uint)idx);
                }
            }
        }
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

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullPrimitiveInfo => _cullPrimInfo;

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
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
                _cullPrimInfo = g.CreateStorageBuffer(
                    (uint)Season.Rendering.InstanceCull.MaxPrimitives * Season.Rendering.InstanceCull.PrimitiveInfoStride);
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
                || _cullArgs == null || _cullPrimInfo == null)
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
    /// source as the direct draw's, because the reset kernel writes it into the indirect args and the draw no longer passes one.
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullPrimInfo == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // Explicitly record that there is no primitive-info table to upload: otherwise
            // PrepareCullInputs would repeatedly upload the previous bytes, and the entry count
            // would look right while the contents were the stale copy.
            _cullPrimInfoBytes = null;
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
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullPrimInfoBytes == null || _cullPrimInfo == null)
                return false;
            int bytes = _cullPrimInfoPending * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride;
            if (!g.TryUpdateStorageBuffer(_cullPrimInfo, new ReadOnlySpan<byte>(_cullPrimInfoBytes, 0, bytes)))
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
        _cullPrimInfo?.Dispose(); _cullPrimInfo = null;
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimInfoBytes = null;
        _cullPrimInfoPending = -1;
        _cullDrawReady = false;
    }

    protected void BuildTransparentInstanceOrder(PrimitiveData primitive)
    {
        _transparentInstanceOrder.Clear();
        for (int i = 0; i < _instanceCount; i++)
            _transparentInstanceOrder.Add(i);

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(_instanceWorlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(_instanceWorlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    protected static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        return RenderView.SortDepth(Vector3.Transform(localCenter, world));
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

        if (_instanceBuffer.Buffer.Handle != 0)
        {
            Device.ResourceManager.DestroyBuffer(_instanceBuffer);
            _instanceBuffer = default;
        }
    }
}
