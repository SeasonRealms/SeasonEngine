// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>只保存不可变的本帧地址/视图，不保存控件、图元或重放 Draw 的委托。</summary>
internal unsafe struct DXSceneDrawCommand : ISceneDrawCommand
{
    [System.Runtime.CompilerServices.InlineArray(23)]
    internal struct Views { GpuDescriptorHandle _first; }
    internal Views Textures;
    internal PipelineMode Mode;
    internal PipelineCullVariant Cull;
    internal bool DepthWrite, Indexed;
    internal VertexBufferView Vertex, Instances;
    internal IndexBufferView Indices;
    internal ulong Matrix, Light, Material, Bones, Text;
    internal uint Count, InstanceCount, StartInstance, BoneBase;

    public void Draw()
    {
        Pipeline.SetPipeline(Mode, Cull, DepthWrite);
        Pipeline.SetOutlineMaskBoneBase(BoneBase);
        var cmd = Device.GraphicsCommandList;
        var vertices = stackalloc VertexBufferView[2] { Vertex, Instances };
        cmd->IASetVertexBuffers(0, 2, vertices);
        cmd->SetGraphicsRootConstantBufferView(0, Matrix);
        cmd->SetGraphicsRootConstantBufferView(1, Light);
        cmd->SetGraphicsRootConstantBufferView(2, Material);
        cmd->SetGraphicsRootConstantBufferView(8, Bones);
        cmd->SetGraphicsRootConstantBufferView(11, Text);
        for (uint root = 3; root < 23; root++)
            if (Textures[(int)root].Ptr != 0) cmd->SetGraphicsRootDescriptorTable(root, Textures[(int)root]);
        if (Indexed)
        {
            var indices = Indices;
            cmd->IASetIndexBuffer(&indices);
            cmd->DrawIndexedInstanced(Count, InstanceCount, 0, 0, StartInstance);
        }
        else cmd->DrawInstanced(Count, InstanceCount, 0, StartInstance);
    }

    // 快照属于 FrameContext，必须等 fence 完成；队列清空不能提前回收。
    public void Release() { }
}

/// <summary>主线程场景收集器。SetPipeline 仅记录状态，消费时才绑定真实 Pass 的 PSO。</summary>
internal unsafe sealed class DXSceneDrawCollection
{
    readonly SceneDrawQueue<DXSceneDrawCommand> _queue = new();
    ulong _stamp;
    bool _live;
    internal static DXSceneDrawCollection? Active { get; private set; }
    internal static bool SuppressFog { get; private set; }
    internal DXSceneDrawCommand State;
    internal static DXDrawSnapshotArena Arena => Device.FrameContexts[Device.FrameIndex].DrawSnapshots;
    internal int OpaqueCount => _queue.OpaqueCount;
    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (_live || Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("场景收集必须由帧调度器开启，且上一帧已经释放。");
        _queue.BeginCollection();
        _stamp = Device.GetCurrentRetireFenceValue();
        _live = true;
        _sceneCaptured = false;
        State = default;
        Active = this;
    }

    // 场景级纹理（阴影图/环境立方图/DDGI/Aerial LUT）每帧只解析一次，其余图元只拷贝句柄：
    // 每图元重建 5 次 SRV 视图并把描述符当作字典键，实测占收集耗时的百分之几。
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
    }

    internal void Seal()
    {
        Validate();
        if (Active != this) throw new InvalidOperationException("场景收集已封存。");
        _queue.Seal();
        Active = null;
    }

    void Validate()
    {
        if (!_live || _stamp != Device.GetCurrentRetireFenceValue())
            throw new InvalidOperationException("场景绘制项只能在收集的同一帧消费。");
    }

    internal void DrawOpaque()
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneOpaque) throw new InvalidOperationException("不透明队列的 Pass 无效。");
        _queue.DrawOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneTransparent) throw new InvalidOperationException("透明队列的 Pass 无效。");
        SuppressFog = !sampleFog;
        try { _queue.DrawTransparent(); }
        finally { SuppressFog = false; }
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

    // 3D 图元的 CB 全部是“每所有者 + 每帧槽位”（primitiveData.MatrixBuffers[fi]、
    // MaterialBuffers[fi]、每组光照 CB、每模型的骨骼 CB），帧内写一次就不再变；
    // 直绘路径本来就只把 GPU 地址交给命令列表，所以这里同样只保留引用，不逐帧深复制。
    // 2D 四边形/文本仍走 Constant 深复制：那条链上的矩阵 CB 可能是跨图元复用的环。
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
    {
        var point = Vector3.Transform(center, world);
        var app = DeviceServices.BaseApp;
        if (app == null) return point.Z;
        var forward = app.CameraTarget - app.CameraPos;
        forward = forward.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(forward);
        return Vector3.Dot(point - app.CameraPos, forward);
    }

    internal void Add(in DXSceneDrawCommand command, float depth)
        => _queue.Add(command, command.Mode != PipelineMode.Opaque, FrameSchedule.SceneDrawOrder.Current, depth);

    internal void CapturePrimitive(PrimitiveData primitive, ID3D12Resource* light, ID3D12Resource* matrix,
        VertexBufferView* instanceView, ID3D12Resource* instanceResource, uint count, uint start,
        GpuDescriptorHandle bones, GpuDescriptorHandle prevBones, GpuDescriptorHandle prevWorld, GpuDescriptorHandle prevMorph)
    {
        if (count == 0) return;
        var draw = State;
        int fi = (int)Device.FrameIndex;
        draw.Matrix = ConstantRef(matrix);
        draw.Light = ConstantRef(light);
        draw.Material = ConstantRef(primitive.MaterialBuffers[fi]);
        var geometry = primitive.SharedGeometrySource ?? primitive;
        // 顶点/索引缓冲在帧内不变（直绘路径同样直接把原始地址交给命令列表），所以只保留引用。
        // 逐帧深复制会让每帧二十多 MB 几何数据穿过 write-combined 上传页，实测占收集耗时的九成。
        Arena.Retain(geometry.VertexBuffer);
        Arena.Retain(geometry.IndexBuffer);
        draw.Vertex = primitive.VertexBufferView;
        draw.Indices = primitive.IndexBufferView;
        draw.Instances = instanceView == null ? Pipeline.IdentityInstanceBufferView : Vertex(instanceResource, *instanceView);
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

    internal void CaptureQuad(ID3D12Resource* vertex, VertexBufferView view, ID3D12Resource* matrix,
        ID3D12Resource* material, DXTexture albedo, DXTexture? normal, DXTexture? metallic, DXTexture? occlusion, DXTexture? emissive)
    {
        var draw = new DXSceneDrawCommand { Mode = PipelineMode.Transparent, Cull = PipelineCullVariant.Back,
            Matrix = Constant(matrix, (uint)sizeof(MatrixBuffer)), Material = Constant(material, (uint)sizeof(MaterialParams)),
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
