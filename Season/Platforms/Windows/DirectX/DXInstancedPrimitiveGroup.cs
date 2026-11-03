// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Common rendering base class for GPU instancing.
/// Manages the instance buffer, shared Matrix CBs, three-bucket draw flow, and
/// transparent sorting.
/// Derived classes (DXInstancedMesh3D / DXInstancedModel) only need to provide
/// the PrimitiveData collection and pass the instance-transform list to Update.
/// </summary>
internal unsafe abstract class DXInstancedPrimitiveGroup : DXPrimitiveGroup, Season.Rendering.IInstanceCullTarget
{
    // _currentAlpha / _transformInitialized / Name / SyncAlpha / CreateMaterialBuffer / WriteMaterialBuffer
    // All inherited from DXPrimitiveGroup and not repeated here.

    protected readonly List<PrimitiveData> _primitives = new();
    protected readonly List<PrimitiveData> _opaquePrimitives = new();
    protected readonly List<PrimitiveData> _transparentPrimitives = new();
    protected readonly List<int> _transparentInstanceOrder = new();

    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    // _wireframeActive = true when any enabled instance has Wireframe on.
    // It gates Draw, so there is no cost when fully disabled.
    // Shell-geometry templates and per-instance shell boxes are built lazily by
    // the base-class highlight pool.
    protected bool _wireframeActive;

    // Outline2D (per-instance activation): list of active-instance writeIndex
    // slots for this frame, rebuilt every frame.
    // When the host is active as a whole (_outline2DHostActive), this list is
    // ignored and the mask is drawn for all instances.
    // For per-instance activation, the composed frame color / width comes from
    // the first active instance (the picker writes panel colors there); host
    // activation uses the host values instead.
    protected readonly List<int> _outline2DInstances = new();
    protected bool _outline2DHostActive;
    protected Vector4 _outline2DInstanceColor;
    protected float _outline2DInstanceWidth;

    /// <summary>Per-instance outline colors aligned with `_outline2DInstances`,
    /// rebuilt every frame. The per-slot mask fetches color by slot.</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    protected ID3D12Resource*[] _matrixBuffers = null!;
    protected byte*[] _mappedMatrixBuffers = null!;

    protected ID3D12Resource* _instanceBuffer;
    protected VertexBufferView _instanceBufferView;
    protected int _instanceCapacity;
    protected int _instanceCount;

    // 2-3 Step C (tier C-a): prev instance-world SB
    // (same capacity as _instanceWorlds, one Matrix4x4 per entry).
    // Each frame, UpdateInstanceData copies the current _instanceWorlds into the
    // mapped prev-SB region before uploading the new frame, so the GPU always
    // holds the previous frame's instance world and the shader can index it by
    // instanceID.
    // Contents are zeroed before the first frame (sentinel _m33==0), and the
    // shader falls back to the current worldMatrix.
    protected ID3D12Resource* _prevInstanceWorldBuffer;
    protected byte* _mappedPrevInstanceWorldBuffer;
    protected GpuDescriptorHandle _prevInstanceWorldSrvHandle;
    protected int _prevInstanceWorldDescriptorId = -1;
    protected int _prevInstanceWorldCapacity;

    // ============================================================
    // A7 per-instance GPU culling
    // ============================================================
    // The five buffers are allocated and released together; capacity is aligned to 64 and only grows. When all
    // are null this group does not participate in A7, and shadows fall back to whole-batch CPU culling rather
    // than drawing with empty buffers.
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
    /// <summary>Number of parameter-table entries already uploaded to the GPU. When it differs from <see cref="_cullPrimInfoPending"/> this frame must re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>Parameter-table bytes already built on the CPU side. Stored here instead of being uploaded directly in UPDATE because building is not the same as uploading:
    /// building can happen anytime, but uploading must wait until the command list is open (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>Number of CPU-side parameter-table entries; -1 means it has not been built yet.</summary>
    int _cullPrimInfoPending = -1;
    bool _cullRegistered;

    /// <summary>Group-local merged bounding box. The culling kernel uses it to convert instance matrices into world bounding spheres, so a multi-surface group must pass the union.</summary>
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;

    /// <summary>Whether this frame has transitioned the reorder stream and indirect args into a drawable state. Cleared at the start of Update, set by PrepareCullDraw.</summary>
    bool _cullDrawReady;

    /// <summary>The owner's effective draw path is <see cref="InstanceDrawPath.Indirect"/>, latched in Update so the cull dispatch and the draw see the same answer.</summary>
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
    /// The index range the indirect args must replay for <paramref name="primitive"/>. The reset kernel writes it into every slot's
    /// args and the indirect draw no longer passes a range of its own, so it must come from the same source as the direct draw's
    /// DrawIndexedInstanced arguments -- a group drawing a sub-range of a shared index buffer overrides this, otherwise its
    /// indirect draws would replay the whole buffer from index 0. The default mirrors <see cref="Pipeline.DrawPrimitive"/>.
    /// </summary>
    protected virtual void GetCullPrimitiveRange(PrimitiveData primitive, out uint indexCount, out uint startIndex, out int baseVertex)
    {
        indexCount = (uint)primitive.Indices.Length;
        startIndex = 0u;
        baseVertex = 0;
    }

    protected DXInstancedPrimitiveGroup(string name)
    {
        Name = name;
    }

    // ============================================================
    // CollectPrimitives: return from the static _primitives list
    // ============================================================

    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    // ============================================================
    // Must be called by derived classes: create shared Matrix CBs and initialize primitives
    // ============================================================

    protected void CreateSharedMatrixBuffers()
    {
        int n = (int)Device.frameCount;
        _matrixBuffers = new ID3D12Resource*[n];
        _mappedMatrixBuffers = new byte*[n];

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };

        for (int i = 0; i < n; i++)
        {
            _matrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(), out _mappedMatrixBuffers[i]);
            Unsafe.Write(_mappedMatrixBuffers[i], matrices);
        }
    }

    /// <summary>Writes the initial Material CB value into every N-buffered frame.</summary>
    protected static void WriteMaterialBuffer(PrimitiveData primitiveData)
    {
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitiveData.MappedMaterialBuffers[i], primitiveData.MaterialParams);
    }

    /// <summary>Rebuilds the Opaque/Transparent buckets from IsTransparent.</summary>
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
    /// Override the cull box with a caller-computed one. A glTF model must: its primitives' local boxes live in their own node spaces
    /// and ignore animation, while the kernel transforms the box by the root instance world, so the box has to be in model space and
    /// cover every pose the clips can reach (see <see cref="InstancedModel.TemplateCullBounds"/>).
    /// </summary>
    protected void SetCullLocalBounds(Season.Rendering.Bounds3D bounds)
    {
        _cullLocalCenter = bounds.Center;
        _cullLocalExtents = bounds.Extents;
    }

    /// <summary>
    /// Latch this frame's A7 state at the top of Update. The transition state is recomputed each frame: last frame's true must not
    /// keep authorizing this frame's indirect draw.
    /// </summary>
    protected void BeginCullFrame(InstancedMesh3DBase owner)
    {
        _cullDrawReady = false;
        _cullMainView = owner.EffectiveDrawPath == InstanceDrawPath.Indirect;
    }

    /// <summary>
    /// Merge the local bounding boxes of all primitives into a group-level merged box. The culling kernel takes
    /// only one box, so the union is mandatory: passing one surface's box would make the other surfaces' instances
    /// outside the box get wrongly culled in batches.
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

    // ============================================================
    // Instance-buffer management
    // ============================================================

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceBuffer != null && _instanceCapacity == count)
            return;

        if (_instanceBuffer != null)
        {
            _instanceBuffer->Release();
            _instanceBuffer = null;
        }

        _instanceBuffer = Device.CreateVertexBuffer<InstanceTransformData>((uint)count, out _instanceBufferView);
        _instanceCapacity = count;

        // 2-3 Step C: grow/shrink the prev instance-world SB together with the
        // instance buffer (same capacity as _instanceWorlds, one Matrix4x4 per entry)
        EnsurePrevInstanceWorldBufferCapacity(count);
    }

    // 2-3 Step C: create / grow / shrink the prev instance-world SB
    // (upload heap, persistently mapped)
    void EnsurePrevInstanceWorldBufferCapacity(int count)
    {
        if (count <= 0)
            return;
        if (_prevInstanceWorldBuffer != null && _prevInstanceWorldCapacity == count)
            return;

        if (_prevInstanceWorldBuffer != null)
        {
            _prevInstanceWorldBuffer->Unmap(0, null);
            _prevInstanceWorldBuffer->Release();
            _prevInstanceWorldBuffer = null;
            _mappedPrevInstanceWorldBuffer = null;
        }

        ulong bufferSize = (ulong)(count * sizeof(Matrix4x4));
        _prevInstanceWorldBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, bufferSize, ResourceStates.GenericRead);
        void* pData;
        _prevInstanceWorldBuffer->Map(0, null, &pData);
        _mappedPrevInstanceWorldBuffer = (byte*)pData;
        _prevInstanceWorldCapacity = count;

        // First creation: clear to zero for the sentinel semantics
        new Span<byte>(_mappedPrevInstanceWorldBuffer, count * sizeof(Matrix4x4)).Clear();

        if (_prevInstanceWorldDescriptorId < 0)
            _prevInstanceWorldDescriptorId = Device.DescriptorAllocator.Allocate();
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(_prevInstanceWorldDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)count,
                StructureByteStride = (uint)sizeof(Matrix4x4),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(_prevInstanceWorldBuffer, &srvDesc, cpuHandle);
        _prevInstanceWorldSrvHandle = Device.SrvHeapManager.GetGpuHandle(_prevInstanceWorldDescriptorId);
    }

    // ============================================================
    // Per-frame Update: build world matrices from the instance-transform list and upload them
    // ============================================================

    /// <summary>
    /// Takes the host control, the instance-transform list, and the overall
    /// alpha, then builds world matrices, uploads the instance buffer, and
    /// writes the Matrix CB.
    /// Derived classes call this directly from their Update method.
    /// </summary>
    protected void UpdateInstanceData(InstancedMesh3DBase owner, IReadOnlyList<MeshInstanceTransform> instances, float alpha)
    {
        bool wasInitialized = _transformInitialized;
        _wireframeActive = false;
        _instanceCount = 0;
        BeginCullFrame(owner);

        // Unified highlighting: clear this frame's per-instance draw lists
        // (rebuilt every frame; _boundsActive / _wireframeActive are set by the
        // per-instance hooks below)
        _boundsActive = false;
        _boundsBoxDrawList.Clear();
        _shellBoxDrawList.Clear();
        _outline2DInstances.Clear();
        _outline2DInstanceColors.Clear();

        for (int i = 0; i < instances.Count; i++)
        {
            if (!instances[i].Enable)
                continue;
            _instanceCount++;
        }

        if (_instanceCount == 0)
        {
            // No instances: also turn off Outline2D to avoid leaving the last
            // frame's mask active
            _outline2DHostActive = false;
            SetOutline2DState(false, owner.Highlight.OutlineColor, owner.Highlight.OutlineWidth);
            _transformInitialized = true;
            SyncAlpha(alpha);
            return;
        }

        if (_instanceWorlds.Length != _instanceCount)
        {
            _instanceWorlds = new Matrix4x4[_instanceCount];
            _instanceData = new InstanceTransformData[_instanceCount];
        }

        EnsureInstanceBufferCapacity(_instanceCount);

        // 2-3 Step C (tier C-a): before uploading this frame, first copy the
        // current _instanceWorlds (which still holds the previous frame's "current")
        // into the mapped prev-SB region.
        // On the first frame, _instanceWorlds is still all zero
        // (freshly allocated and not yet written), so prev SB stays zero and the
        // sentinel semantics remain correct.
        if (_mappedPrevInstanceWorldBuffer != null && _instanceCount > 0)
        {
            fixed (Matrix4x4* pSrc = _instanceWorlds)
                Unsafe.CopyBlock(_mappedPrevInstanceWorldBuffer, pSrc, (uint)(_instanceCount * sizeof(Matrix4x4)));
        }

        int writeIndex = 0;

        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (!instance.Enable)
                continue;

            // Unified transform convention: route everything through
            // BuildInstanceMatrix (anchor pivot: per-axis scale -> anchor
            // translation -> Rotation -> Position, see InstancedMesh3DBase)
            var world = owner.BuildInstanceMatrix(instance);

            _instanceWorlds[writeIndex] = world;
            var instanceData = InstanceTransformData.FromWorld(world);
            bool instWire = instance.Highlight.Wireframe;
            _wireframeActive |= instWire;

            // Outline2D (per-instance activation): record the writeIndex slot and
            // per-instance outline color (the per-slot mask fetches color by slot).
            // The first active instance also captures the frame-level composed
            // color / width used by the host path and SetOutline2DState.
            if (instance.Highlight.Outline)
            {
                _outline2DInstances.Add(writeIndex);
                _outline2DInstanceColors.Add(instance.Highlight.OutlineColor);
                if (_outline2DInstances.Count == 1)
                {
                    _outline2DInstanceColor = instance.Highlight.OutlineColor;
                    _outline2DInstanceWidth = instance.Highlight.OutlineWidth;
                }
            }
            _instanceData[writeIndex] = instanceData;

            // Unified highlighting (per-instance bounds box): use pooled boxes by
            // compressed writeIndex, growing lazily. The draw list is rebuilt
            // every frame. Box alpha / color are independent of the host's alpha
            // chain. Boxes with near-zero extents (unloaded / degenerate) stay off.
            if (instance.Highlight.Bounds)
            {
                var worldBounds = owner.GetInstanceWorldBoundsRaw(instance);
                if (worldBounds.Extents.LengthSquared() >= 1e-12f)
                {
                    _boundsActive = true;
                    var box = AcquireBoundsBox(writeIndex);
                    WriteHighlightBox(box,
                        Matrix4x4.CreateScale(worldBounds.Extents * 2f)
                        * Matrix4x4.CreateTranslation(worldBounds.Center),
                        instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    _boundsBoxDrawList.Add(writeIndex);
                }
            }

            // Unified highlighting (per-instance wireframe): lazily build shared
            // shell templates and per-instance shell boxes. Matrices are fetched
            // through the instance-stream writeIndex slot, and each instance is
            // drawn separately. Mixed assets draw both shells (rigid + skinned).
            // If neither template is usable (no valid primitives / morph / multiple
            // skins), the box stays null and is not added to the draw list.
            if (instWire)
            {
                EnsureShellGeometry(owner.Highlight.EdgeWidth,
                    MathF.Max(owner.TemplateLocalSize.X, MathF.Max(owner.TemplateLocalSize.Y, owner.TemplateLocalSize.Z)));
                var shellBox = AcquireShellBox(writeIndex);
                var skinnedShellBox = AcquireSkinnedShellBox(writeIndex);
                if (shellBox != null || skinnedShellBox != null)
                {
                    if (shellBox != null)
                        WriteInstanceShell(shellBox, instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    if (skinnedShellBox != null)
                        WriteInstanceShell(skinnedShellBox, instance.Highlight.SurfaceColor, instance.Highlight.EdgeColor);
                    _shellBoxDrawList.Add(writeIndex);
                }
            }
            writeIndex++;
        }

        // Outline2D active = host-active union any-instance-active.
        // Host activation uses the full mask and ignores the per-instance list.
        // Color / width prefer the instance values when any instance is active
        // (the picker writes panel colors there); otherwise they fall back to
        // the host values, matching Mesh3D/Model semantics.
        _outline2DHostActive = owner.Highlight.Outline;
        bool anyInstanceOutline = _outline2DInstances.Count > 0;
        SetOutline2DState(_outline2DHostActive || anyInstanceOutline,
            anyInstanceOutline ? _outline2DInstanceColor : owner.Highlight.OutlineColor,
            anyInstanceOutline ? _outline2DInstanceWidth : owner.Highlight.OutlineWidth);

        Device.SetVertexBuffer(_instanceBuffer, _instanceBufferView, _instanceData);

        // A7: the culling kernel's input is the same bit-for-bit 80-byte matrix stream as the IA instance stream,
        // so reuse this CPU array directly -- no extra copy and no extra bounding-sphere computation (the sphere
        // is computed on the GPU from the matrices and the group-level box). Only resources are prepared here,
        // nothing is uploaded: uploading waits for the Record phase, see the notes on PrepareCullInputs.
        EnsureCullResources(_instanceCount);

        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // 2-3 Step C (tier C-a): the previous instance world now lives in the
            // prev instance-world SB (t9), so b0.PrevWorld stays all zero.
            // The instanced shader path does not read b0 prevWorld.
        };
        Unsafe.Write(_mappedMatrixBuffers[fi], matrices);

        _transformInitialized = true;

        // 2-3 Step C (tier C-a): from the second frame onward, prev
        // instance-world SB contains valid data, so notify the shader that it
        // may read previous instance worlds. The first-frame all-zero sentinel is
        // still guarded by the shader's _m33 check.
        if (wasInitialized)
            SetPrevInstanceWorldReady();

        SyncAlpha(alpha);
    }

    /// <summary>2-3 Step C (tier C-a): once prev instance-world SB contains
    /// valid data, sets MaterialParams.HasPrevInstanceWorld = 1 on all
    /// primitives (writing every N-buffered frame) so the shader starts reading
    /// prev instance-world SB.
    /// Written only on the first call because the value never changes later.
    /// Shell primitives are updated in sync as well (plan risk 2); otherwise the
    /// shell has no trail.</summary>
    protected void SetPrevInstanceWorldReady()
    {
        for (int i = 0; i < _primitives.Count; i++)
        {
            var primitive = _primitives[i];
            if (primitive.MaterialParams.HasPrevInstanceWorld != 0)
                continue;
            primitive.MaterialParams.HasPrevInstanceWorld = 1;
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
        // Synchronize prev flags for shell primitives too: cover both template
        // sets and both instance-box pools, because pooled boxes may have been
        // created before this frame and still carry stale flags.
        SyncShellPrevFlags(hasPrevInstanceWorld: true, hasPrevBones: false, hasPrevMorph: false);
    }

    // SyncAlpha is inherited from DXPrimitiveGroup and adapts automatically
    // through the CollectPrimitives override

    // ============================================================
    // Unified highlighting: per-instance wireframe shell boxes
    // (matrices come from the instance-stream writeIndex slot, drawn per instance)
    // ============================================================

    /// <summary>Unified highlighting for instancing: writes face/edge colors
    /// into the per-instance shell box's own Material CB for the current frame
    /// and records the frame's face alpha.
    /// Matrices do not go through the box CB. Rendering uses the group-level
    /// shared matrix CB (World=Identity, isInstanced=1 so b0 world is ignored)
    /// plus the instance-stream writeIndex slot
    /// (slot1 world matrix + t9 previous world indexed by instance).</summary>
    protected void WriteInstanceShell(HighlightBox box, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = (int)Device.FrameIndex;
        box.Face.MaterialParams.BaseColor = faceColor;
        Unsafe.Write(box.Face.MappedMaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        Unsafe.Write(box.Edges.MappedMaterialBuffers[fi], box.Edges.MaterialParams);
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting for instancing: draws a single per-instance
    /// shell box with instanceCount=1 and a startInstanceLocation slot.
    /// When face alpha (SurfaceColor.W) is &gt; 0, faces are drawn in double-sided
    /// transparent 2-pass mode. When it is 0, the box becomes edge-only and face
    /// rendering is skipped. Edges use the Opaque path (CullNone + depth write).
    /// For skinned shells (IsSkinned=1), SV_InstanceID does not include
    /// StartInstanceLocation, so the slot base must be carried explicitly via
    /// the outlineMaskBoneBase root constant. The call order must be
    /// SetPipeline -> OnBeforeDraw -> this write -> Draw because SetPipeline
    /// resets OutlineMaskBoneBase. Non-skinned shells skip this write and keep
    /// boneBase = 0.</summary>
    protected void DrawInstanceShellBox(HighlightBox box, ID3D12Resource* lightCB, ID3D12Resource* instanceBuffer, VertexBufferView instanceVB, uint startInstanceLocation)
    {
        int fi = (int)Device.FrameIndex;
        // instanceVB is passed by value and lives on the stack, so it can be
        // addressed directly without a fixed block.
        VertexBufferView* vb = &instanceVB;
        bool skinnedShell = box.Face.MaterialParams.IsSkinned != 0;
        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
            OnBeforeDraw();
            if (skinnedShell)
                Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
            Pipeline.DrawPrimitive(box.Face, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer);

            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
            OnBeforeDraw();
            if (skinnedShell)
                Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
            Pipeline.DrawPrimitive(box.Face, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer);
        }

        Pipeline.SetPipeline(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: true);
        OnBeforeDraw();
        if (skinnedShell)
            Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
        Pipeline.DrawPrimitive(box.Edges, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer);
    }

    /// <summary>Unified highlighting for instancing: draws all instance shell
    /// boxes that have Wireframe highlighting enabled this frame, one box at a
    /// time through DrawInstanceShellBox.
    /// `instanceBuffer` is the instance stream: either the base-class
    /// `_instanceBuffer`, or a per-primitive stream managed by a derived class
    /// such as DXInstancedModel. The slot layout (64-byte stride) is identical,
    /// so any such stream works.
    /// Mixed assets (rigid + skinned primitives) draw both shells: the same
    /// writeIndex is fetched once from the rigid pool and once from the skinned
    /// pool.</summary>
    protected void DrawShellBoxes(ID3D12Resource* lightCB, ID3D12Resource* instanceBuffer, VertexBufferView instanceVB)
    {
        for (int i = 0; i < _shellBoxDrawList.Count; i++)
        {
            int idx = _shellBoxDrawList[i];
            if ((uint)idx < (uint)_instanceShellBoxes.Count)
            {
                var box = _instanceShellBoxes[idx];
                if (box != null)
                    DrawInstanceShellBox(box, lightCB, instanceBuffer, instanceVB, (uint)idx);
            }
            if ((uint)idx < (uint)_skinnedInstanceShellBoxes.Count)
            {
                var box = _skinnedInstanceShellBoxes[idx];
                if (box != null)
                    DrawInstanceShellBox(box, lightCB, instanceBuffer, instanceVB, (uint)idx);
            }
        }
    }

    // ============================================================
    // Draw (three-bucket flow: Opaque -> Fade -> Transparent)
    // ============================================================

    public override void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var lightCB = DXPrimitiveGroup.lightConstantBuffers[(int)Device.FrameIndex];
        bool forceFadeByAlpha = _currentAlpha < 1f;

        // 1. Opaque (use the Opaque PSO when overall alpha >= 1, otherwise Fade)
        // A7 main view: the opaque body reads the culled, compacted main-view segment and takes its instance count from the
        // indirect args. Fade stays direct: the scene collector splits a Fade draw per instance for back-to-front order,
        // which a GPU-sized draw cannot be, and a fading group is about to change visibility anyway.
        if (!forceFadeByAlpha && TryGetMainViewSegment(0, out var segment, out var sortedResource, out var argsResource))
        {
            int fiMain = (int)Device.FrameIndex;
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var primitive = _opaquePrimitives[i];
                Pipeline.SetPipeline(PipelineMode.Opaque, primitive.DoubleSided);
                OnBeforeDraw();
                Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fiMain], &segment, 0, 0,
                    GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(),
                    sortedResource, argsResource,
                    Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, i), _instanceBuffer);
                Season.Rendering.InstanceCull.MainViewDraws++;
            }
        }
        else
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var primitive = _opaquePrimitives[i];
                Pipeline.SetPipeline(forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
                OnBeforeDraw();
                DrawPrimitiveInstances(primitive, lightCB, (uint)_instanceCount, 0);
            }
        }

        // 2. Transparent (actual BLEND materials)
        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            BuildTransparentInstanceOrder(primitive);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
                    OnBeforeDraw();
                    DrawPrimitiveInstances(primitive, lightCB, 1, (uint)instanceIndex);
                }

                Pipeline.SetPipeline(PipelineMode.Transparent, primitive.DoubleSided ? PipelineCullVariant.Back : PipelineCullVariant.Back);
                OnBeforeDraw();
                DrawPrimitiveInstances(primitive, lightCB, 1, (uint)instanceIndex);
            }
        }

        // Unified highlighting: per-instance highlighting
        // (bounds boxes + wireframe shell boxes; enabled instances this frame,
        // with transparent faces in 2-pass mode plus opaque edges, drawn after
        // all regular surfaces)
        if (_boundsActive)
            DrawBoundsBoxes(lightCB);
        if (_wireframeActive)
            DrawShellBoxes(lightCB, _instanceBuffer, _instanceBufferView);
    }

    void BuildTransparentInstanceOrder(PrimitiveData primitive)
    {
        _transparentInstanceOrder.Clear();
        for (int i = 0; i < _instanceCount; i++)
            _transparentInstanceOrder.Add(i);
        if (DXSceneDrawCollection.Active != null) return;

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(_instanceWorlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(_instanceWorlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        return RenderView.SortDepth(Vector3.Transform(localCenter, world));
    }

    void DrawPrimitiveInstances(PrimitiveData primitiveData, ID3D12Resource* lightConstantBuffer, uint instanceCount, uint startInstanceLocation)
    {
        int fi = (int)Device.FrameIndex;
        fixed (VertexBufferView* instanceVB = &_instanceBufferView)
        {
            Pipeline.DrawPrimitive(primitiveData, lightConstantBuffer, _matrixBuffers[fi], instanceVB, instanceCount, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), _instanceBuffer);
        }
    }

    /// <summary>
    /// A7: whether the opaque body may draw from the main-view segment right now, and where it is.
    /// Every condition is a reason the segment would be wrong rather than merely slower, so any miss falls back to the direct draw:
    /// the owner did not ask for it; this frame's chain did not run or did not include the main-view slot (its args would still
    /// hold reset's zero instances and the group would vanish); the view being drawn is not the one the segment was culled for
    /// (mirrors, minimaps); or the command signature could not be created.
    /// <paramref name="stream"/> is the gather stream: 0 for a single-stream group, the opaque-primitive index for a per-primitive one.
    /// </summary>
    protected bool TryGetMainViewSegment(int stream, out VertexBufferView segment, out ID3D12Resource* sortedResource, out ID3D12Resource* argsResource)
    {
        segment = default;
        sortedResource = null;
        argsResource = null;
        if (!_cullMainView || !_cullDrawReady || !Season.Rendering.InstanceCull.MainViewCulled || _cullCapacity < _instanceCount)
            return false;
        if (!ReferenceEquals(RenderView.CurrentOrNull, DeviceServices.BaseApp?.MainView))
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not DXStorageBuffer sorted || sorted.Resource == null
            || _cullArgs is not DXStorageBuffer args || args.Resource == null)
            return false;
        if (Pipeline.EnsureShadowIndirectSignature() == null)
            return false;

        segment = new VertexBufferView
        {
            BufferLocation = sorted.Resource->GetGPUVirtualAddress()
                + (ulong)(Season.Rendering.InstanceCull.MainViewSlot * _cullCapacity) * Season.Rendering.InstanceCull.WorldStride,
            SizeInBytes = (uint)_cullCapacity * Season.Rendering.InstanceCull.WorldStride,
            StrideInBytes = Season.Rendering.InstanceCull.WorldStride,
        };
        sortedResource = sorted.Resource;
        argsResource = args.Resource;
        return true;
    }

    /// <summary>
    /// A7 shadow: when this frame's culling chain is ready, the slot segment of gather stream <paramref name="stream"/> for the shadow
    /// view being drawn (<see cref="Season.Rendering.CascadedShadow.ActiveSlot"/>) and the args buffer its counts come from. Any miss
    /// falls back to the direct whole-batch shadow draw, never to an empty one.
    /// </summary>
    protected bool TryGetShadowSegment(int stream, out VertexBufferView slotView, out ID3D12Resource* argsResource, out int slot)
    {
        slotView = default;
        argsResource = null;
        slot = Season.Rendering.CascadedShadow.ActiveSlot;
        if (!_cullDrawReady || _cullCapacity < _instanceCount)
            return false;
        if (slot < 0 || slot >= Season.Rendering.InstanceCull.MaxSlots)
            return false;
        if ((uint)stream >= (uint)_cullSorted.Length
            || _cullSorted[stream] is not DXStorageBuffer sorted || sorted.Resource == null
            || _cullArgs is not DXStorageBuffer args || args.Resource == null)
            return false;

        // Slot segment: capacity is in instances, 80 bytes per instance, one definition across all four backends.
        slotView = new VertexBufferView
        {
            BufferLocation = sorted.Resource->GetGPUVirtualAddress()
                + (ulong)(slot * _cullCapacity) * Season.Rendering.InstanceCull.WorldStride,
            SizeInBytes = (uint)_cullCapacity * Season.Rendering.InstanceCull.WorldStride,
            StrideInBytes = Season.Rendering.InstanceCull.WorldStride,
        };
        argsResource = args.Resource;
        return true;
    }

    /// <summary>
    /// 1-5 shadow pass: draw all opaque primitives for all instances in one
    /// batch, with no culling and no sorting. Transparent buckets are skipped.
    /// Uses the same group-level Matrix CB + instance buffer as the main pass.
    /// Group-invariant t6/t8/t9/t10 are bound once before the primitive loop
    /// (see Pipeline.SetShadowGroupBindings).
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        OnBeforeDraw();

        Pipeline.SetShadowGroupBindings(GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(),
            GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());

        // When b2/t5 are identical within the group, only let the first
        // primitive bind them (see CanShareShadowMaterial)
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        int fi = (int)Device.FrameIndex;

        // A7: when this frame's culling chain is ready, the instance stream reads this slot's reorder segment and
        // the instance count comes from the indirect args. The parameter table is addressed at a fixed length per
        // (slot, primitive), so one ExecuteIndirect still maps to exactly one primitive: indirect only changes
        // where the parameters come from, it does not merge draws.
        if (TryGetShadowSegment(0, out var slotView, out var argsResource, out int slot))
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                Pipeline.DrawShadowPrimitiveIndirect(_opaquePrimitives[i], _matrixBuffers[fi], &slotView,
                    argsResource, Season.Rendering.InstanceCull.ArgsOffset(slot, i),
                    bindMaterial: !shareMaterial || !materialBound);
                materialBound = true;
            }
            return;
        }

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            fixed (VertexBufferView* instanceVB = &_instanceBufferView)
            {
                Pipeline.DrawShadowPrimitive(_opaquePrimitives[i], _matrixBuffers[fi], instanceVB,
                    (uint)_instanceCount, 0, bindMaterial: !shareMaterial || !materialBound);
            }
            materialBound = true;
        }
    }

    /// <summary>
    /// Outline2D mask: rendered through the instance stream.
    /// Host-wide activation draws a single full batch; per-instance activation
    /// draws instanceCount=1 for each writeIndex slot.
    /// The base class's non-instanced DrawOutlineMask would draw the instance
    /// stream with an identity matrix, causing missing / misaligned instances, so
    /// this override is required.
    /// Transparent buckets are skipped, matching the base-class mask semantics.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        // Rewrite outline color per group through root constant b6. Multiple
        // colors may exist in the same frame; colors come from the group color
        // fixed during UpdateInstanceData (instance color or host color).
        Pipeline.SetOutlineMaskColor(_outline2DColor);

        int fi = (int)Device.FrameIndex;
        var lightCB = DXPrimitiveGroup.lightConstantBuffers[fi];
        fixed (VertexBufferView* instanceVB = &_instanceBufferView)
        {
            for (int i = 0; i < _opaquePrimitives.Count; i++)
            {
                var p = _opaquePrimitives[i];
                Pipeline.SetPipeline(PipelineMode.Opaque,
                    p.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
                OnBeforeDraw();
                if (_outline2DHostActive)
                {
                    Pipeline.DrawPrimitive(p, lightCB, _matrixBuffers[fi], instanceVB, (uint)_instanceCount, 0,
                        GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), _instanceBuffer);
                }
                else
                {
                    for (int k = 0; k < _outline2DInstances.Count; k++)
                    {
                        int idx = _outline2DInstances[k];
                        if ((uint)idx >= (uint)_instanceCount)
                            continue;
                        Pipeline.DrawPrimitive(p, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)idx,
                            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), _instanceBuffer);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Derived classes may override this to return the per-instance bone
    /// StructuredBuffer SRV handle.
    /// The default return value is `default`, in which case DrawPrimitive uses
    /// the identity buffer automatically.
    /// </summary>
    protected virtual GpuDescriptorHandle GetInstanceBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier B): derived classes may override this to return
    /// the previous per-instance bone SB SRV handle.</summary>
    protected override GpuDescriptorHandle GetPrevBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier C-a): returns the prev instance-world SB SRV
    /// handle maintained by the base class.</summary>
    protected GpuDescriptorHandle GetPrevInstanceWorldSrvHandle()
        => DXSceneDrawCollection.Structured(_prevInstanceWorldBuffer, 64, _prevInstanceWorldSrvHandle);

    /// <summary>2-3 Step C (tier C-a): when a derived class such as
    /// DXInstancedModel manages its own instance-world array, it can explicitly
    /// copy this frame's data into the prev SB.
    /// The base class already calls this automatically inside UpdateInstanceData.
    /// If a derived class bypasses that update path, it must call this manually
    /// before uploading the current frame.</summary>
    protected void FillPrevInstanceWorldFrom(Matrix4x4[] worlds, int count)
    {
        if (_mappedPrevInstanceWorldBuffer == null || count <= 0 || worlds == null)
            return;
        fixed (Matrix4x4* pSrc = worlds)
            Unsafe.CopyBlock(_mappedPrevInstanceWorldBuffer, pSrc, (uint)(count * sizeof(Matrix4x4)));
    }

    // ============================================================
    // A7 per-instance GPU culling: resources and transition
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

    /// <summary>
    /// Prepare culling resources for the given instance count. Capacity is aligned to 64 so small instance-count
    /// jitter does not recreate the five buffers every frame; but the capacity reported through the interface must
    /// be the real capacity, otherwise the index table would address by this frame's instance count and get out of
    /// sync with what the kernel writes.
    /// A per-primitive-stream group also rebuilds when its opaque-primitive count changes, since each of those owns a stream pair; a
    /// group with more primitives than <see cref="Season.Rendering.InstanceCull.MaxPrimitives"/>, or with no opaque primitive at all,
    /// cannot join and releases instead, so it keeps the direct path rather than registering buffers nothing will read.
    /// </summary>
    protected void EnsureCullResources(int count)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // The instance count dropping to 0 does not mean release: hiding and re-showing would otherwise recreate
        // the five buffers every frame.
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
                // The args buffer is the only one that must declare the indirect usage: all four backends bake that
                // usage into the native object, and it cannot be added afterward.
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
    /// Build only the parameter-table bytes, without touching the GPU -- the safe timing for building and
    /// uploading differs: building can be done anytime, but uploading must wait until the command list is open
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>). If the primitive set does not
    /// change, do not rebuild. The index range must share one source with the draw side: the reset kernel writes
    /// this range into the indirect args, and the draw side no longer provides one itself (see <see cref="GetCullPrimitiveRange"/>).
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullPrimInfo == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // Explicitly record "there is no parameter table to upload": otherwise PrepareCullInputs would
            // re-upload last time's bytes over and over, where the entry count looks right but the content is stale.
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
    /// A7: upload the culling CPU-side inputs to the GPU. InstanceCullEffect calls this before each dispatch; that
    /// phase runs after BeforeRender and outside all Passes, and is the only place across all four backends that
    /// jointly satisfies the upload preconditions. The control's Update will not work: at that point the command
    /// list is not yet open.
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        if (!Season.Rendering.InstanceCull.Enabled || _instanceCount <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // The parameter table follows the geometry, not the frame, so re-upload only when the entry count changes.
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullPrimInfoBytes == null || _cullPrimInfo == null)
                return false;
            int bytes = _cullPrimInfoPending * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride;
            if (!g.TryUpdateStorageBuffer(_cullPrimInfo, new ReadOnlySpan<byte>(_cullPrimInfoBytes, 0, bytes)))
                return false;
            _cullPrimitiveCount = _cullPrimInfoPending;
        }

        // The matrix stream changes every frame; upload only the valid range and leave no slack.
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
    /// A7: transition the reorder stream written by gather and the args buffer into a draw-readable state. This
    /// must run outside a Pass, so InstanceCullEffect calls it after the three dispatches rather than waiting until
    /// DrawShadow. If any step fails it returns false and the whole group falls back to CPU culling for this frame.
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullSorted.Length == 0)
            return false;
        foreach (var s in _cullSorted)
        {
            if (s is not DXStorageBuffer sb || sb.Resource == null)
                return false;
        }
        if (_cullArgs is not DXStorageBuffer args || args.Resource == null)
            return false;
        if (_cullCapacity <= 0)
            return false;

        var cmd = Device.GraphicsCommandList;
        if (cmd == null)
            return false;

        // The vertex stage needs to read them; the next gather transitions each back to UnorderedAccess through
        // DispatchCompute's gated transition.
        foreach (var s in _cullSorted)
            ((DXStorageBuffer)s!).TransitionTo(cmd, ResourceStates.VertexAndConstantBuffer);

        // The args buffer goes through the unified entry point, which also checks the usage and "not currently inside a Pass".
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

    /// <summary>2-3 Step C (tier C-b): derived classes may override this to
    /// return the previous per-instance morph-weights SB SRV handle.</summary>
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle() => default;

    // ============================================================
    // Dispose
    // ============================================================

    public override void Dispose()
    {
        ReleaseCullResources();

        foreach (var primitive in _primitives)
            primitive.Dispose();
        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        if (_instanceBuffer != null)
        {
            _instanceBuffer->Release();
            _instanceBuffer = null;
        }

        // 2-3 Step C: release the previous instance-world SB
        if (_prevInstanceWorldBuffer != null)
        {
            _prevInstanceWorldBuffer->Unmap(0, null);
            _prevInstanceWorldBuffer->Release();
            _prevInstanceWorldBuffer = null;
            _mappedPrevInstanceWorldBuffer = null;
        }
        if (_prevInstanceWorldDescriptorId >= 0)
        {
            Device.DescriptorAllocator.Free(_prevInstanceWorldDescriptorId);
            _prevInstanceWorldDescriptorId = -1;
        }

        if (_matrixBuffers != null)
        {
            for (int i = 0; i < _matrixBuffers.Length; i++)
            {
                if (_matrixBuffers[i] == null)
                    continue;

                _matrixBuffers[i]->Unmap(0, null);
                _matrixBuffers[i]->Release();
                _matrixBuffers[i] = null;
            }
        }

        _mappedMatrixBuffers = null!;
        _matrixBuffers = null!;

        // Unified highlighting: release the highlight pool
        // (host bounds box + instance-box pool + wireframe shell boxes /
        // templates / instance shell-box pool)
        DisposeHighlights();
    }
}
