// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>Stores only this frame's immutable addresses/views, not controls, primitives, or a delegate to replay Draw.</summary>
internal unsafe struct DXSceneDrawCommand : ISceneDrawCommand
{
    // 29 rather than 23: the Sky-View LUT of 2-7 P0 is attached at root parameter 28 (see Pipeline.SkyViewRoot).
    // 23..27 are not descriptor tables in the root signature (t16 is a root SRV, b6/b7 are root constants, b8 is a CBV),
    // so the replay loop only goes up to 22, and index 28 is bound separately.
    [System.Runtime.CompilerServices.InlineArray(29)]
    internal struct Views { GpuDescriptorHandle _first; }
    internal Views Textures;
    internal PipelineMode Mode;
    internal PipelineCullVariant Cull;
    internal bool DepthWrite, Indexed;
    internal VertexBufferView Vertex, Instances;
    internal IndexBufferView Indices;
    internal ulong Matrix, View, Light, Material, Bones, Text;
    internal uint Count, InstanceCount, StartInstance, BoneBase;
    // A7 main-view draw: non-null means the five draw parameters come from this GPU-written record rather than
    // Count / InstanceCount / StartInstance. Only the reference is retained -- the record does not exist on the CPU to copy.
    internal ID3D12Resource* Args;
    internal uint ArgsOffset;

    public void Draw()
    {
        Pipeline.SetPipeline(Mode, Cull, DepthWrite);
        Pipeline.SetOutlineMaskBoneBase(BoneBase);
        var cmd = Device.GraphicsCommandList;
        var vertices = stackalloc VertexBufferView[2] { Vertex, Instances };
        cmd->IASetVertexBuffers(0, 2, vertices);
        cmd->SetGraphicsRootConstantBufferView(0, Matrix);
        cmd->SetGraphicsRootConstantBufferView(Pipeline.ViewConstantsRoot, View);
        cmd->SetGraphicsRootConstantBufferView(1, Light);
        cmd->SetGraphicsRootConstantBufferView(2, Material);
        cmd->SetGraphicsRootConstantBufferView(8, Bones);
        cmd->SetGraphicsRootConstantBufferView(11, Text);
        for (uint root = 3; root < 23; root++)
            if (Textures[(int)root].Ptr != 0) cmd->SetGraphicsRootDescriptorTable(root, Textures[(int)root]);
        if (Textures[(int)Pipeline.SkyViewRoot].Ptr != 0)
            cmd->SetGraphicsRootDescriptorTable(Pipeline.SkyViewRoot, Textures[(int)Pipeline.SkyViewRoot]);
        if (Args != null)
        {
            // The capture side only takes this path once the signature exists, so null here would be a lifetime bug, not a fallback case.
            var signature = Pipeline.EnsureShadowIndirectSignature();
            if (signature == null) return;
            var indices = Indices;
            cmd->IASetIndexBuffer(&indices);
            cmd->ExecuteIndirect(signature, 1, Args, ArgsOffset, null, 0);
        }
        else if (Indexed)
        {
            var indices = Indices;
            cmd->IASetIndexBuffer(&indices);
            cmd->DrawIndexedInstanced(Count, InstanceCount, 0, 0, StartInstance);
        }
        else cmd->DrawInstanced(Count, InstanceCount, 0, StartInstance);
    }

    // The snapshot belongs to the FrameContext and must wait for the fence to complete; clearing the queue cannot reclaim it early.
    public void Release() { }
}

/// <summary>Main-thread scene collector. SetPipeline only records state; the real Pass PSO is bound at consumption time.</summary>
internal unsafe sealed class DXSceneDrawCollection
{
    readonly SceneDrawQueue<DXSceneDrawCommand> _queue = new();
    ulong _stamp;
    bool _live;
    internal static DXSceneDrawCollection? Active { get; private set; }
    /// <summary>S5: the positive gate "whether this view has fog", replacing the former SuppressFog negative semantics.
    /// Set by DrawTransparent(sampleFog) during replay; BindSceneFog uses it to decide between the real composite binding and the zero-gate bypass.</summary>
    internal static bool ViewHasFog { get; private set; }
    internal DXSceneDrawCommand State;
    internal static DXDrawSnapshotArena Arena => Device.FrameContexts[Device.FrameIndex].DrawSnapshots;
    internal int OpaqueCount => _queue.OpaqueCount;
    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (_live || Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("Scene collection must be opened by the frame scheduler, and the previous frame must already be released.");
        _queue.BeginCollection();
        _stamp = Device.GetCurrentRetireFenceValue();
        _live = true;
        _sceneCaptured = false;
        State = default;
        Active = this;
    }

    // Scene-level textures (shadow map / environment cubemap / DDGI / Aerial LUT) are resolved only once per
    // frame, and the remaining primitives only copy the handle: rebuilding 5 SRV views per primitive and using
    // the descriptor as a dictionary key measured a few percent of the collection time.
    DXSceneDrawCommand _sceneTemplate;
    bool _sceneCaptured;

    void CaptureSceneTextures(ref DXSceneDrawCommand draw)
    {
        if (!_sceneCaptured)
        {
            _sceneTemplate = draw;
            Pipeline.CaptureSceneTextures(ref _sceneTemplate);
            _sceneCaptured = true;
        }
        draw.Textures[12] = _sceneTemplate.Textures[12];
        draw.Textures[17] = _sceneTemplate.Textures[17];
        draw.Textures[18] = _sceneTemplate.Textures[18];
        draw.Textures[19] = _sceneTemplate.Textures[19];
        draw.Textures[21] = _sceneTemplate.Textures[21];
        draw.Textures[22] = _sceneTemplate.Textures[22];
        draw.Textures[(int)Pipeline.SkyViewRoot] = _sceneTemplate.Textures[(int)Pipeline.SkyViewRoot];
    }

    internal void Seal()
    {
        Validate();
        if (Active != this) throw new InvalidOperationException("The scene collection is already sealed.");
        _queue.Seal();
        Active = null;
    }

    void Validate()
    {
        if (!_live || _stamp != Device.GetCurrentRetireFenceValue())
            throw new InvalidOperationException("Scene draw items can only be consumed in the same frame they were collected.");
    }

    internal void DrawOpaque()
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneOpaque) throw new InvalidOperationException("Invalid Pass for the opaque queue.");
        _queue.DrawOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneTransparent) throw new InvalidOperationException("Invalid Pass for the transparent queue.");
        ViewHasFog = sampleFog;
        try { _queue.DrawTransparent(); }
        finally { ViewHasFog = false; }
    }

    internal void Release()
    {
        if (Active == this) Active = null;
        try { _queue.Release(); }
        finally { _live = false; State = default; }
    }

    internal void SetPipeline(PipelineMode mode, PipelineCullVariant cull, bool depthWrite)
    {
        State = new DXSceneDrawCommand { Mode = mode, Cull = cull, DepthWrite = depthWrite,
            Bones = Pipeline.DefaultTextDrawParamsGpuAddress, Text = Pipeline.DefaultTextDrawParamsGpuAddress };
    }

    internal static T ReadBuffer<T>(ID3D12Resource* source, ulong offset = 0) where T : unmanaged
    {
        if (source == null || offset > source->GetDesc().Width || (ulong)sizeof(T) > source->GetDesc().Width - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        void* mapped;
        int hr = source->Map(0, null, &mapped);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { return Unsafe.ReadUnaligned<T>((byte*)mapped + offset); }
        finally { Silk.NET.Direct3D12.Range written = default; source->Unmap(0, &written); }
    }

    internal static ulong Constant(ID3D12Resource* source, uint size)
        => Arena.CaptureBuffer(source, 0, size).GpuAddress;

    // The CBs of 3D primitives are all "per-owner + per-frame slot" (primitiveData.MatrixBuffers[fi],
    // MaterialBuffers[fi], each light group CB, each model's skeleton CB), written once within the frame
    // and unchanged thereafter; the direct draw path already hands only the GPU address to the command
    // list, so only a reference is retained here as well, with no per-frame deep copy.
    // 2D quads/text still go through the Constant deep copy: the matrix CB on that path may be a per-primitive reuse ring.
    internal static ulong ConstantRef(ID3D12Resource* source)
    {
        Arena.Retain(source);
        return source->GetGPUVirtualAddress();
    }

    internal static GpuDescriptorHandle Structured(ID3D12Resource* source, uint stride, GpuDescriptorHandle fallback)
        => Active == null || source == null ? fallback
            : Arena.CaptureStructuredBuffer(source, stride, checked((uint)(source->GetDesc().Width / stride)));

    internal static GpuDescriptorHandle Texture(DXTexture? texture)
    {
        texture ??= Device.White;
        texture.EnsureReadyForRendering(Device.GraphicsCommandList);
        var resource = texture._textureResource;
        var native = resource->GetDesc();
        return Arena.CaptureView(resource, new ShaderResourceViewDesc {
            Format = native.Format, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688,
            Texture2D = new() { MipLevels = native.MipLevels } });
    }

    internal static VertexBufferView Vertex(ID3D12Resource* resource, VertexBufferView view)
    {
        var slice = Arena.CaptureBuffer(resource, checked(view.BufferLocation - resource->GetGPUVirtualAddress()), view.SizeInBytes, 16);
        view.BufferLocation = slice.GpuAddress;
        return view;
    }

    internal static float Depth(Vector3 center, Matrix4x4 world)
        => RenderView.SortDepth(Vector3.Transform(center, world));

    internal void Add(in DXSceneDrawCommand command, float depth)
        => _queue.Add(command, command.Mode != PipelineMode.Opaque, FrameSchedule.SceneDrawOrder.Current, depth);

    internal void CapturePrimitive(PrimitiveData primitive, ID3D12Resource* light, ID3D12Resource* matrix,
        VertexBufferView* instanceView, ID3D12Resource* instanceResource, uint count, uint start,
        GpuDescriptorHandle bones, GpuDescriptorHandle prevBones, GpuDescriptorHandle prevWorld, GpuDescriptorHandle prevMorph,
        ID3D12Resource* args = null, uint argsOffset = 0, ID3D12Resource* depthSource = null)
    {
        if (count == 0 && args == null) return;
        var draw = State;
        int fi = (int)Device.FrameIndex;
        draw.Matrix = ConstantRef(matrix);
        draw.View = ConstantRef(Pipeline.ActiveViewConstantBuffer);
        draw.Light = ConstantRef(light);
        draw.Material = ConstantRef(primitive.MaterialBuffers[fi]);
        var geometry = primitive.SharedGeometrySource ?? primitive;
        // The vertex/index buffers are unchanged within the frame (the direct draw path likewise hands the raw
        // address to the command list), so only a reference is retained. A per-frame deep copy would push over
        // twenty MB of geometry data per frame through the write-combined upload page, measured at ninety percent of the collection time.
        Arena.Retain(geometry.VertexBuffer);
        Arena.Retain(geometry.IndexBuffer);
        draw.Vertex = primitive.VertexBufferView;
        draw.Indices = primitive.IndexBufferView;
        draw.Indexed = true;
        draw.Count = (uint)primitive.Indices.Length;
        draw.Textures[3] = Texture(primitive.BaseColorTexture);
        draw.Textures[4] = Texture(primitive.NormalTexture);
        draw.Textures[5] = Texture(primitive.MetallicRoughnessTexture);
        draw.Textures[6] = Texture(primitive.OcclusionTexture);
        draw.Textures[7] = Texture(primitive.EmissiveTexture);
        draw.Textures[9] = Structured(primitive.MorphDeltasBuffer, 4, Pipeline.DefaultMorphDeltasSrvHandle);
        draw.Textures[10] = bones.Ptr != 0 ? bones : Pipeline.DefaultInstanceBoneSrvHandle;
        draw.Textures[14] = prevBones.Ptr != 0 ? prevBones : Pipeline.DefaultPrevBoneSrvHandle;
        draw.Textures[15] = prevWorld.Ptr != 0 ? prevWorld : Pipeline.DefaultPrevInstanceWorldSrvHandle;
        draw.Textures[16] = prevMorph.Ptr != 0 ? prevMorph : Pipeline.DefaultPrevMorphWeightsSrvHandle;
        CaptureSceneTextures(ref draw);
        var world = Matrix4x4.Transpose(ReadBuffer<MatrixBuffer>(matrix).World);
        if (args != null && instanceView != null)
        {
            // A7 main-view segment: GPU-written by this frame's gather, which is recorded but has not executed when this capture runs,
            // so neither the deep copy nor the depth read below can apply -- the bytes do not exist yet. Retaining the two buffers is enough:
            // nothing else writes them before the replay (the next gather is recorded after it, on the same queue).
            // The sort depth comes from instance 0 of the CPU stream, the same record the direct draw would use, so switching
            // paths never reorders the opaque queue.
            Arena.Retain(instanceResource);
            Arena.Retain(args);
            draw.Instances = *instanceView;
            draw.Args = args;
            draw.ArgsOffset = argsOffset;
            draw.InstanceCount = 0;
            draw.StartInstance = 0;
            draw.BoneBase = 0;
            if (depthSource != null)
                world = ReadBuffer<Matrix4x4>(depthSource);
            Add(draw, Depth(primitive.LocalBoundsCenter, world));
            return;
        }
        draw.Instances = instanceView == null ? Pipeline.IdentityInstanceBufferView : Vertex(instanceResource, *instanceView);
        bool splitInstances = instanceView != null && draw.Mode != PipelineMode.Opaque;
        for (uint i = 0; i < (splitInstances ? count : 1); i++)
        {
            draw.InstanceCount = splitInstances ? 1 : count;
            draw.StartInstance = start + i;
            draw.BoneBase = draw.StartInstance;
            if (instanceView != null)
                world = ReadBuffer<Matrix4x4>(instanceResource,
                    instanceView->BufferLocation - instanceResource->GetGPUVirtualAddress() + (ulong)draw.StartInstance * instanceView->StrideInBytes);
            Add(draw, Depth(primitive.LocalBoundsCenter, world));
        }
    }

    internal void CaptureQuad(ID3D12Resource* vertex, VertexBufferView view, ID3D12Resource* matrix, bool screenSpace,
        ID3D12Resource* material, DXTexture albedo, DXTexture? normal, DXTexture? metallic, DXTexture? occlusion, DXTexture? emissive)
    {
        var draw = new DXSceneDrawCommand { Mode = PipelineMode.Transparent, Cull = PipelineCullVariant.Back,
            Matrix = Constant(matrix, (uint)sizeof(MatrixBuffer)), Material = Constant(material, (uint)sizeof(MaterialParams)),
            View = screenSpace ? Pipeline.ScreenViewGpuAddress : ConstantRef(Pipeline.ActiveViewConstantBuffer),
            Light = Constant(DXPrimitiveGroup.lightConstantBuffers[Device.FrameIndex], (uint)sizeof(SceneLightParams)),
            Bones = Pipeline.DefaultTextDrawParamsGpuAddress, Text = Pipeline.DefaultTextDrawParamsGpuAddress,
            Vertex = Vertex(vertex, view), Instances = Pipeline.IdentityInstanceBufferView, Count = 6, InstanceCount = 1 };
        draw.Textures[3] = Texture(albedo); draw.Textures[4] = Texture(normal); draw.Textures[5] = Texture(metallic);
        draw.Textures[6] = Texture(occlusion); draw.Textures[7] = Texture(emissive);
        Defaults(ref draw);
        Add(draw, Depth(Vector3.Zero, Matrix4x4.Transpose(ReadBuffer<MatrixBuffer>(matrix).World)));
    }

    internal void CaptureText(ID3D12Resource* matrix, ID3D12Resource* instances, VertexBufferView view,
        ID3D12Resource* glyphs, DXTexture? atlas, uint count, in TextDrawParams parameters)
    {
        var draw = new DXSceneDrawCommand { Mode = PipelineMode.Transparent, Cull = PipelineCullVariant.None,
            Matrix = Constant(matrix, (uint)sizeof(MatrixBuffer)), Material = Pipeline.DefaultTextMaterialGpuAddress,
            View = Pipeline.ScreenViewGpuAddress,
            Light = Constant(DXPrimitiveGroup.lightConstantBuffers[Device.FrameIndex], (uint)sizeof(SceneLightParams)),
            Bones = Pipeline.DefaultTextDrawParamsGpuAddress,
            Text = Arena.CaptureBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in parameters), 1))).GpuAddress,
            Vertex = Pipeline.UnitQuadVertexBufferView, Instances = Vertex(instances, view), Indices = Pipeline.UnitQuadIndexBufferView,
            Indexed = true, Count = 6, InstanceCount = count };
        draw.Textures[3] = Texture(atlas);
        for (int i = 4; i <= 7; i++) draw.Textures[i] = Texture(null);
        Defaults(ref draw);
        draw.Textures[9] = Structured(glyphs, 4, Pipeline.DefaultMorphDeltasSrvHandle);
        Add(draw, Depth(Vector3.Zero, ReadBuffer<Matrix4x4>(instances)));
    }

    void Defaults(ref DXSceneDrawCommand draw)
    {
        draw.Textures[9] = Pipeline.DefaultMorphDeltasSrvHandle;
        draw.Textures[10] = Pipeline.DefaultInstanceBoneSrvHandle;
        draw.Textures[14] = Pipeline.DefaultPrevBoneSrvHandle;
        draw.Textures[15] = Pipeline.DefaultPrevInstanceWorldSrvHandle;
        draw.Textures[16] = Pipeline.DefaultPrevMorphWeightsSrvHandle;
        CaptureSceneTextures(ref draw);
    }
}
