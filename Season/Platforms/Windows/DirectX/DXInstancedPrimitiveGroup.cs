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
internal unsafe abstract class DXInstancedPrimitiveGroup : DXPrimitiveGroup
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

    protected void CreateSharedMatrixBuffers(Season.Basic.Camera camera)
    {
        int n = (int)Device.frameCount;
        _matrixBuffers = new ID3D12Resource*[n];
        _mappedMatrixBuffers = new byte*[n];

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(camera.View),
            Projection = Matrix4x4.Transpose(camera.Projection)
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

        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.View),
            Projection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.Projection),
            // 2-3 Step C (tier C-a): the previous instance world now lives in the
            // prev instance-world SB (t9), so b0.PrevWorld stays all zero.
            // The instanced shader path does not read b0 prevWorld.
            PrevViewProjection = Matrix4x4.Transpose(DXPrimitiveGroup.Camera.PrevViewProjection),
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
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), _prevInstanceWorldSrvHandle, GetPrevMorphSrvHandle());

            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
            OnBeforeDraw();
            if (skinnedShell)
                Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
            Pipeline.DrawPrimitive(box.Face, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), _prevInstanceWorldSrvHandle, GetPrevMorphSrvHandle());
        }

        Pipeline.SetPipeline(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: true);
        OnBeforeDraw();
        if (skinnedShell)
            Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
        Pipeline.DrawPrimitive(box.Edges, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), _prevInstanceWorldSrvHandle, GetPrevMorphSrvHandle());
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
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            OnBeforeDraw();
            DrawPrimitiveInstances(primitive, lightCB, (uint)_instanceCount, 0);
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

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(_instanceWorlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(_instanceWorlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        var center = Vector3.Transform(localCenter, world);

        var app = DeviceServices.BaseApp;
        if (app == null)
            return center.Z;

        var forward = app.CameraTarget - app.CameraPos;
        if (forward.LengthSquared() < 1e-6f)
            forward = Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        return Vector3.Dot(center - app.CameraPos, forward);
    }

    void DrawPrimitiveInstances(PrimitiveData primitiveData, ID3D12Resource* lightConstantBuffer, uint instanceCount, uint startInstanceLocation)
    {
        int fi = (int)Device.FrameIndex;
        fixed (VertexBufferView* instanceVB = &_instanceBufferView)
        {
            Pipeline.DrawPrimitive(primitiveData, lightConstantBuffer, _matrixBuffers[fi], instanceVB, instanceCount, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());
        }
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
                        GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());
                }
                else
                {
                    for (int k = 0; k < _outline2DInstances.Count; k++)
                    {
                        int idx = _outline2DInstances[k];
                        if ((uint)idx >= (uint)_instanceCount)
                            continue;
                        Pipeline.DrawPrimitive(p, lightCB, _matrixBuffers[fi], instanceVB, 1, (uint)idx,
                            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());
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
    protected virtual GpuDescriptorHandle GetPrevBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier C-a): returns the prev instance-world SB SRV
    /// handle maintained by the base class.</summary>
    protected GpuDescriptorHandle GetPrevInstanceWorldSrvHandle() => _prevInstanceWorldSrvHandle;

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

    /// <summary>2-3 Step C (tier C-b): derived classes may override this to
    /// return the previous per-instance morph-weights SB SRV handle.</summary>
    protected override GpuDescriptorHandle GetPrevMorphSrvHandle() => default;

    // ============================================================
    // Dispose
    // ============================================================

    public override void Dispose()
    {
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
