// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>三种绘制命令形态：3D 图元、精灵四边形、文字批次。三者都走描述符集 + 顶点缓冲。</summary>
internal enum VKSceneDrawKind : byte
{
    Primitive,
    Quad,
    Text,
}

/// <summary>
/// 单个已捕获的绘制项。只保存不可变句柄（描述符集、顶点/索引/实例缓冲、计数），
/// 不保存控件、也不保存延迟调用 <c>Control.Draw</c> 的委托。
///
/// 与 DX 版本的差别只有一处：VK 的旋转环本来就是“所有者 + 帧槽”独占的
/// （<c>PrimitiveData.MatrixBuffers[fi]</c>、<c>VKSpriteQuad._matrixBuffers[fi]</c>、
/// 文字各自按帧槽的实例/参数缓冲），直绘路径只把句柄交给命令缓冲，
/// 帧内没有谁会覆写别人的槽位，因此这里不需要 DX 那种上传页字节深拷贝。
/// </summary>
internal unsafe struct VKSceneDrawCommand : ISceneDrawCommand
{
    internal VKSceneDrawKind Kind;
    internal PipelineMode Mode;
    internal CullModeFlags Cull;
    internal bool DepthWrite;

    // Primitive
    internal PrimitiveData? Primitive;

    // 三者共用：描述符集 + 顶点/索引/实例缓冲
    internal DescriptorSet Set;
    // 视图 set 2（主视图帧槽集合或屏幕空间集合），收集时确定。
    internal DescriptorSet View;
    internal VkBuffer Vertex;
    internal VkBuffer Index;
    internal VkBuffer Instances;
    internal IndexType IndexKind;
    internal uint IndexCount;
    internal uint InstanceCount;
    internal uint FirstInstance;

    public void Draw()
    {
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetPipeline(cmd, Mode, Cull, DepthWrite);

        switch (Kind)
        {
            case VKSceneDrawKind.Primitive:
                // 纹理就绪与描述符刷新已在收集阶段完成：
                // 那些操作包含布局转换，只能在渲染 Pass 之外调用。
                Pipeline.DrawPrimitiveCore(cmd, Primitive!, Vertex, Index, Set, IndexCount,
                    Instances, InstanceCount, FirstInstance, prepared: true, View);
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

    // 资源引用由 <see cref="VKSceneDrawCollection"/> 的快照页持有，直到本帧 GPU 退休。
    public void Release() { }
}

/// <summary>
/// Vulkan 主线程场景收集器。收集期不提交任何绘制：
/// <c>Pipeline.SetPipeline</c> 只记录模式/剔除/深度写入，真正绑定 PSO 发生在重放时
/// （重放的 Pass 才知道该用哪一族 PSO：主场景、速度 MRT、Overlay、OutlineMask）。
/// </summary>
internal sealed unsafe class VKSceneDrawCollection
{
    readonly SceneDrawQueue<VKSceneDrawCommand> _queue = new();
    readonly VKDrawSnapshotArena _arena = new();

    ulong _stamp;
    bool _live;

    /// <summary>正在收集的实例；非收集期为空，重放不会再被自己捕获。</summary>
    internal static VKSceneDrawCollection? Active { get; private set; }

    // 本帧/上一帧实际绘制过的图元，按引用去重。
    // binding 16-20 只有在命令缓冲 begin 之前改写才安全（否则校验层把整条命令缓冲判为 invalid），
    // 而场景收集发生在 begin 之后，所以帧首预刷新只能依据上一帧的绘制集合。
    // 见 Pipeline.RefreshSceneBindingsForFrameStart。
    static HashSet<PrimitiveData> _drawnThisFrame = new();
    static HashSet<PrimitiveData> _drawnLastFrame = new();
    static HashSet<VKSpriteQuad> _quadThisFrame = new();
    static HashSet<VKSpriteQuad> _quadLastFrame = new();

    /// <summary>上一帧实际绘制过的图元集合，供帧首预刷新遍历。</summary>
    internal static IReadOnlyCollection<PrimitiveData> PreviousFramePrimitives => _drawnLastFrame;

    /// <summary>上一帧实际绘制过的精灵集合，同样供帧首预刷新遍历。</summary>
    internal static IReadOnlyCollection<VKSpriteQuad> PreviousFrameQuads => _quadLastFrame;

    /// <summary>
    /// 登记一个本帧渲染过的图元，作为下一帧帧首预刷新的依据。
    /// 主通道收集与阴影通道都会调：阴影的绘制集合是它自己的剔除结果，不一定被主通道完整覆盖，
    /// 而两者刷新的又是同一批描述符集。非收集期调用也是安全的。
    /// </summary>
    internal static void NotePrimitiveDrawn(PrimitiveData primitive) => _drawnThisFrame.Add(primitive);

    /// <summary>登记一个本帧绘制过的精灵，用途同 <see cref="NotePrimitiveDrawn"/>。</summary>
    internal static void NoteQuadDrawn(VKSpriteQuad quad) => _quadThisFrame.Add(quad);

    // 雾采样开关只在状态翻转时记一行日志，方便在真机上确认“透明阶段到底有没有采样雾”。
    static bool? _reportedFog;

    /// <summary>当前状态：最近一次 SetPipeline 记录下来的管线状态。</summary>
    internal VKSceneDrawCommand State;

    internal int OpaqueCount => _queue.OpaqueCount;

    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (_live || Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("场景收集必须由帧调度器开启，且上一帧已经释放。");
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
        if (Device.ActivePassId != RenderPassId.SceneOpaque)
            throw new InvalidOperationException("不透明队列的 Pass 无效。");
        // 不透明变体没有雾采样代码，但 SceneFogParams 是跨帧的静态状态，必须显式归零。
        Pipeline.SceneFogParams = default;
        _queue.DrawOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        Validate();
        if (Device.ActivePassId != RenderPassId.SceneTransparent)
            throw new InvalidOperationException("透明队列的 Pass 无效。");

        bool fog = false;
        if (sampleFog && Pipeline.SceneFogSamplingBaked
            && VKFogCompositePipeline.TryGetTransparentBinding(out var frame, out var frameRange,
                out var integrated, out var local))
        {
            // 雾的体积纹理是本帧合成阶段才产出的资源，收集期拿不到句柄，
            // 所以三段绑定只能在透明 Pass 内补齐。
            // 写入的是独立的 set 1（每帧槽一个），不是绘制项自己的 set 0：
            // set 0 在本帧更早的阴影 pass 里已经被绑定使用过，
            // 未声明 UPDATE_AFTER_BIND 的集合被正在录制的命令缓冲引用后再改写，整条缓冲会失效。
            // 顺序仍然是“先写完，再开始绘制”。
            Pipeline.PrepareTransparentFog(frame, frameRange, integrated, local);
            fog = true;
        }

        Pipeline.SceneFogParams = new Vector4(fog ? 1 : 0, FrameSchedule.WeatherComposited ? 1 : 0, 0, 0);
        var app = DeviceServices.BaseApp;
        if (app != null && _reportedFog != fog)
        {
            _reportedFog = fog;
            app.AddLog(Season.Basic.LogType.Backend,
                $"{DateTime.UtcNow} [VK Scene] 透明阶段雾采样={(fog ? "开" : "关")} " +
                $"transparent={_queue.TransparentCount} baked={Pipeline.SceneFogSamplingBaked} requested={sampleFog}");
        }
        try { _queue.DrawTransparent(); }
        finally
        {
            Pipeline.SceneFogParams = default;
            // set 1 回到默认集：直绘路径不能继续绑本帧的雾资源，它下一帧就会被改写。
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
            // 交换而不是复制：本帧的绘制集合成为下一帧帧首预刷新的依据。
            (_drawnThisFrame, _drawnLastFrame) = (_drawnLastFrame, _drawnThisFrame);
            _drawnThisFrame.Clear();
            (_quadThisFrame, _quadLastFrame) = (_quadLastFrame, _quadThisFrame);
            _quadThisFrame.Clear();
        }
    }

    /// <summary>记录本次 SetPipeline 请求的状态，供重放时重新选择 PSO。</summary>
    internal void RecordPipeline(PipelineMode mode, CullModeFlags cull, bool depthWrite)
        => State = new VKSceneDrawCommand { Kind = VKSceneDrawKind.Primitive, Mode = mode, Cull = cull, DepthWrite = depthWrite };

    /// <summary>
    /// 钉住一个在重放期仍被命令缓冲引用的所有者（图元分组 / 精灵 / 文字状态）。
    /// 共享壳体模板与实例缓冲都挂在分组上，钉住分组即可覆盖它们。
    /// 非收集期是空操作。
    /// </summary>
    internal static void RetainOwner(object owner) => Active?._arena.Retain(owner);

    /// <summary>沿相机前向的投影距离，与 DX 的 <c>DXSceneDrawCollection.Depth</c> 同式。</summary>
    internal static float Depth(Vector3 center, Matrix4x4 world)
        => RenderView.SortDepth(Vector3.Transform(center, world));

    /// <summary>把实例流的行主序四行还原成世界矩阵，供透明排序求深度。</summary>
    internal static Matrix4x4 InstanceWorld(in InstanceTransformData instance) => new(
        instance.Row0.X, instance.Row0.Y, instance.Row0.Z, instance.Row0.W,
        instance.Row1.X, instance.Row1.Y, instance.Row1.Z, instance.Row1.W,
        instance.Row2.X, instance.Row2.Y, instance.Row2.Z, instance.Row2.W,
        instance.Row3.X, instance.Row3.Y, instance.Row3.Z, instance.Row3.W);

    /// <summary>读取图元矩阵 UBO 中的世界矩阵（存储时已转置，这里转置回来）。</summary>
    static Matrix4x4 PrimitiveWorld(PrimitiveData primitive, int frameIndex)
    {
        var mapped = primitive.MappedMatrixBuffers[frameIndex];
        if (mapped == null) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(Unsafe.Read<MatrixBuffer>(mapped).World);
    }

    internal void CapturePrimitive(CommandBuffer cmd, PrimitiveData primitive, VkBuffer vertex, VkBuffer index,
        DescriptorSet set, uint indexCount, VkBuffer instanceBuffer, uint instanceCount, uint firstInstance,
        float sortDepth)
    {
        if (indexCount == 0 || instanceCount == 0) return;

        // 纹理就绪与绑定 16-20 的刷新必须在 Pass 之外完成，见 PreparePrimitive 的说明。
        Pipeline.PreparePrimitive(cmd, primitive, set);
        _arena.Retain(primitive);
        _drawnThisFrame.Add(primitive);

        var draw = State;
        draw.Kind = VKSceneDrawKind.Primitive;
        draw.Primitive = primitive;
        draw.Set = set;
        // 视图槽在收集期已由 FrameSchedule.PrepareView 选好，所以这里记下的就是本视图的 set 2。
        draw.View = Pipeline.ActiveViewSet;
        draw.Vertex = vertex;
        draw.Index = index;
        draw.Instances = instanceBuffer;
        draw.IndexKind = primitive.Use32BitIndices ? IndexType.Uint32 : IndexType.Uint16;
        draw.IndexCount = indexCount;

        if (!float.IsNaN(sortDepth))
        {
            draw.InstanceCount = instanceCount;
            draw.FirstInstance = firstInstance;
            _queue.Add(draw, draw.Mode != PipelineMode.Opaque, FrameSchedule.SceneDrawOrder.Current,
                sortDepth);
            return;
        }

        // 实例拆分：透明阶段必须逐实例绘制，才能让前后两个实例正确混色；
        // 不透明阶段的整批实例化绘制保持原样（重复绘制同一实例没有意义）。
        int frameIndex = (int)Device.FrameIndex;
        var world = PrimitiveWorld(primitive, frameIndex);
        bool split = instanceBuffer.Handle != 0 && draw.Mode != PipelineMode.Opaque;
        uint slices = split ? instanceCount : 1;
        for (uint i = 0; i < slices; i++)
        {
            draw.InstanceCount = split ? 1u : instanceCount;
            draw.FirstInstance = firstInstance + i;
            // 拆分路径上的个体深度需要实例矩阵，而实例流是 GPU 侧资源，
            // 调用方能给就给（sortDepth 参数），给不了就退化到图元级深度：
            // 同深度项之间由队列按添加顺序稳定排序，而调用方的遍历顺序本身已经排好序。
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
            // Transparent 变体本身就不写深度，与 DX 捕获时保持零值一致。
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
