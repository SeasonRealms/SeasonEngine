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

    // The primitive buckets, the per-instance world matrices, the transparent instance order, the wireframe /
    // Outline2D activation state and the merged cull box all live in PrimitiveGroupCore.Instanced.cs: they are
    // CPU-side bookkeeping that every instance backend keeps identical, and the transparent sort reads the world
    // matrices, so the two cannot be separated. Only the D3D12 instance stream below is backend state.
    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();

    protected ID3D12Resource*[] _matrixBuffers = null!;
    protected byte*[] _mappedMatrixBuffers = null!;

    // ============================================================
    // Frame ring: one slot per frame in flight
    // ============================================================
    // Every per-frame GPU buffer below is a ring with one slot per in-flight frame (Device.frameCount). A frame
    // writes slot FrameSlot and reads slot FrameSlot; the GPU may still be executing the previous one or two
    // frames, which read their own slots, so nothing a frame writes can be observed by another frame's work.
    //
    // "The previous frame" is therefore not a copy but a different slot of the SAME ring: PrevFrameSlot is the
    // slot written by the frame before this one. That is what replaced the old whole-block memcpy, and on D3D12 it
    // also removed a read of write-combined upload memory, which the CPU performs an order of magnitude slower
    // than an ordinary write.
    //
    // The swap is only sound with one slot per in-flight frame: a two-slot swap would have frame k overwrite the
    // data frame k-2 is still reading. Cold slots stay zero-initialized, which is what keeps the shader sentinels
    // (_m33 == 0) meaningful on the first frame a slot is used.
    protected static int FrameSlotCount => (int)Device.frameCount;

    /// <summary>Empty ring, shared. A pointer type cannot be a generic argument, so <c>Array.Empty&lt;T&gt;()</c> does not
    /// exist for these arrays; the empty rings below point here rather than allocate a fresh zero-length array.</summary>
    protected static readonly ID3D12Resource*[] EmptyResourceRing = new ID3D12Resource*[0];
    protected static readonly byte*[] EmptyMappedRing = new byte*[0];

    /// <summary>Ring slot this frame writes and reads.</summary>
    protected static int FrameSlot => (int)(Device.FrameIndex % Device.frameCount);

    /// <summary>Ring slot holding the previous frame's data (see the frame-ring note above).</summary>
    protected static int PrevFrameSlot => (int)((Device.FrameIndex + Device.frameCount - 1) % Device.frameCount);

    protected ID3D12Resource*[] _instanceBuffers = EmptyResourceRing;
    protected VertexBufferView[] _instanceBufferViews = Array.Empty<VertexBufferView>();
    protected int _instanceCapacity;

    /// <summary>This frame's IA instance buffer and view.</summary>
    protected ID3D12Resource* CurrentInstanceBuffer
        => (uint)FrameSlot < (uint)_instanceBuffers.Length ? _instanceBuffers[FrameSlot] : null;
    protected VertexBufferView CurrentInstanceBufferView
        => (uint)FrameSlot < (uint)_instanceBufferViews.Length ? _instanceBufferViews[FrameSlot] : default;

    // 2-3 Step C (tier C-a): instance-world ring (one Matrix4x4 per entry).
    // Slot i holds the instance worlds of the frame that wrote slot i, so the shader reads the previous frame's
    // worlds from PrevFrameSlot with no copy in between. Contents are zeroed before the first frame (sentinel
    // _m33==0), and the shader falls back to the current worldMatrix.
    protected ID3D12Resource*[] _instanceWorldBuffers = EmptyResourceRing;
    protected byte*[] _mappedInstanceWorldBuffers = EmptyMappedRing;
    protected GpuDescriptorHandle[] _instanceWorldSrvHandles = Array.Empty<GpuDescriptorHandle>();
    protected int[] _instanceWorldDescriptorIds = Array.Empty<int>();
    protected int _instanceWorldCapacity;

    // ============================================================
    // Generic per-instance extras (t20): Color / Extra per compacted slot
    // ============================================================
    // One stream per group per frame slot, sized to the compacted instance count and indexed by exactly the slot the
    // instance matrices and every other per-instance SB use. Kept apart from the 80-byte InstanceTransformData on
    // purpose: that stream is bit-identical to the A7 cull input and the IA instance buffer, and the text path also
    // reinterprets it, so growing its stride would touch culling, gather and text at once. This one costs one extra
    // vertex-stage storage read and nothing else.
    //
    // It is uploaded every frame, white included. Measuring it as "zero when unused" would be a false saving: the
    // CPU array is rebuilt by the same loop that rebuilds the matrices, so skipping the copy would still leave the
    // rebuild, and a group that does use colors would then need a change-detection path of its own. 32 bytes per
    // instance is 40% of the 80 the matrix stream already moves.
    protected InstanceExtras[] _instanceExtras = Array.Empty<InstanceExtras>();
    protected ID3D12Resource*[] _instanceExtrasBuffers = EmptyResourceRing;
    protected byte*[] _mappedInstanceExtrasBuffers = EmptyMappedRing;
    protected GpuDescriptorHandle[] _instanceExtrasSrvHandles = Array.Empty<GpuDescriptorHandle>();
    protected int[] _instanceExtrasDescriptorIds = Array.Empty<int>();
    protected int _instanceExtrasCapacity;

    /// <summary>This frame slot's extras SRV, or default when the ring has not been sized yet.</summary>
    protected GpuDescriptorHandle GetInstanceExtrasSrvHandle()
        => (uint)FrameSlot < (uint)_instanceExtrasSrvHandles.Length ? _instanceExtrasSrvHandles[FrameSlot] : default;

    // ============================================================
    // A7 per-instance GPU culling
    // ============================================================
    // The culling resources are allocated and released together; capacity is aligned to 64 and only grows. When the owning ones are
    // null this group does not participate in A7, and shadows fall back to whole-batch CPU culling rather than drawing with empty buffers.
    //
    // A7c maintenance contract. The read-only aliases (_cullSource on a single-stream group, _cullStreamSources on a per-primitive-stream one)
    // are what removed the duplicate upload, and each of the following is a precondition rather than a detail:
    //
    // (1) Refresh ordering. EnsureBorrowedCullSources has to run after the instance buffer may have been recreated and before the dispatch. Update
    // satisfies that today (EnsureInstanceBufferCapacity -> SetVertexBuffer -> EnsureCullResources). Growing or swapping the instance buffer
    // anywhere later in the frame leaves the alias describing a released resource, and without the debug layer D3D12 reports nothing: the symptom is
    // instances drawn with another object's transforms, which reads as a game-logic bug rather than a resource bug.
    //
    // (2) Key the alias on (pointer, size), never on capacity alone. Release a COM object and allocate the next one and the allocator can hand back the
    // same address; a descriptor whose NumElements still describes the old capacity then either reads past a smaller resource or rejects a larger one.
    //
    // (3) The IA instance stream must not be rewritten after Device.SetVertexBuffer. This group's cull input is now that very buffer, so a later write
    // would be seen by the cull kernel on D3D12 but not on Vulkan/Metal/WebGPU, which still upload a snapshot in PrepareCullInputs. "Same bytes, same
    // frame" is the whole reason the four backends can disagree about the transport and still agree about the picture.
    //
    // (4) What this buys and what it costs. The instance buffer is recreated on every exact count change, so an unstable count allocates one SRV plus one
    // deferred descriptor release per change, against the former per-frame staging write plus CopyBufferRegion plus two transitions. A stable count is
    // strictly cheaper; a jittering one is roughly a wash, not a win. Measure before assuming the direction.

    /// <summary>A7 cull input (see <see cref="Season.Rendering.IInstanceCullTarget.CullSourceWorlds"/>), with one of two origins:
    /// a single-stream group aliases its IA instance ring, because its IA stream is bit-identical to this input and uploading it
    /// twice moves no new information; a per-primitive-stream group owns a buffer instead, since the root instance worlds it culls against
    /// exist nowhere else on the GPU. That is also why <see cref="PrepareCullInputs"/> uploads only the second kind.
    /// The single-stream case is one alias per frame slot, for the same reason the buffers themselves are a ring.</summary>
    Season.Rendering.StorageBuffer?[] _cullSourceSlots = Array.Empty<Season.Rendering.StorageBuffer?>();
    /// <summary>The owned variant above (per-primitive-stream groups only).</summary>
    Season.Rendering.StorageBuffer? _cullSource;
    /// <summary>One reorder stream per gather stream (see <see cref="Season.Rendering.IInstanceCullTarget.CullStreamCount"/>): length 1 for a
    /// group whose primitives share one instance stream, one per opaque primitive when <see cref="CullPerPrimitiveStreams"/>. Empty means no A7.</summary>
    Season.Rendering.StorageBuffer?[] _cullSorted = Array.Empty<Season.Rendering.StorageBuffer?>();
    /// <summary>Per-primitive-stream gather sources, parallel to <see cref="_cullSorted"/> and one alias per frame slot. Empty for a single-stream
    /// group, whose one gather source is the cull input <see cref="_cullSourceSlots"/> itself -- uploading the same bytes twice would buy nothing.</summary>
    Season.Rendering.StorageBuffer?[][] _cullStreamSourceSlots = Array.Empty<Season.Rendering.StorageBuffer?[]>();
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    int _cullCapacity = -1;
    /// <summary>Number of parameter-table entries already uploaded to the GPU. When it differs from <see cref="_cullPrimInfoPending"/> this frame must re-upload.</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>Parameter-table bytes already built on the CPU side. Stored here instead of being uploaded directly in UPDATE because building is not the same as uploading:
    /// building can happen anytime, but uploading must wait until the command list is open (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>).</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>Number of CPU-side parameter-table entries; -1 means it has not been built yet.</summary>
    int _cullPrimInfoPending = -1;
    /// <summary>A7b: the arg-buffer image composed from <see cref="_cullPrimInfoBytes"/>, kept next to it for the same reason -- one array
    /// whose content only changes with the primitive set, so the per-frame path is one "is it stale" comparison and nothing else.</summary>
    byte[]? _cullArgsTemplate;
    bool _cullRegistered;

    /// <summary>Whether this frame has transitioned the reorder stream and indirect args into a drawable state. Cleared at the start of Update, set by PrepareCullDraw.</summary>
    bool _cullDrawReady;

    /// <summary>The owner's effective draw path is <see cref="InstanceDrawPath.Indirect"/>, latched in Update so the cull dispatch and the draw see the same answer.</summary>
    bool _cullMainView;

    /// <summary>
    /// A7 multi-stream hook. False (default): every primitive reads the one group instance stream, which is also the cull input.
    /// True: opaque primitive i reads its own instance stream i (a glTF model, whose stream is "node world × instance world" plus that
    /// primitive's morph weights), so gather materializes one reordered stream per opaque primitive from the shared index table.
    /// The cull input stays the base <see cref="_instanceData"/>, which the derived class fills with the root instance worlds.
    /// </summary>
    protected virtual bool CullPerPrimitiveStreams => false;

    /// <summary>
    /// A7c: the IA instance ring behind gather stream <paramref name="stream"/> and its capacity in instances (index into
    /// <see cref="_opaquePrimitives"/>). Only called when <see cref="CullPerPrimitiveStreams"/> is true.
    ///
    /// The cull no longer uploads a copy of these streams: gather reads the very buffer the draw binds, through a read-only alias
    /// (see <see cref="DXStorageBuffer.CreateReadOnlyView"/>). That is only sound as long as the alias is rebuilt whenever the buffer
    /// object changes, which is why the capacity travels with the pointer instead of being assumed -- and why the whole ring travels
    /// with it, now that a stream is one buffer per frame in flight rather than a single one.
    /// </summary>
    protected virtual bool TryGetCullDrawStreamRing(int stream, out ID3D12Resource*[] ring, out int capacity)
    {
        ring = EmptyResourceRing;
        capacity = 0;
        return false;
    }

    /// <summary>
    /// The index range the indirect args must replay for <paramref name="primitive"/>. <see cref="InstanceCull.BuildArgsTemplate"/> writes
    /// it into every slot's args and the indirect draw no longer passes a range of its own, so it must come from the same source as the direct draw's
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

    /// <summary>
    /// Latch this frame's A7 state at the top of Update. The transition state is recomputed each frame: last frame's true must not
    /// keep authorizing this frame's indirect draw.
    /// </summary>
    protected void BeginCullFrame(InstancedMesh3DBase owner)
    {
        _cullDrawReady = false;
        _cullMainView = owner.EffectiveDrawPath == InstanceDrawPath.Indirect;
    }

    // ============================================================
    // Instance-buffer management
    // ============================================================

    /// <summary>
    /// Retire a per-frame ring. The unmaps are CPU-only and happen now; the releases wait for the fence value this
    /// frame already signalled, because up to two frames in flight are still reading these slots. Releasing eagerly
    /// here is what the old single-slot code did, and D3D12 only reports the consequence through DRED after the fact.
    /// </summary>
    /// <param name="mapped">
    /// The ring's persistent-mapping array, or null when the ring holds no live mapping. Only the persistent-mapping
    /// rings - bone / morph / world / extras, mapped at creation and written through the stored pointer - may be
    /// unmapped here. The IA streams built by CreateVertexBuffer are written through balanced Map/Unmap pairs and
    /// have no live mapping, and unmapping those raises RESOURCE_MANIPULATION #310 (resource is not mapped), which
    /// the debug layer turns into a hard break and the shutdown path then reports as an SEHException. Both arrays are
    /// cleared as they are retired, so retiring the same ring twice is a no-op instead of a second Unmap.
    /// </param>
    protected static void ReleaseRingDeferred(ID3D12Resource*[] ring, byte*[]? mapped = null)
    {
        if (ring == null || ring.Length == 0)
            return;

        // Detach before anything else: the deferred Release below captures this copy, so a repeated call on the same
        // ring finds it empty, and a caller that swaps its field afterwards cannot make the release see new slots.
        var retired = new ID3D12Resource*[ring.Length];
        Array.Copy(ring, retired, ring.Length);
        Array.Clear(ring, 0, ring.Length);

        if (mapped != null)
        {
            int slots = Math.Min(mapped.Length, retired.Length);
            for (int i = 0; i < slots; i++)
            {
                // A cleared mapping means this slot was already retired; a null resource means the two arrays
                // disagree, and the Unmap is skipped rather than aimed at whatever resource happens to sit there.
                if (mapped[i] == null) continue;
                if (retired[i] != null) retired[i]->Unmap(0, null);
                mapped[i] = null;
            }
            // A mapping longer than the ring must not keep a dangling pointer past the length it shared.
            for (int i = slots; i < mapped.Length; i++)
                mapped[i] = null;
        }

        ulong fence = Device.GetCurrentRetireFenceValue();
        Device.EnqueueDeferredRelease(fence, () =>
        {
            foreach (var resource in retired)
            {
                if (resource != null)
                    resource->Release();
            }
        });
    }

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceCapacity == count && _instanceBuffers.Length == FrameSlotCount)
            return;

        ReleaseRingDeferred(_instanceBuffers);

        int slots = FrameSlotCount;
        var buffers = new ID3D12Resource*[slots];
        var views = new VertexBufferView[slots];
        for (int i = 0; i < slots; i++)
            buffers[i] = Device.CreateVertexBuffer<InstanceTransformData>((uint)count, out views[i]);

        _instanceBuffers = buffers;
        _instanceBufferViews = views;
        _instanceCapacity = count;

        // 2-3 Step C: grow/shrink the instance-world ring together with the
        // instance buffer (same capacity as _instanceWorlds, one Matrix4x4 per entry)
        EnsureInstanceWorldRingCapacity(count);
    }

    // 2-3 Step C: create / grow / shrink the instance-world ring
    // (upload heap, persistently mapped, one slot per frame in flight)
    /// <summary>
    /// Grows the instance-world ring on its own. A per-primitive-stream group (a glTF model) never uses the group-level
    /// IA buffer -- each primitive has its own stream -- but the shader still reads the previous frame's worlds from this
    /// ring, so it has to be sized independently of <see cref="EnsureInstanceBufferCapacity"/>.
    /// </summary>
    protected void EnsureInstanceWorldCapacity(int count) => EnsureInstanceWorldRingCapacity(count);

    /// <summary>
    /// Grows/shrinks the extras ring to <paramref name="count"/> records per frame slot. Same policy as the
    /// instance-world ring: exact count, reallocated on a change, deferred release, one SRV per slot.
    /// </summary>
    protected void EnsureInstanceExtrasRingCapacity(int count)
    {
        if (count <= 0)
            return;
        if (_instanceExtrasCapacity == count && _instanceExtrasBuffers.Length == FrameSlotCount)
            return;

        ReleaseRingDeferred(_instanceExtrasBuffers, _mappedInstanceExtrasBuffers);
        foreach (int id in _instanceExtrasDescriptorIds)
        {
            if (id >= 0)
                Device.DescriptorAllocator.Free(id);
        }

        int slots = FrameSlotCount;
        ulong bufferSize = (ulong)(count * sizeof(InstanceExtras));
        var buffers = new ID3D12Resource*[slots];
        var mapped = new byte*[slots];
        var handles = new GpuDescriptorHandle[slots];
        var descriptorIds = new int[slots];

        for (int slot = 0; slot < slots; slot++)
        {
            var buffer = Device.ResourceManager.CreateBuffer(HeapType.Upload, bufferSize, ResourceStates.GenericRead);
            void* pData;
            buffer->Map(0, null, &pData);
            mapped[slot] = (byte*)pData;
            // White, not zero: a slot that is never written this frame must read as "no tint" rather than black.
            var initial = new Span<InstanceExtras>((void*)mapped[slot], count);
            for (int i = 0; i < count; i++)
                initial[i] = new InstanceExtras { Color = Vector4.One, Extra = Vector4.Zero };

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate(DescriptorKind.InstanceRing);
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatUnknown,
                ViewDimension = Silk.NET.Direct3D12.SrvDimension.Buffer,
                Shader4ComponentMapping = 0x00001688u,
                Buffer = new BufferSrv
                {
                    FirstElement = 0,
                    NumElements = (uint)count,
                    StructureByteStride = (uint)sizeof(InstanceExtras),
                    Flags = BufferSrvFlags.None
                }
            };
            Device.D3dDevice->CreateShaderResourceView(buffer, &srvDesc, Device.SrvHeapManager.GetCpuHandle(descriptorIds[slot]));
            handles[slot] = Device.SrvHeapManager.GetGpuHandle(descriptorIds[slot]);
            buffers[slot] = buffer;
        }

        _instanceExtrasBuffers = buffers;
        _mappedInstanceExtrasBuffers = mapped;
        _instanceExtrasSrvHandles = handles;
        _instanceExtrasDescriptorIds = descriptorIds;
        _instanceExtrasCapacity = count;
    }

    /// <summary>Copies this frame's extras records into this frame slot's mapped page. The ring is already sized.</summary>
    protected void UploadInstanceExtrasForFrame()
    {
        int slot = FrameSlot;
        if ((uint)slot >= (uint)_mappedInstanceExtrasBuffers.Length || _instanceCount <= 0)
            return;
        var destination = new Span<byte>(_mappedInstanceExtrasBuffers[slot], _instanceCount * sizeof(InstanceExtras));
        MemoryMarshal.AsBytes(_instanceExtras.AsSpan(0, _instanceCount)).CopyTo(destination);
    }

    void EnsureInstanceWorldRingCapacity(int count)
    {
        if (count <= 0)
            return;
        if (_instanceWorldCapacity == count && _instanceWorldBuffers.Length == FrameSlotCount)
            return;

        ReleaseRingDeferred(_instanceWorldBuffers, _mappedInstanceWorldBuffers);
        foreach (int id in _instanceWorldDescriptorIds)
        {
            if (id >= 0)
                Device.DescriptorAllocator.Free(id);
        }

        int slots = FrameSlotCount;
        ulong bufferSize = (ulong)(count * sizeof(Matrix4x4));
        var buffers = new ID3D12Resource*[slots];
        var mapped = new byte*[slots];
        var handles = new GpuDescriptorHandle[slots];
        var descriptorIds = new int[slots];

        for (int slot = 0; slot < slots; slot++)
        {
            var buffer = Device.ResourceManager.CreateBuffer(HeapType.Upload, bufferSize, ResourceStates.GenericRead);
            void* pData;
            buffer->Map(0, null, &pData);
            mapped[slot] = (byte*)pData;
            // Every slot starts zeroed, not just the first one: the shader's sentinel (_m33 == 0) is what makes an
            // unwritten slot read as "no previous frame" instead of as whatever the driver handed back.
            new Span<byte>(mapped[slot], count * sizeof(Matrix4x4)).Clear();

            descriptorIds[slot] = Device.DescriptorAllocator.Allocate(DescriptorKind.InstanceRing);
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
            Device.D3dDevice->CreateShaderResourceView(buffer, &srvDesc, Device.SrvHeapManager.GetCpuHandle(descriptorIds[slot]));
            handles[slot] = Device.SrvHeapManager.GetGpuHandle(descriptorIds[slot]);
            buffers[slot] = buffer;
        }

        _instanceWorldBuffers = buffers;
        _mappedInstanceWorldBuffers = mapped;
        _instanceWorldSrvHandles = handles;
        _instanceWorldDescriptorIds = descriptorIds;
        _instanceWorldCapacity = count;
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
            _instanceExtras = new InstanceExtras[_instanceCount];
        }

        EnsureInstanceBufferCapacity(_instanceCount);
        EnsureInstanceExtrasRingCapacity(_instanceCount);

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
            // Generic per-instance extras: the color is whatever the control set, and white (the default) keeps this
            // instance bit-identical to the pre-extras render. Color.W stays in the record even though nothing reads it
            // yet: alpha stays with the material and the control's own Alpha chain.
            _instanceExtras[writeIndex] = new InstanceExtras { Color = instance.Color, Extra = Vector4.Zero };

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

        Device.SetVertexBuffer(CurrentInstanceBuffer, CurrentInstanceBufferView, _instanceData);

        // 2-3 Step C (tier C-a): publish this frame's instance worlds into this frame's ring slot. The shader reads
        // the previous frame's worlds from the slot the previous frame wrote, so there is no copy between the two.
        UploadInstanceWorldsForFrame(_instanceWorlds, _instanceCount);
        UploadInstanceExtrasForFrame();

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

        // 2-3 Step C (tier C-a): publishes whether the previous-frame streams may be read. Not a one-shot call any more:
        // the history is addressed by the compacted instance slot, so it only lines up while the enabled order is unchanged
        // (see InstancedMesh3DBase.InstanceHistoryValid). The first frame after load has no history and passes false.
        UpdatePrevStateFlags(wasInitialized && owner.InstanceHistoryValid);

        SyncAlpha(alpha);
    }

    /// <summary>
    /// D3D12 records a group's draw commands into <see cref="DXSceneDrawCollection"/> and replays them later, so the
    /// transparent order is fixed at record time and must not be re-sorted: the replay does not come back through the
    /// sort, and the two orders would then disagree. Vulkan and Metal submit immediately and keep the core default.
    /// </summary>
    protected override bool IsQueuedReplayActive => DXSceneDrawCollection.Active != null;

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
    /// SetPipeline -> this write -> Draw because SetPipeline resets
    /// OutlineMaskBoneBase. Non-skinned shells skip this write and keep
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
            if (skinnedShell)
                Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
            Pipeline.DrawPrimitive(box.Face, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer, instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());

            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
            if (skinnedShell)
                Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
            Pipeline.DrawPrimitive(box.Face, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer, instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
        }

        Pipeline.SetPipeline(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: true);
        if (skinnedShell)
            Pipeline.SetOutlineMaskBoneBase(startInstanceLocation);
        Pipeline.DrawPrimitive(box.Edges, lightCB, _matrixBuffers[fi], vb, 1, startInstanceLocation,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), instanceBuffer, instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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

    /// <summary>
    /// Main pass for the instance group. Overrides the core entry rather than hiding it, so a
    /// <see cref="Season.Rendering.Drawables.PrimitiveGroupCore{TPrim, TMat, TTex}"/> reference dispatches here.
    /// The bucket walk, the fade decision and the two-pass transparent split live in
    /// <c>PrimitiveGroupCore.DrawInstancedMainPass</c>.
    /// </summary>
    public override void Draw() => DrawInstancedMainPass();

    protected override void OnBeginMainPass()
        => DrawTrace.Group("dx", "main", Name, _opaquePrimitives.Count);

    protected override void DrawOpaqueInstances(PrimitiveData primitive, int streamIndex, bool fadeInsteadOfOpaque)
    {
        int fi = (int)Device.FrameIndex;
        var lightCB = DXPrimitiveGroup.lightConstantBuffers[fi];

        // A7 main view: the opaque body reads the culled, compacted main-view segment and takes its instance count from the
        // indirect args. Fade stays direct: the scene collector splits a Fade draw per instance for back-to-front order,
        // which a GPU-sized draw cannot be, and a fading group is about to change visibility anyway.
        if (!fadeInsteadOfOpaque && TryGetMainViewSegment(0, out var segment, out var sortedResource, out var argsResource))
        {
            Pipeline.SetPipeline(PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(primitive, lightCB, _matrixBuffers[fi], &segment, 0, 0,
                GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(),
                sortedResource, argsResource,
                Season.Rendering.InstanceCull.ArgsOffset(Season.Rendering.InstanceCull.MainViewSlot, streamIndex), CurrentInstanceBuffer,
                instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
            Season.Rendering.InstanceCull.MainViewDraws++;
            return;
        }

        Pipeline.SetPipeline(fadeInsteadOfOpaque ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
        DrawPrimitiveInstances(primitive, lightCB, (uint)_instanceCount, 0);
    }

    protected override void DrawTransparentInstance(PrimitiveData primitive, int instanceIndex, bool frontFace)
    {
        Pipeline.SetPipeline(PipelineMode.Transparent, frontFace ? PipelineCullVariant.Front : PipelineCullVariant.Back);
        DrawPrimitiveInstances(primitive, DXPrimitiveGroup.lightConstantBuffers[(int)Device.FrameIndex], 1, (uint)instanceIndex);
    }

    // Unified highlighting: per-instance bounds boxes plus wireframe shell boxes. It uses the instances
    // enabled for this frame, draws transparent faces in two passes plus opaque edges, and runs after all
    // surfaces have finished.
    protected override void DrawInstanceBoundsBoxes()
        => DrawBoundsBoxes(DXPrimitiveGroup.lightConstantBuffers[(int)Device.FrameIndex]);

    protected override void DrawInstanceShellBoxes()
        => DrawShellBoxes(DXPrimitiveGroup.lightConstantBuffers[(int)Device.FrameIndex], CurrentInstanceBuffer, CurrentInstanceBufferView);

    void DrawPrimitiveInstances(PrimitiveData primitiveData, ID3D12Resource* lightConstantBuffer, uint instanceCount, uint startInstanceLocation)
    {
        int fi = (int)Device.FrameIndex;
        // A stack copy of this frame's view, taken by address directly: a local is already a fixed expression, so
        // wrapping it in fixed is both redundant and rejected by the compiler.
        var instanceVBValue = CurrentInstanceBufferView;
        VertexBufferView* instanceVB = &instanceVBValue;
        Pipeline.DrawPrimitive(primitiveData, lightConstantBuffer, _matrixBuffers[fi], instanceVB, instanceCount, startInstanceLocation,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(), CurrentInstanceBuffer,
            instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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
        // Not a range test against MaxSlots: the far slot and the local cube slots sit inside that range, but the chain never writes their
        // arg rows, so accepting one here would submit an empty batch where the whole-batch draw below would have been correct.
        if (!Season.Rendering.InstanceCull.ShadowSlotActive(slot))
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
    /// The loop, the shared-material decision and the skip accounting live in
    /// <c>PrimitiveGroupCore.DrawInstancedShadowPass</c>.
    /// </summary>
    public override void DrawShadow() => DrawInstancedShadowPass();

    protected override void OnBeginShadowPass()
    {
        DrawTrace.Group("dx", "shadow", Name, _opaquePrimitives.Count);
        Pipeline.SetShadowGroupBindings(GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(),
            GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle());
    }

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginInstancePrimitive(PrimitiveData primitive) => true;

    protected override void DrawShadowInstance(PrimitiveData primitive, int streamIndex, bool bindMaterial)
    {
        int fi = (int)Device.FrameIndex;

        // A7: when this frame's culling chain is ready, the instance stream reads this slot's reorder segment and
        // the instance count comes from the indirect args. The parameter table is addressed at a fixed length per
        // (slot, primitive), so one ExecuteIndirect still maps to exactly one primitive: indirect only changes
        // where the parameters come from, it does not merge draws.
        if (TryGetShadowSegment(0, out var slotView, out var argsResource, out int slot))
        {
            Pipeline.DrawShadowPrimitiveIndirect(primitive, _matrixBuffers[fi], &slotView,
                argsResource, Season.Rendering.InstanceCull.ArgsOffset(slot, streamIndex),
                bindMaterial: bindMaterial);
            return;
        }

        var shadowVBValue = CurrentInstanceBufferView;
        VertexBufferView* shadowVB = &shadowVBValue;
        Pipeline.DrawShadowPrimitive(primitive, _matrixBuffers[fi], shadowVB,
            (uint)_instanceCount, 0, bindMaterial: bindMaterial);
    }

    /// <summary>
    /// Outline2D mask: rendered through the instance stream.
    /// Host-wide activation draws a single full batch; per-instance activation
    /// draws instanceCount=1 for each writeIndex slot.
    /// The base class's non-instanced DrawOutlineMask would draw the instance
    /// stream with an identity matrix, causing missing / misaligned instances, so
    /// this override is required.
    /// Transparent buckets are skipped, matching the base-class mask semantics.
    /// The loop and the slot guard live in <c>PrimitiveGroupCore.DrawInstancedOutlineMask</c>.
    /// </summary>
    public override void DrawOutlineMask() => DrawInstancedOutlineMask();

    /// <summary>Root constant b6: one colour for the whole group. Several colours may occur in the same
    /// frame; the value is the group colour fixed during UpdateInstanceData.</summary>
    protected override void OnBeginOutlineMaskPass() => Pipeline.SetOutlineMaskColor(_outline2DColor);

    /// <summary>A generated group owns one group-wide stream, so every opaque primitive is drawable.</summary>
    protected override bool TryBeginOutlineMaskPrimitive(PrimitiveData primitive)
    {
        Pipeline.SetPipeline(PipelineMode.Opaque,
            primitive.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
        return true;
    }

    protected override void DrawOutlineMaskBatch(PrimitiveData primitive)
    {
        int fi = (int)Device.FrameIndex;
        var instanceVBValue = CurrentInstanceBufferView;
        VertexBufferView* instanceVB = &instanceVBValue;
        Pipeline.DrawPrimitive(primitive, DXPrimitiveGroup.lightConstantBuffers[fi], _matrixBuffers[fi], instanceVB,
            (uint)_instanceCount, 0,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(),
            CurrentInstanceBuffer, instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
    }

    protected override void DrawOutlineMaskInstance(PrimitiveData primitive, int instanceIndex, int slot)
    {
        int fi = (int)Device.FrameIndex;
        var instanceVBValue = CurrentInstanceBufferView;
        VertexBufferView* instanceVB = &instanceVBValue;
        Pipeline.DrawPrimitive(primitive, DXPrimitiveGroup.lightConstantBuffers[fi], _matrixBuffers[fi], instanceVB,
            1, (uint)instanceIndex,
            GetInstanceBoneSrvHandle(), GetPrevBoneSrvHandle(), GetPrevInstanceWorldSrvHandle(), GetPrevMorphSrvHandle(),
            CurrentInstanceBuffer, instanceExtrasSrvHandle: GetInstanceExtrasSrvHandle());
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

    /// <summary>2-3 Step C (tier C-a): SRV of the previous frame's instance worlds. The "previous frame" is the
    /// ring slot that frame wrote, not a copy of anything, so this is a handle swap by construction.</summary>
    protected GpuDescriptorHandle GetPrevInstanceWorldSrvHandle()
    {
        int slot = PrevFrameSlot;
        if ((uint)slot >= (uint)_instanceWorldBuffers.Length)
            return default;
        return DXSceneDrawCollection.Structured(_instanceWorldBuffers[slot], 64, _instanceWorldSrvHandles[slot]);
    }

    /// <summary>2-3 Step C (tier C-a): copy this frame's instance worlds into this frame's ring slot. A derived class
    /// that fills its own world array (instancing through per-primitive streams) calls this after its loop; the base
    /// class already calls it inside <see cref="UpdateInstanceData"/>. The previous frame's data is never built here:
    /// it is the slot that frame wrote.</summary>
    protected void UploadInstanceWorldsForFrame(Matrix4x4[] worlds, int count)
    {
        int slot = FrameSlot;
        if ((uint)slot >= (uint)_mappedInstanceWorldBuffers.Length || _mappedInstanceWorldBuffers[slot] == null)
            return;
        if (count <= 0 || worlds == null)
            return;
        if (count > _instanceWorldCapacity)
            count = _instanceWorldCapacity;
        fixed (Matrix4x4* pSrc = worlds)
            Unsafe.CopyBlock(_mappedInstanceWorldBuffers[slot], pSrc, (uint)(count * sizeof(Matrix4x4)));
    }

    // ============================================================
    // A7 per-instance GPU culling: resources and transition
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => _instanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _opaquePrimitives.Count;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds
        => CullPerPrimitiveStreams
            ? _cullSource
            : (uint)FrameSlot < (uint)_cullSourceSlots.Length ? _cullSourceSlots[FrameSlot] : null;

    int Season.Rendering.IInstanceCullTarget.CullStreamCount => _cullSorted.Length;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSource(int stream)
    {
        if (_cullStreamSourceSlots.Length == 0)
            return stream == 0 ? ((Season.Rendering.IInstanceCullTarget)this).CullSourceWorlds : null;
        if ((uint)stream >= (uint)_cullStreamSourceSlots.Length)
            return null;
        var slots = _cullStreamSourceSlots[stream];
        return (uint)FrameSlot < (uint)slots.Length ? slots[FrameSlot] : null;
    }

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullStreamSorted(int stream)
        => (uint)stream < (uint)_cullSorted.Length ? _cullSorted[stream] : null;

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
            || _cullStreamSourceSlots.Length != (perPrimitive ? streams : 0))
        {
            ReleaseCullResources();
            var sorted = new Season.Rendering.StorageBuffer?[streams];
            var streamSourceSlots = perPrimitive
                ? new Season.Rendering.StorageBuffer?[streams][]
                : Array.Empty<Season.Rendering.StorageBuffer?[]>();
            bool complete = true;
            try
            {
                var g = Season.Basic.Graphics.Instance;
                // A7c: only a per-primitive-stream group owns a cull input. Its root instance worlds are not what any IA stream carries
                // (each stream is "node world × instance world"), so they have to be uploaded somewhere; a single-stream group instead
                // aliases the instance buffer it already uploaded (EnsureBorrowedCullSources), which is where the duplicate transfer used to be.
                if (perPrimitive)
                    _cullSource = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                for (int s = 0; s < streams; s++)
                {
                    sorted[s] = g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(capacity),
                        Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
                }
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                // The args buffer is the only one that must declare the indirect usage: all four backends bake that
                // usage into the native object, and it cannot be added afterward.
                // A7b: it is sized ArgsTotalBytes, not ArgsBytes -- the per-slot visible-count accumulator lives in the tail of the same
                // block, which is what lets the per-frame reset dispatch go away without needing a second buffer or a second upload path.
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsTotalBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] Buffer creation failed: {ex.Message}");
            }

            // Hand the arrays over before checking, so a partial failure is released through the one path below.
            _cullSorted = sorted;
            _cullStreamSourceSlots = streamSourceSlots;
            foreach (var b in sorted)
                complete &= b != null;

            // The aliased inputs are deliberately not required here: they are created after this block from buffers this group does not
            // own, and a missing one simply leaves this group on the direct path for the frame.
            if (!complete || _cullVisible == null || _cullArgs == null
                || (perPrimitive && _cullSource == null))
            {
                ReleaseCullResources();
                return;
            }

            _cullCapacity = capacity;
            _cullPrimitiveCount = -1;
        }

        EnsureBorrowedCullSources(streams);

        EnsureCullPrimitiveInfo();

        if (!_cullRegistered)
        {
            Season.Rendering.InstanceCull.Register(this);
            _cullRegistered = true;
        }
    }

    /// <summary>
    /// A7c: keep the read-only cull aliases in step with the instance buffers they point at.
    ///
    /// This cannot live inside the capacity branch of <see cref="EnsureCullResources"/>: that branch is keyed on the 64-aligned cull
    /// capacity, while the buffers behind it move on a different schedule -- a group's instance buffer is recreated on every exact
    /// instance-count change, and a glTF model rebuilds a primitive stream when its capacity grows. Keying the alias on the pointer
    /// rather than on the capacity is what keeps a descriptor from outliving the buffer it describes: the debug layer reports that as
    /// a stale descriptor, and without it the driver just reads whatever memory now lives there, showing up as instances drawn with
    /// another object's transforms rather than as an error.
    ///
    /// The size has to be part of that check, not just the pointer: releasing a COM object and creating a new one immediately can hand
    /// back the same address, and a descriptor whose NumElements still describes the old capacity would either read past a smaller
    /// resource or reject a larger one. Comparing the expected byte size catches both.
    /// </summary>
    void EnsureBorrowedCullSources(int streams)
    {
        int slots = FrameSlotCount;

        if (!CullPerPrimitiveStreams)
        {
            uint size = (uint)_instanceCapacity * Season.Rendering.InstanceCull.WorldStride;
            if (AliasesMatch(_cullSourceSlots, _instanceBuffers, slots, size))
                return;
            DisposeAliases(_cullSourceSlots);
            _cullSourceSlots = CreateAliases(_instanceBuffers, slots, size);
            return;
        }

        if (_cullStreamSourceSlots.Length != streams)
            return;
        for (int s = 0; s < streams; s++)
        {
            TryGetCullDrawStreamRing(s, out var ring, out int capacity);
            uint size = (uint)capacity * Season.Rendering.InstanceCull.WorldStride;
            if (AliasesMatch(_cullStreamSourceSlots[s], ring, slots, size))
                continue;
            DisposeAliases(_cullStreamSourceSlots[s]);
            _cullStreamSourceSlots[s] = CreateAliases(ring, slots, size);
        }
    }

    /// <summary>Whether <paramref name="aliases"/> already describes exactly the buffers in <paramref name="ring"/>.
    /// Every slot is checked, not just slot 0: the ring is created in one call today, but a future partial rebuild would
    /// otherwise leave one slot describing a released resource, and D3D12 reports that only as "instances drawn with
    /// another object's transforms".</summary>
    static bool AliasesMatch(Season.Rendering.StorageBuffer?[] aliases, ID3D12Resource*[] ring, int slots, uint size)
    {
        if (aliases == null || aliases.Length != slots || ring == null || ring.Length != slots)
            return false;
        for (int i = 0; i < slots; i++)
        {
            if (ring[i] == null)
            {
                if (aliases[i] != null)
                    return false;
                continue;
            }
            if (aliases[i] is not DXStorageBuffer alias || alias.Resource != ring[i] || alias.SizeInBytes != size)
                return false;
        }
        return true;
    }

    static Season.Rendering.StorageBuffer?[] CreateAliases(ID3D12Resource*[] ring, int slots, uint size)
    {
        var aliases = new Season.Rendering.StorageBuffer?[slots];
        if (size == 0 || ring == null)
            return aliases;
        for (int i = 0; i < slots && i < ring.Length; i++)
        {
            if (ring[i] != null)
                aliases[i] = DXStorageBuffer.CreateReadOnlyView(ring[i], size);
        }
        return aliases;
    }

    static void DisposeAliases(Season.Rendering.StorageBuffer?[] aliases)
    {
        if (aliases == null)
            return;
        foreach (var alias in aliases)
            alias?.Dispose();
    }

    /// <summary>
    /// Build only the parameter-table bytes, without touching the GPU -- the safe timing for building and
    /// uploading differs: building can be done anytime, but uploading must wait until the command list is open
    /// (see <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>). If the primitive set does not
    /// change, do not rebuild. The index range must share one source with the draw side: the arg template writes
    /// this range into the indirect args, and the draw side no longer provides one itself (see <see cref="GetCullPrimitiveRange"/>).
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullArgs == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // Explicitly record "there is no parameter table to upload": otherwise PrepareCullInputs would
            // re-upload last time's bytes over and over, where the entry count looks right but the content is stale.
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
        // A7b: the arg image is composed here as well, because it is a pure function of this table and the slot layout -- the same
        // bytes the reset kernel used to write every frame, just built once at the moment the input changes.
        _cullArgsTemplate = Season.Rendering.InstanceCull.BuildArgsTemplate(bytes, n);
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
        // A7b: this upload replaced the reset dispatch. What it writes is frame-invariant (the index ranges) or re-derived by the GPU
        // later in the same frame (the instance counts, published by gather), so paying for it once per primitive-set change instead of
        // once per frame is the whole point: one dispatch per group per frame was exactly the fixed cost that made small groups unprofitable.
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullArgsTemplate == null || _cullArgs == null)
                return false;
            if (!g.TryUpdateStorageBuffer(_cullArgs, _cullArgsTemplate))
                return false;
            _cullPrimitiveCount = _cullPrimInfoPending;
        }

        // A7c: a single-stream group is done here. Its cull input is a read-only alias of the instance buffer, which the engine already
        // filled for this frame (Device.SetVertexBuffer in Update); uploading those same bytes into a second buffer was pure duplication.
        if (!CullPerPrimitiveStreams)
            return true;

        // What still has to travel is the root-world stream: it is the only cull input with no IA counterpart, because each primitive
        // stream is "node world × instance world" and cannot be inverted back on the GPU. The per-primitive streams themselves are
        // aliases as well (EnsureBorrowedCullSources), so their former per-frame upload is gone too.
        if (_cullSource == null || !g.TryUpdateStorageBuffer(_cullSource,
                MemoryMarshal.AsBytes(_instanceData.AsSpan(0, _instanceCount))))
            return false;
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
        DisposeAliases(_cullSourceSlots);
        _cullSourceSlots = Array.Empty<Season.Rendering.StorageBuffer?>();
        foreach (var s in _cullSorted)
            s?.Dispose();
        _cullSorted = Array.Empty<Season.Rendering.StorageBuffer?>();
        foreach (var slots in _cullStreamSourceSlots)
            DisposeAliases(slots);
        _cullStreamSourceSlots = Array.Empty<Season.Rendering.StorageBuffer?[]>();
        _cullArgs?.Dispose(); _cullArgs = null;
        _cullVisible?.Dispose(); _cullVisible = null;
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimInfoBytes = null;
        _cullArgsTemplate = null;
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

        if (_instanceBuffers != null)
        {
            foreach (var buffer in _instanceBuffers)
            {
                if (buffer == null)
                    continue;
                buffer->Release();
            }
            _instanceBuffers = EmptyResourceRing;
            _instanceBufferViews = Array.Empty<VertexBufferView>();
        }

        // 2-3 Step C: release the instance-world ring
        foreach (var buffer in _instanceWorldBuffers)
        {
            if (buffer == null)
                continue;
            buffer->Unmap(0, null);
            buffer->Release();
        }
        _instanceWorldBuffers = EmptyResourceRing;
        _mappedInstanceWorldBuffers = EmptyMappedRing;
        foreach (int id in _instanceWorldDescriptorIds)
        {
            if (id >= 0)
                Device.DescriptorAllocator.Free(id);
        }
        _instanceWorldDescriptorIds = Array.Empty<int>();
        _instanceWorldSrvHandles = Array.Empty<GpuDescriptorHandle>();

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
