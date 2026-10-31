// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Rendering;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>三种绘制命令形态：3D 图元、精灵四边形、文字批次。三者绑定槽位不同，各自成组保存。</summary>
internal enum MTLSceneDrawKind : byte
{
    Primitive,
    Quad,
    Text,
}

/// <summary>
/// 单个已捕获的绘制项。只保存不可变句柄（<c>IMTLBuffer</c>/<c>IMTLTexture</c> 引用与偏移、计数），
/// 不保存控件、也不保存延迟调用 <c>Control.Draw</c> 的委托。
///
/// 与 DX/VK 的形态差异正是 Metal 的 encoder 语义：
/// 一次 <c>BeginPass</c> 就是一个 <c>RenderCommandEncoder</c>，换 Pass 之后所有绑定归零，
/// 没有类似描述符集或根签名的“持久绑定”可复用。
/// 因此每条命令都必须自带**完整**的绑定清单，重放时从头绑一遍；
/// 好处是重放完全没有跨命令状态依赖，也不需要 VK 那种“绑定后不可改写”的补写纪律。
/// </summary>
internal struct MTLSceneDrawCommand : ISceneDrawCommand
{
    internal MTLSceneDrawKind Kind;
    internal PipelineMode Mode;
    internal MTLCullMode Cull;
    internal bool DepthWrite;
    // 每视图常量（VS buffer(11)）：收集时就定下是主视图帧槽还是屏幕空间视图。
    internal IMTLBuffer? View;

    // -- 3D 图元：九个顶点/片元缓冲 + 五张材质纹理 + 绘制参数
    internal PrimitiveData? Primitive;
    internal IMTLBuffer? Vertex;
    internal IMTLBuffer? IndexBuffer;
    internal IMTLBuffer? Matrix;
    internal IMTLBuffer? Material;
    internal IMTLBuffer? Light;
    internal IMTLBuffer? Bones;
    internal IMTLBuffer? Morph;
    internal IMTLBuffer? InstanceBones;
    internal IMTLBuffer? Instances;
    internal IMTLBuffer? PrevInstances;
    internal IMTLBuffer? PrevBones;
    internal MTLTexture? Albedo;
    internal MTLTexture? Normal;
    internal MTLTexture? Metallic;
    internal MTLTexture? Occlusion;
    internal MTLTexture? Emissive;
    internal MTLIndexType IndexKind;
    internal nuint IndexCount;
    internal nuint IndexBufferOffset;
    internal nuint InstanceCount;
    internal nuint InstanceBufferOffset;
    internal nuint InstanceBoneOffset;
    internal nuint PrevBoneOffset;

    // -- 精灵四边形：无 IB，六个非索引顶点
    internal IMTLBuffer? QuadVertex;
    internal IMTLBuffer? QuadMatrix;
    internal IMTLBuffer? QuadMaterial;
    internal IMTLBuffer? QuadLight;
    internal IMTLBuffer? QuadBones;
    internal MTLTexture? QuadAlbedo;
    internal MTLTexture? QuadNormal;
    internal MTLTexture? QuadMetallic;
    internal MTLTexture? QuadOcclusion;
    internal MTLTexture? QuadEmissive;

    // -- 文字批次：单位四边形 + 实例流 + 字形数据 + 本帧参数块
    internal IMTLBuffer? TextVertex;
    internal IMTLBuffer? TextMatrix;
    internal IMTLBuffer? TextInstances;
    internal IMTLBuffer? TextBones;
    internal IMTLBuffer? TextMaterial;
    internal IMTLBuffer? TextGlyphs;
    internal IMTLBuffer? TextInstanceBones;
    internal IMTLBuffer? TextParams;
    internal IMTLBuffer? TextLight;
    internal MTLTexture? TextAtlas;
    internal MTLTexture? TextFallback;
    internal nuint TextInstanceCount;

    public void Draw()
    {
        var enc = Device.GraphicsEncoder;

        // 重放的 Pass 才知道该用哪一族 PSO（主场景 / 速度 MRT / Overlay / OutlineMask），
        // 所以 PSO 与 DSS 都在这里才真正绑定，收集期只记录模式。
        Pipeline.SetPipeline(enc, Mode, doubleSided: Cull == MTLCullMode.None, DepthWrite);
        // 双面两遍的正面那一遍用的是 Front 剔除，SetPipeline 只表达 None/Back 两态，这里补上。
        enc.SetCullMode(Cull);
        // 透明阶段的门控与雾采样资源：整条命令自带，不依赖上一条命令留下的 encoder 状态。
        Pipeline.BindSceneFog(enc);

        switch (Kind)
        {
            case MTLSceneDrawKind.Primitive:
                Pipeline.DrawPrimitive(enc, Primitive!, Vertex!, IndexBuffer!, Matrix!, Material!, Light!, Bones!,
                    Morph!, InstanceBones!, MTLPrimitiveType.Triangle, IndexCount, IndexKind, IndexBufferOffset,
                    Instances, InstanceBufferOffset, InstanceCount, InstanceBoneOffset, PrevInstances, PrevBones, PrevBoneOffset, View);
                break;

            case MTLSceneDrawKind.Quad:
                enc.SetVertexBuffer(QuadVertex!, 0, 0);
                enc.SetVertexBuffer(QuadMatrix!, 0, 1);
                Pipeline.BindView(enc, View!);
                enc.SetVertexBuffer(Pipeline.IdentityInstanceBuffer, 0, 2);
                enc.SetVertexBuffer(QuadBones!, 0, 3);
                enc.SetVertexBuffer(QuadMaterial!, 0, 4);
                enc.SetFragmentBuffer(QuadLight!, 0, 1);
                enc.SetFragmentBuffer(QuadMaterial!, 0, 2);
                enc.SetFragmentTexture((QuadAlbedo ?? Device.White).Image, 0);
                enc.SetFragmentTexture((QuadNormal ?? Device.White).Image, 1);
                enc.SetFragmentTexture((QuadMetallic ?? Device.White).Image, 2);
                enc.SetFragmentTexture((QuadOcclusion ?? Device.White).Image, 3);
                enc.SetFragmentTexture((QuadEmissive ?? Device.White).Image, 4);
                enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 6);
                break;

            case MTLSceneDrawKind.Text:
                enc.SetVertexBuffer(TextVertex!, 0, 0);
                enc.SetVertexBuffer(TextMatrix!, 0, 1);
                Pipeline.BindView(enc, View!);
                enc.SetVertexBuffer(TextInstances!, 0, 2);
                enc.SetVertexBuffer(TextBones!, 0, 3);
                enc.SetVertexBuffer(TextMaterial!, 0, 4);
                enc.SetVertexBuffer(TextGlyphs!, 0, 5);
                enc.SetVertexBuffer(TextInstanceBones!, 0, 6);
                enc.SetVertexBuffer(TextParams!, 0, 7);
                enc.SetFragmentBuffer(TextLight!, 0, 1);
                enc.SetFragmentBuffer(TextMaterial!, 0, 2);
                enc.SetFragmentBuffer(TextParams!, 0, 3);
                // 只有 atlas 有效，其余四张材质位恒用白体占位：文字分支在 FS 里只读 tex0，
                // 但 Metal API Validation 要求参数里声明的每一个 texture 槽都有合法绑定。
                var fallback = TextFallback!;
                enc.SetFragmentTexture((TextAtlas ?? fallback).Image, 0);
                enc.SetFragmentTexture(fallback.Image, 1);
                enc.SetFragmentTexture(fallback.Image, 2);
                enc.SetFragmentTexture(fallback.Image, 3);
                enc.SetFragmentTexture(fallback.Image, 4);
                enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 6, TextInstanceCount);
                break;
        }
    }

    // 资源引用由 <see cref="MTLSceneDrawCollection"/> 的快照页持有，直到本帧 GPU 退休。
    public void Release() { }
}

/// <summary>
/// Metal 主线程场景收集器。收集期不提交任何绘制：
/// <c>Pipeline.SetPipeline</c> 只记录模式/剔除/深度写入，真正绑定 PSO 发生在重放时
/// （重放的 Pass 才知道该用哪一族 PSO：主场景、速度 MRT、Overlay、OutlineMask）。
///
/// 收集期 <c>Device.GraphicsEncoder</c> 为空（还没开 Pass），这是与 DX/VK 最大的形态差异：
/// 那两端在收集期仍有一条活着的命令缓冲/命令列表可以“什么都不做地”接收绑定调用，
/// Metal 没有任何 encoder 可接收，所以所有绘制入口都必须在碰到 encoder 之前分流到捕获路径。
/// </summary>
internal sealed class MTLSceneDrawCollection
{
    readonly SceneDrawQueue<MTLSceneDrawCommand> _queue = new();
    readonly MTLDrawSnapshotArena _arena = new();

    ulong _serial;
    int _sequence;

    /// <summary>正在收集的实例；非收集期为空，重放不会再被自己捕获。</summary>
    internal static MTLSceneDrawCollection? Active { get; private set; }

    /// <summary>当前状态：最近一次 SetPipeline/SetCullMode 记录下来的绘制状态。</summary>
    internal MTLSceneDrawCommand State;

    internal MTLDrawSnapshotArena Arena => _arena;

    internal int OpaqueCount => _queue.OpaqueCount;

    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("场景收集必须由帧调度器开启，且上一帧已经释放。");
        _queue.BeginCollection();
        // Metal 的帧槽退休由 in-flight 信号量保证；这里只需要一个单调句柄来表达“同一帧”。
        _arena.BeginFrame(++_serial);
        _sequence = 0;
        State = default;
        Active = this;
    }

    internal void Seal()
    {
        if (Active != this) throw new InvalidOperationException("场景收集已封存。");
        _queue.Seal();
        Active = null;
    }

    internal void DrawOpaque()
    {
        if (Device.ActivePassId != RenderPassId.SceneOpaque)
            throw new InvalidOperationException("不透明队列的 Pass 无效。");
        // 不透明变体没有雾采样门控，但它是跨 Pass 的静态状态，必须显式归零。
        Pipeline.SceneFogParams = default;
        _queue.DrawOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        if (Device.ActivePassId != RenderPassId.SceneTransparent)
            throw new InvalidOperationException("透明队列的 Pass 无效。");

        // 先置空再短路：&& 右侧的不求值路径也要有确定的空绑定，否则会是“采上一帧体积”而不是“不采样”。
        IMTLBuffer? frame = null;
        IMTLTexture? integrated = null;
        IMTLTexture? local = null;
        bool fog = sampleFog && Pipeline.SceneFogSamplingBaked
            && MTLFogCompositePipeline.TryGetTransparentBinding(out frame, out integrated, out local);
        Pipeline.SceneFogParams = fog ? new Vector4(1, 0, 0, 0) : default;
        Pipeline.SceneFogFrame = fog ? frame : null;
        Pipeline.SceneFogIntegrated = fog ? integrated : null;
        Pipeline.SceneFogLocal = fog ? local : null;
        try { _queue.DrawTransparent(); }
        finally
        {
            Pipeline.SceneFogParams = default;
            Pipeline.SceneFogFrame = null;
            Pipeline.SceneFogIntegrated = null;
            Pipeline.SceneFogLocal = null;
        }
    }

    internal void Release()
    {
        if (Active == this) Active = null;
        try { _queue.Release(); }
        finally { _arena.Reset(); State = default; }
    }

    /// <summary>记录本次 SetPipeline 请求的状态，供重放时重新选择 PSO。</summary>
    internal void RecordPipeline(PipelineMode mode, bool doubleSided, bool depthWrite)
    {
        State.Mode = mode;
        State.DepthWrite = depthWrite;
        State.Cull = doubleSided ? MTLCullMode.None : MTLCullMode.Back;
    }

    /// <summary>记录双面两遍里单独设置的剔除面（正面那一遍为 Front）。</summary>
    internal void RecordCull(MTLCullMode cull) => State.Cull = cull;

    void Add(in MTLSceneDrawCommand command, bool transparent)
    {
        // Metal 直绘路径本来就是按控件树顺序逐条编码，透明实例也已在调用方按深度排好并逐个拆分。
        // 这里用递减序号充当“深度”，让共享队列的排序退化为稳定保持调用顺序，
        // 从而保证分段重放与直绘逐像素一致；跨图元的全局透明排序在 Metal 上从未存在过。
        _queue.Add(command, transparent, FrameSchedule.SceneDrawOrder.Current, -_sequence++);
    }

    internal void CapturePrimitive(PrimitiveData primitive, IMTLBuffer vertex, IMTLBuffer index,
        IMTLBuffer matrix, IMTLBuffer material, IMTLBuffer light, IMTLBuffer bones, IMTLBuffer morph,
        IMTLBuffer instanceBones, MTLIndexType indexKind, nuint indexCount, nuint indexBufferOffset,
        IMTLBuffer? instances, nuint instanceBufferOffset, nuint instanceCount, nuint instanceBoneOffset,
        IMTLBuffer? prevInstances, IMTLBuffer? prevBones, nuint prevBoneOffset, IMTLBuffer view)
    {
        if (indexCount == 0) return;
        var draw = State;
        draw.Kind = MTLSceneDrawKind.Primitive;
        draw.View = view;
        draw.Primitive = primitive;
        draw.Vertex = vertex;
        draw.IndexBuffer = index;
        draw.Matrix = matrix;
        draw.Material = material;
        draw.Light = light;
        draw.Bones = bones;
        draw.Morph = morph;
        draw.InstanceBones = instanceBones;
        draw.Instances = instances ?? Pipeline.IdentityInstanceBuffer;
        draw.PrevInstances = prevInstances ?? draw.Instances;
        draw.PrevBones = prevBones ?? bones;
        draw.IndexKind = indexKind;
        draw.IndexCount = indexCount;
        draw.IndexBufferOffset = indexBufferOffset;
        draw.InstanceCount = instanceCount;
        draw.InstanceBufferOffset = instanceBufferOffset;
        draw.InstanceBoneOffset = instanceBoneOffset;
        draw.PrevBoneOffset = prevBoneOffset;
        draw.Albedo = primitive.BaseColorTexture;
        draw.Normal = primitive.NormalTexture;
        draw.Metallic = primitive.MetallicRoughnessTexture;
        draw.Occlusion = primitive.OcclusionTexture;
        draw.Emissive = primitive.EmissiveTexture;

        // Metal 的 buffer 由所有者按帧槽创建，纹理由字典集中管理；
        // 只要所有人都活到本帧 GPU 退休，重放期就不会读到已释放的对象。
        _arena.Retain(primitive);
        _arena.Retain(vertex); _arena.Retain(index); _arena.Retain(matrix); _arena.Retain(material);
        _arena.Retain(bones); _arena.Retain(morph); _arena.Retain(instanceBones);
        _arena.Retain(draw.Albedo); _arena.Retain(draw.Normal); _arena.Retain(draw.Metallic);
        _arena.Retain(draw.Occlusion); _arena.Retain(draw.Emissive);
        Add(draw, State.Mode != PipelineMode.Opaque);
    }

    internal void CaptureQuad(IMTLBuffer vertex, IMTLBuffer matrix, IMTLBuffer material, IMTLBuffer light,
        IMTLBuffer bones, MTLTexture? albedo, MTLTexture? normal, MTLTexture? metallic,
        MTLTexture? occlusion, MTLTexture? emissive, IMTLBuffer view)
    {
        var draw = State;
        draw.Kind = MTLSceneDrawKind.Quad;
        draw.View = view;
        draw.QuadVertex = vertex;
        draw.QuadMatrix = matrix;
        draw.QuadMaterial = material;
        draw.QuadLight = light;
        draw.QuadBones = bones;
        draw.QuadAlbedo = albedo;
        draw.QuadNormal = normal;
        draw.QuadMetallic = metallic;
        draw.QuadOcclusion = occlusion;
        draw.QuadEmissive = emissive;
        // 精灵永远是透明混合，与 DX CaptureQuad / VK 的 Quad 形态一致。
        // 剔除与深度写入显式写定，不继承上一条命令留下的状态：
        // 直绘路径的 SetPipeline(Transparent) 就是 CullBack + TransparentDSS（DSS 不看 depthWrite）。
        draw.Mode = PipelineMode.Transparent;
        draw.Cull = MTLCullMode.Back;
        draw.DepthWrite = true;

        _arena.Retain(vertex); _arena.Retain(matrix); _arena.Retain(material);
        _arena.Retain(albedo); _arena.Retain(normal); _arena.Retain(metallic);
        _arena.Retain(occlusion); _arena.Retain(emissive);
        Add(draw, true);
    }

    internal void CaptureText(IMTLBuffer vertex, IMTLBuffer matrix, IMTLBuffer instances, IMTLBuffer bones,
        IMTLBuffer material, IMTLBuffer glyphs, IMTLBuffer instanceBones, IMTLBuffer parameters,
        IMTLBuffer light, MTLTexture? atlas, MTLTexture fallback, nuint instanceCount)
    {
        var draw = State;
        draw.Kind = MTLSceneDrawKind.Text;
        // 文字恒走屏幕空间视图（单位 View/Projection）。
        draw.View = Pipeline.ScreenViewBuffer;
        draw.TextVertex = vertex;
        draw.TextMatrix = matrix;
        draw.TextInstances = instances;
        draw.TextBones = bones;
        draw.TextMaterial = material;
        draw.TextGlyphs = glyphs;
        draw.TextInstanceBones = instanceBones;
        draw.TextParams = parameters;
        draw.TextLight = light;
        draw.TextAtlas = atlas;
        draw.TextFallback = fallback;
        draw.TextInstanceCount = instanceCount;
        // 文字走双面透明，与直绘路径的 SetPipeline(Transparent, doubleSided: true) 对齐。
        draw.Mode = PipelineMode.Transparent;
        draw.Cull = MTLCullMode.None;
        draw.DepthWrite = true;

        _arena.Retain(instances); _arena.Retain(glyphs); _arena.Retain(parameters);
        _arena.Retain(atlas); _arena.Retain(fallback);
        Add(draw, true);
    }
}
