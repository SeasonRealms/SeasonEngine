// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using Season.Rendering;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>Three forms of draw command: 3D primitives, sprite quads, and text batches. Their binding slots differ, so each is stored in its own group.</summary>
internal enum MTLSceneDrawKind : byte
{
    Primitive,
    Quad,
    Text,
}

/// <summary>
/// A single captured draw item. Only immutable handles are stored (<c>IMTLBuffer</c>/<c>IMTLTexture</c> references with offsets and counts);
/// neither the control nor a delegate for the deferred call to <c>Control.Draw</c> is stored.
///
/// The difference in form from DX/VK is precisely Metal's encoder semantics:
/// one <c>BeginPass</c> is one <c>RenderCommandEncoder</c>, and switching Passes resets all bindings,
/// with no "persistent binding" like a descriptor set or root signature to reuse.
/// Therefore every command must carry a **complete** binding list and rebind from the start during replay;
/// the benefit is that replay has no cross-command state dependency at all, and it does not need VK's "no rewriting after binding" rewrite discipline.
/// </summary>
internal struct MTLSceneDrawCommand : ISceneDrawCommand
{
    internal MTLSceneDrawKind Kind;
    internal PipelineMode Mode;
    internal MTLCullMode Cull;
    internal bool DepthWrite;
    // Per-view constant (VS buffer(11)): whether it is the main view's frame slot or the screen-space view is decided at collection time.
    internal IMTLBuffer? View;

    // -- 3D primitive: nine vertex/fragment buffers + five material textures + draw parameters
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
    // Generic per-instance extras (VS buffer(12)): the group's ring slot for an instanced draw, the shared white record otherwise.
    internal IMTLBuffer? InstanceExtras;
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
    // A7 main view: the GPU-written args replace IndexCount/InstanceCount. Owned by the instanced group, which outlives the frame.
    internal IMTLBuffer? IndirectArgs;
    internal nuint IndirectOffset;

    // -- Sprite quad: no IB, six non-indexed vertices
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

    // -- Text batch: unit quad + instance stream + glyph data + this frame's parameter block
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

        // Only the replay Pass knows which PSO family to use (main scene / velocity MRT / Overlay / OutlineMask),
        // so both the PSO and DSS are actually bound here; the collection phase only records the mode.
        Pipeline.SetPipeline(enc, Mode, doubleSided: Cull == MTLCullMode.None, DepthWrite);
        // In the two-pass double-sided scheme, the front-face pass uses Front culling, while SetPipeline only expresses the None/Back states, so it is filled in here.
        // Routed through Pipeline.SetCullMode rather than straight to the encoder: during collection that is the call
        // that records the value, and at replay it is the one place that also feeds the S0 draw trace.
        Pipeline.SetCullMode(enc, Cull);
        // The transparent stage's gating and fog-sampling resources: carried by the whole command itself, not depending on encoder state left by the previous command.
        Pipeline.BindSceneFog(enc);

        switch (Kind)
        {
            case MTLSceneDrawKind.Primitive:
                Pipeline.DrawPrimitive(enc, Primitive!, Vertex!, IndexBuffer!, Matrix!, Material!, Light!, Bones!,
                    Morph!, InstanceBones!, MTLPrimitiveType.Triangle, IndexCount, IndexKind, IndexBufferOffset,
                    Instances, InstanceBufferOffset, InstanceCount, InstanceBoneOffset, PrevInstances, PrevBones, PrevBoneOffset, View,
                    IndirectArgs, IndirectOffset, InstanceExtras);
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
                // Only the atlas is valid; the other four material slots always use the white texture as a placeholder: the text branch reads only tex0 in the FS,
                // but Metal API Validation requires every texture slot declared in the parameters to have a legal binding.
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

    // Resource references are held by the <see cref="MTLSceneDrawCollection"/> snapshot page until this frame's GPU retires.
    public void Release() { }
}

/// <summary>
/// Metal main-thread scene collector. During collection it submits no draws at all:
/// <c>Pipeline.SetPipeline</c> only records mode/cull/depth-write, and the actual PSO binding happens during replay
/// (only the replay Pass knows which PSO family to use: main scene, velocity MRT, Overlay, OutlineMask).
///
/// During collection <c>Device.GraphicsEncoder</c> is empty (no Pass has been opened yet), and this is the biggest difference in form from DX/VK:
/// those two still have a live command buffer / command list during collection that can "do nothing while" accepting binding calls,
/// whereas Metal has no encoder to accept them, so every draw entry point must be split off to the capture path before it touches an encoder.
/// </summary>
internal sealed class MTLSceneDrawCollection
{
    readonly SceneDrawQueue<MTLSceneDrawCommand> _queue = new();
    readonly MTLDrawSnapshotArena _arena = new();

    ulong _serial;
    int _sequence;

    /// <summary>The instance currently collecting; empty outside collection, so replay will not be captured by itself again.</summary>
    internal static MTLSceneDrawCollection? Active { get; private set; }

    /// <summary>Current state: the draw state last recorded by SetPipeline/SetCullMode.</summary>
    internal MTLSceneDrawCommand State;

    internal MTLDrawSnapshotArena Arena => _arena;

    internal int OpaqueCount => _queue.OpaqueCount;

    internal int TransparentCount => _queue.TransparentCount;

    internal void Begin()
    {
        if (Active != null || !FrameSchedule.SceneCollectionActive || !FrameSchedule.SceneDrawOrder.IsCollecting)
            throw new InvalidOperationException("Scene collection must be started by the frame scheduler, and the previous frame must already have been released.");
        _queue.BeginCollection();
        // Metal's frame-slot retirement is guaranteed by the in-flight semaphore; here only a monotonic handle is needed to express "the same frame".
        _arena.BeginFrame(++_serial);
        _sequence = 0;
        State = default;
        Active = this;
    }

    internal void Seal()
    {
        if (Active != this) throw new InvalidOperationException("Scene collection has already been sealed.");
        _queue.Seal();
        Active = null;
    }

    internal void DrawOpaque()
    {
        if (Device.ActivePassId != RenderPassId.SceneOpaque)
            throw new InvalidOperationException("The opaque queue's Pass is invalid.");
        // The opaque variant has no fog-sampling gating, but it is a cross-Pass static state, so it must be explicitly zeroed.
        Pipeline.SceneFogParams = default;
        _queue.DrawOpaque();
    }

    internal void DiscardOpaque()
    {
        if (Device.ActivePassId != RenderPassId.SceneOpaque)
            throw new InvalidOperationException("The opaque queue's Pass is invalid.");
        // The geometry-off row must leave the same cross-Pass state behind as the real opaque stage.
        Pipeline.SceneFogParams = default;
        _queue.DiscardOpaque();
    }

    internal void DrawTransparent(bool sampleFog)
    {
        if (Device.ActivePassId != RenderPassId.SceneTransparent)
            throw new InvalidOperationException("The transparent queue's Pass is invalid.");

        // Null out first, then short-circuit: the non-evaluated path on the right of && also needs a definite empty binding, otherwise it would be "sample the previous frame's volume" rather than "do not sample".
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

    /// <summary>Record the state requested by this SetPipeline, so replay can re-select the PSO.</summary>
    internal void RecordPipeline(PipelineMode mode, bool doubleSided, bool depthWrite)
    {
        State.Mode = mode;
        State.DepthWrite = depthWrite;
        State.Cull = doubleSided ? MTLCullMode.None : MTLCullMode.Back;
    }

    /// <summary>Record the cull face set separately in the two-pass double-sided scheme (Front for the front-face pass).</summary>
    internal void RecordCull(MTLCullMode cull) => State.Cull = cull;

    void Add(in MTLSceneDrawCommand command, bool transparent)
    {
        // The Metal direct-draw path already encodes one by one in control-tree order, and transparent instances are already sorted by depth on the caller side and split one by one.
        // Here a decreasing serial number acts as "depth", degrading the shared queue's sort into a stable preservation of call order,
        // thereby guaranteeing that segmented replay matches direct draw pixel for pixel; global transparent ordering across primitives has never existed on Metal.
        _queue.Add(command, transparent, FrameSchedule.SceneDrawOrder.Current, -_sequence++);
    }

    internal void CapturePrimitive(PrimitiveData primitive, IMTLBuffer vertex, IMTLBuffer index,
        IMTLBuffer matrix, IMTLBuffer material, IMTLBuffer light, IMTLBuffer bones, IMTLBuffer morph,
        IMTLBuffer instanceBones, MTLIndexType indexKind, nuint indexCount, nuint indexBufferOffset,
        IMTLBuffer? instances, nuint instanceBufferOffset, nuint instanceCount, nuint instanceBoneOffset,
        IMTLBuffer? prevInstances, IMTLBuffer? prevBones, nuint prevBoneOffset, IMTLBuffer view,
        IMTLBuffer? indirectArgs = null, nuint indirectOffset = 0, IMTLBuffer? instanceExtras = null)
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
        // A non-instanced command still binds the white record, since the MSL argument is declared unconditionally.
        draw.InstanceExtras = instanceExtras ?? Pipeline.DefaultInstanceExtrasBuffer;
        draw.IndexKind = indexKind;
        draw.IndexCount = indexCount;
        draw.IndexBufferOffset = indexBufferOffset;
        draw.InstanceCount = instanceCount;
        draw.InstanceBufferOffset = instanceBufferOffset;
        draw.InstanceBoneOffset = instanceBoneOffset;
        draw.PrevBoneOffset = prevBoneOffset;
        draw.IndirectArgs = indirectArgs;
        draw.IndirectOffset = indirectOffset;
        draw.Albedo = primitive.BaseColorTexture;
        draw.Normal = primitive.NormalTexture;
        draw.Metallic = primitive.MetallicRoughnessTexture;
        draw.Occlusion = primitive.OcclusionTexture;
        draw.Emissive = primitive.EmissiveTexture;

        // Metal's buffers are created by their owners per frame slot, and textures are managed centrally by a dictionary;
        // as long as everyone lives until this frame's GPU retires, replay will not read an already-released object.
        _arena.Retain(primitive);
        _arena.Retain(vertex); _arena.Retain(index); _arena.Retain(matrix); _arena.Retain(material);
        _arena.Retain(bones); _arena.Retain(morph); _arena.Retain(instanceBones);
        _arena.Retain(draw.InstanceExtras);
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
        // Sprites are always alpha-blended, consistent with the DX CaptureQuad / VK Quad form.
        // Cull and depth-write are set explicitly, not inheriting state left by the previous command:
        // the direct-draw path's SetPipeline(Transparent) is CullBack + TransparentDSS (DSS ignores depthWrite).
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
        // Text always uses the screen-space view (identity View/Projection).
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
        // Text uses double-sided transparent, aligned with the direct-draw path's SetPipeline(Transparent, doubleSided: true).
        draw.Mode = PipelineMode.Transparent;
        draw.Cull = MTLCullMode.None;
        draw.DepthWrite = true;

        _arena.Retain(instances); _arena.Retain(glyphs); _arena.Retain(parameters);
        _arena.Retain(atlas); _arena.Retain(fallback);
        Add(draw, true);
    }
}
