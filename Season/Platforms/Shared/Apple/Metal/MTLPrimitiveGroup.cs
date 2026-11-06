// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.CompilerServices;
using MTLTexture = Season.Platforms.Shared.Apple.Metal.Texture;

namespace Season.Platforms.Shared.Apple.Metal;

/// <summary>
/// Shared base class on the Metal backend for Pbr3D-path primitive groups rendered from PrimitiveData lists.
/// It is aligned one to one with DXPrimitiveGroup and VKPrimitiveGroup:
/// - Static responsibilities: camera, shared lighting UBOs with N-buffering, and dummy identity bone, instance-bone, and zero morph buffers used by Mesh3D.
/// - Instance responsibilities: Matrix and Material UBO creation, SyncAlpha, and three-bucket drawing for Opaque, Fade, and Transparent.
///
/// Derived differences:
/// - Geometry and material sources, where MTLModel uses a glTF node tree and MTLMesh3D uses Mesh3D.Surfaces.
/// - Whether bones exist, where MTLModel overrides the VS buffer(6) palette ring and writes it during Update.
///
/// Simplifications relative to Vulkan:
/// - No DescriptorSet objects are needed because DrawPrimitive binds directly through SetVertexBuffer, SetFragmentBuffer, and SetFragmentTexture.
/// - No EnsureReadyForRendering step is needed because StorageMode.Private plus BlitEncoder synchronization handles it automatically.
/// - No mapped-pointer cache is needed because IMTLBuffer.Contents is a persistent IntPtr and supports direct writes and reads.
/// </summary>
internal abstract class MTLPrimitiveGroup : PrimitiveGroupCore<PrimitiveData, MaterialParams, MTLTexture>, IDisposable
{
    /// <summary>Bones per palette block. It sizes the shared identity ring (the retired b3 slot's placeholder and the
    /// non-skinned fallback at VS buffer(6)) and the single-model CPU palette; GPU palettes are sized by the joint count.</summary>
    public const int MaxBones = 100;

    // === Globally shared lighting UBOs, N-buffered at FS slot 1 ===
    internal static IMTLBuffer[] LightConstantBuffers = null!;

    // === Globally shared identity-bone UBO. It backs the retired b3 slot's placeholder at VS slot 3 and the
    //     non-skinned fallback at VS slot 6; no shader reads skinning from slot 3 any more. ===
    internal static IMTLBuffer[] IdentityBoneBuffers = null!;

    // === Globally shared placeholder instance-bone buffer with one identity matrix, used as the dummy binding at VS slot 5 ===
    internal static IMTLBuffer[] IdentityInstanceBoneBuffers = null!;

    // === Globally shared zero morph-delta buffer for primitives without morph targets, used as the dummy binding at VS slot 4 ===
    internal static IMTLBuffer[] DefaultMorphDeltasBuffers = null!;

    // Contract clause 10 of 2-4:
    // DDGI irradiance atlas for the current frame, stored as a compute 2D texture.
    // SetLighting resolves it once per frame, mirroring MTLTextureCube.Active,
    // and Device.BeginPass binds it at texture(7).
    // Null falls back to Device.White, and it stays null whenever the feature is disabled or not ready.
    internal static MTLTexture? DdgiAtlasActive;

    // Step 3 of 2-4:
    // DDGI depth-moment atlas for the current frame, stored as an rg16float compute 2D texture.
    // It follows the same pattern as DdgiAtlasActive:
    // SetLighting resolves it every frame, Device.BeginPass binds it at texture(8),
    // null falls back to Device.White,
    // and runtime Chebyshev sampling is gated by giParams2.y.
    internal static MTLTexture? DdgiDepthActive;

    // Step C of 2-5:
    // cloud-noise 2D texture for the current frame, sampled with wrap mode.
    // SetLighting resolves it once per frame using the same pattern as DdgiAtlas,
    // and Device.BeginPass binds it at texture(9).
    // Null falls back to Device.White, which is a dangerous fallback,
    // so zero sampling is guaranteed by gating through cloudParams0.w layer count.
    internal static MTLTexture? CloudNoiseActive;

    // Step E of 2-5:
    // current-frame AP 3D LUT.
    // SetLighting resolves it once per frame and Device.BeginPass binds it at texture(10).
    // Null falls back to MTLTexture3D.DummyBlack,
    // where 1x1x1 acts as an additive identity and apParams0.x gating only avoids the sampling work.
    internal static MTLTexture3D? AerialLutActive;

    // 2-7 P0:
    // The current frame's procedural sky Sky-View LUT, an rgba16float 2D texture computed by the compute pass.
    // Same pattern as the previous three: SetLighting resolves it once per frame, Device.BeginPass binds it to texture(13);
    // when null it falls back to Device.White, but the consumer side is mutually gated by skyParams0.w in the uniform branch, so the white texture is never sampled.
    internal static MTLTexture? SkyViewActive;

    // === Per-instance state ===
    // Group-level state (alpha, tint, transform, the reusable draw list, the shadow-pass cache) and the
    // highlight boxes all live in PrimitiveGroupCore. What stays here is Metal resource creation and submission.

    /// <summary>Cull mode applied by the previous <see cref="ApplySurfaceState"/> call. Metal keeps face culling
    /// outside the PSO, and the two-pass double-sided transparent scheme ends on the front pass, so returning to the
    /// back pass needs an explicit rebind.</summary>
    bool _cullWasFront;


    // ============================================================
    // Static side: lighting and identity-bone UBO lifetime plus global camera and lighting updates
    // ============================================================

    public static unsafe void InitLights()
    {
        int n = Device.frameCount;
        LightConstantBuffers = new IMTLBuffer[n];
        for (int i = 0; i < n; i++)
            LightConstantBuffers[i] = Device.ResourceManager.CreateConstantBuffer((nuint)Unsafe.SizeOf<SceneLightParams>());

        var defaultLight = new SceneLightParams
        {
            CameraPos = new Vector4(0, 0, -1, 1),
            Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Params0 = new Vector4(0, Device.HdrExposure, 0, 0),
        };
        for (int i = 0; i < n; i++) WriteStruct(LightConstantBuffers[i], defaultLight);

        // Identity-bone UBO:
        // 100 matrices at 64 bytes each for a total of 6400 bytes, filled with Identity for all frames.
        IdentityBoneBuffers = new IMTLBuffer[n];
        nuint boneSize = (nuint)(Unsafe.SizeOf<Matrix4x4>() * MaxBones);
        var identity = Matrix4x4.Identity;

        for (int i = 0; i < n; i++)
        {
            IdentityBoneBuffers[i] = Device.ResourceManager.CreateConstantBuffer(boneSize);
            byte* basePtr = (byte*)IdentityBoneBuffers[i].Contents;
            for (int j = 0; j < MaxBones; j++)
                *(Matrix4x4*)(basePtr + j * sizeof(float) * 16) = identity;
        }

        IdentityInstanceBoneBuffers = new IMTLBuffer[n];
        nuint instanceBoneSize = (nuint)Unsafe.SizeOf<Matrix4x4>();
        for (int i = 0; i < n; i++)
        {
            IdentityInstanceBoneBuffers[i] = Device.ResourceManager.CreateBuffer(instanceBoneSize);
            *(Matrix4x4*)IdentityInstanceBoneBuffers[i].Contents = identity;
        }

        DefaultMorphDeltasBuffers = new IMTLBuffer[n];
        nuint morphSize = (nuint)sizeof(float);
        for (int i = 0; i < n; i++)
        {
            DefaultMorphDeltasBuffers[i] = Device.ResourceManager.CreateBuffer(morphSize);
            *(float*)DefaultMorphDeltasBuffers[i].Contents = 0f;
        }
    }

    public static void InitLightsDispose()
    {
        if (LightConstantBuffers != null)
        {
            for (int i = 0; i < LightConstantBuffers.Length; i++) LightConstantBuffers[i]?.Dispose();
            LightConstantBuffers = null!;
        }

        if (IdentityBoneBuffers != null)
        {
            for (int i = 0; i < IdentityBoneBuffers.Length; i++) IdentityBoneBuffers[i]?.Dispose();
            IdentityBoneBuffers = null!;
        }

        if (IdentityInstanceBoneBuffers != null)
        {
            for (int i = 0; i < IdentityInstanceBoneBuffers.Length; i++) IdentityInstanceBoneBuffers[i]?.Dispose();
            IdentityInstanceBoneBuffers = null!;
        }

        if (DefaultMorphDeltasBuffers != null)
        {
            for (int i = 0; i < DefaultMorphDeltasBuffers.Length; i++) DefaultMorphDeltasBuffers[i]?.Dispose();
            DefaultMorphDeltasBuffers = null!;
        }
    }

    /// <summary>Writes the lighting UBO for the current frame using the 1-2 SceneLightParams layout of 976 bytes.
    /// Params0.Y carries HDR exposure through shader-side params0.y so text can apply inverse-ACES compensation.
    /// VelocityParams carries current-frame subpixel jitter plus inverse screen size, matching contract clause 6 of 2-3.
    /// Both are injected at a single point every frame, and app-side writes to them are ignored.</summary>
    public static void SetLighting(SceneLightParams lightParams)
    {
        int fi = Device.FrameIndex;

        // Exposure, velocity parameters, environment, DDGI and sky are resolved by the shared frame setup, so
        // DX and VK cannot drift from this backend.
        SceneFrameSetup.PrepareLighting(ref lightParams, Device.HdrExposure);

        // Contract clause 4 of 1-7: resolve this frame's radiance cube once so the draw path does not look it up.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        MTLTextureCube.Active = env != null ? MTLTextureCube.Find(env.RadianceName) : null;

        // Contract clause 10 of 2-4:
        // resolve the current-frame DDGI irradiance atlas once per frame, mirroring MTLTextureCube.Active.
        // If not ready, keep it null so Device.BeginPass falls back to Device.White at texture(7),
        // and real sampling remains gated by DDGI_ENABLED.
        DdgiAtlasActive = Season.Rendering.Effects.DdgiEffect.Ready
            ? Season.Platforms.Shared.Apple.Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName)
            : null;

        // Step 3 of 2-4:
        // resolve the current-frame DDGI depth-moment atlas using the same pattern as the irradiance atlas.
        // If not ready, keep it null so Device.BeginPass falls back to Device.White at texture(8),
        // while Chebyshev sampling remains gated by giParams2.y.
        DdgiDepthActive = Season.Rendering.Effects.DdgiEffect.Ready
            ? Season.Platforms.Shared.Apple.Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveDepthName)
            : null;

        // Step C and E of 2-5:
        // resolve the current-frame cloud noise and AP 3D LUT once per frame, following the DdgiAtlas pattern.
        // Cloud noise is resolved every frame because when FrameSchedule.CloudNoiseTexture returns to null
        // after disposal or quality-tier downgrade, Active must be cleared as well.
        // Otherwise a released texture handle would continue to be bound, as noted on the VK and DX sides too.
        // If not ready, both remain null so Device.BeginPass falls back at texture(9) and texture(10),
        // while real sampling is gated by cloudParams0.w and apParams0.x.
        CloudNoiseActive = Season.Rendering.FrameSchedule.CloudNoiseTexture is string cloudNoiseName
            ? Season.Platforms.Shared.Apple.Graphics.FindDdgiAtlas(cloudNoiseName)
            : null;
        AerialLutActive = MTLTexture3D.Find(Season.Rendering.FrameSchedule.AerialLutTexture);

        // 2-7 P0: resolve the current frame's Sky-View LUT once per frame, for the same reason as cloud noise:
        // after a release or a quality downgrade FrameSchedule.SkyViewTexture returns to null,
        // so Active must be cleared in sync, otherwise the already-released texture handle would keep being bound.
        SkyViewActive = Season.Rendering.FrameSchedule.SkyViewTexture is string skyViewName
            ? Season.Platforms.Shared.Apple.Graphics.FindDdgiAtlas(skyViewName)
            : null;

        // The snapshot is read by fog / weather / particles, so it must come from the view that is actually
        // being rendered, not from the legacy camera mirror.
        var snapshotView = RenderView.Current;
        FrameSchedule.CaptureLighting(lightParams, snapshotView.View, snapshotView.RenderProjection,
            snapshotView.RenderViewProjection);
        WriteStruct(LightConstantBuffers[fi], lightParams);
    }

    /// <summary>Called once per frame by the main loop to refresh camera view and projection and then write the lighting UBO.</summary>
    public static void Update(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // For 1-3, matrix construction is centralized in shared Camera3D.
        // It is gated by Changed so a static camera rebuilds nothing,
        // while FOV and near/far are driven by BaseApp.Camera.
        // cameraPos and cameraTarget are forwarded from BaseApp.Camera.Position and Target,
        // and the method keeps them in the signature for frame-loop compatibility.
        var res = DeviceServices.BaseApp.DeviceResolution;

        // Contract clause 4 of 2-3:
        // the main view is the only injection point for jitter.
        // Prepare snapshots the previous-frame non-jittered ViewProjection and then rebuilds matrices,
        // baking jitter only into the render projection.
        // Frustum culling and CSM cascades keep the non-jittered matrices, avoiding edge flicker and shadow shimmer;
        // with MotionVectors off the history is all zeros.
        var view = DeviceServices.BaseApp.MainView;
        view.Prepare(res.X, res.Y);
        FrameSchedule.CurrentView = view;
        Pipeline.WriteMainView(view.Constants);

        // 1-5: the CPU shadow-matrix chain runs after the camera update and before the lighting UBO is written.
        // The rule itself is shared with DX and VK; this call site only decides when to run it.
        SceneFrameSetup.ApplyShadowChain(ref lightParams, view.Camera);

        // This must go through SetLighting because that path injects HdrExposure.
        // Writing the UBO directly would leave params0.y at zero in the shader,
        // and once text falls back to 1.0 through inverse-ACES compensation,
        // exposure immunity would stop working.
        // This matches the shared four-backend rule described by RenderQuality contract clause 5.
        SetLighting(lightParams);
    }

    // ============================================================
    // IMTLBuffer direct write and read helpers, where IMTLBuffer.Contents is a persistent IntPtr
    // ============================================================

    protected static unsafe void WriteStruct<T>(IMTLBuffer buffer, T value, nuint offset = 0) where T : unmanaged
    {
        *(T*)((byte*)buffer.Contents + (long)offset) = value;
    }

    protected static unsafe T ReadStruct<T>(IMTLBuffer buffer, nuint offset = 0) where T : unmanaged
    {
        return *(T*)((byte*)buffer.Contents + (long)offset);
    }

    // ============================================================
    // Instance side: UBO creation, used during PrimitiveData initialization by derived classes
    // ============================================================

    protected void CreateMatrixBuffer(PrimitiveData primitiveData)
    {
        int n = Device.frameCount;
        primitiveData.MatrixBuffers = new IMTLBuffer[n];
        for (int i = 0; i < n; i++)
            primitiveData.MatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer((nuint)Unsafe.SizeOf<MatrixBuffer>());
    }

    protected void CreateMaterialBuffer(PrimitiveData primitiveData)
    {
        int n = Device.frameCount;
        primitiveData.MaterialBuffers = new IMTLBuffer[n];
        for (int i = 0; i < n; i++)
            primitiveData.MaterialBuffers[i] = Device.ResourceManager.CreateConstantBuffer((nuint)Unsafe.SizeOf<MaterialParams>());
    }

    /// <summary>Derived override that returns the palette ring this group binds at VS buffer(6), indexed by frame. The default is the global Identity ring; the retired b3 slot no longer carries skinning.</summary>
    protected virtual IMTLBuffer[] BonePaletteBuffers => IdentityBoneBuffers;

    /// <summary>Derived override that returns the current-frame per-instance bone buffer used by shell rendering at VS buffer(6). The default is the global Identity buffer.</summary>
    protected virtual IMTLBuffer InstanceBoneBufferForDraw(int fi) => IdentityInstanceBoneBuffers[fi];

    /// <summary>Derived override that returns the previous-frame per-instance bone buffer used by shell rendering at VS buffer(10). The default is the global Identity buffer.</summary>
    protected virtual IMTLBuffer PrevInstanceBoneBufferForDraw(int fi) => IdentityInstanceBoneBuffers[fi];

    /// <summary>Derived override for shell-rendering bone addressing stride, measured in palette matrices per instance, where boneOffset = 64B × stride × slot.
    /// The default is 1, and non-skinned shells always use offset 0 and therefore ignore this value.</summary>
    protected virtual int ShellBonePaletteStride => 1;

    /// <summary>
    /// Morph-target delta buffer at VS buffer(5), laid out as [target][vertex] × 9 floats,
    /// which means 3 floats each for position, normal, and tangent.
    /// This matches the offset calculation in MSL vertex_main exactly.
    /// The path is shared with DX and VK, where morphing is always performed on the GPU
    /// and the CPU never rewrites the vertex buffer, because doing so would make it impossible to reconstruct the previous-frame shape.
    /// </summary>
    /// <summary>
    /// Phase 3 packs morph-target deltas into a buffer&lt;float&gt; layout where
    /// [targetIndex * vertexCount + vertexIndex] * 9 floats corresponds to pos.xyz plus normal.xyz plus tangent.xyz.
    /// When vertexMap is not null, expansion follows that mapping:
    /// for shell-vertex layout, the delta of shell vertex v comes from source delta [vertexMap[v]],
    /// so vertex count equals vertexMap.Count.
    /// The source-primitive path passes no map, meaning identity mapping and vertex count equal to baseVertices.Length.
    /// </summary>
    protected static void CreateMorphDeltaBuffer(PrimitiveData primitive, Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets, IReadOnlyList<int>? vertexMap = null)
    {
        // The [target][vertex] × 9 layout, the sparse-target zero fill, and the shell vertex map all live in
        // MorphDelta.Pack, shared with DX/VK/Web. Only the upload below is backend-specific.
        var deltaData = MorphDelta.Pack(baseVertices, morphTargets, vertexMap);

        primitive.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer((nuint)(sizeof(float) * deltaData.Length));
        Device.ResourceManager.UpdateBuffer(primitive.MorphDeltasBuffer, deltaData);
        primitive.OwnsMorphDeltasBuffer = true;
    }

    /// <summary>Takes the first four node morph weights, matching the MSL-side limit where morphTargetCount is less than 4u.</summary>
    protected static Vector4 ExtractMorphWeights(GltfNodeBase? node) => MorphDelta.ExtractWeights(node);

    /// <summary>
    /// Contract clause 8(b) of 2-3:
    /// previous-frame slot in the frame ring.
    /// The bone-UBO frame ring is rewritten in full every frame rather than incrementally,
    /// so slot [FrameIndex - 1] is exactly the palette submitted on the previous frame.
    /// It can therefore be used directly as the prev-bone source with no extra buffer or copy.
    /// </summary>
    protected static int PrevFrameIndex => (Device.FrameIndex + Device.frameCount - 1) % Device.frameCount;

    // ============================================================
    // Instance side: sync path bindings
    // (the alpha / tint / material-parameter walks live in PrimitiveGroupCore)
    // ============================================================

    // ============================================================
    // Instance side: material-texture replacement, with primitive lists supplied by derived classes through CollectPrimitives
    // ============================================================

    /// <summary>
    /// 2-6 clause 1: mip policy implied by a material slot. Runtime replacement must resolve this the same way the
    /// glTF load path does, otherwise an overridden texture would silently lose the chain its neighbours have.
    /// </summary>
    static TextureMipPolicy MipPolicyForSlot(TextureSlot slot) => slot switch
    {
        TextureSlot.Normal => TextureMipPolicy.Normal,
        TextureSlot.MetallicRoughness => TextureMipPolicy.Linear,
        TextureSlot.Occlusion => TextureMipPolicy.Linear,
        _ => TextureMipPolicy.Color,
    };

    /// <summary>
    /// Replaces the texture in the specified slot for all primitives.
    /// The current implementation always follows the create-new path.
    /// </summary>
    internal void ReplaceTextureBySlot(TextureSlot slot, INativeImageDecoder decoder)
    {
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0) return;

        var oldTex = _drawList[0].Texture(slot);
        if (oldTex == null) return;

        bool sameSize = (uint)decoder.Width == oldTex.Width
                     && (uint)decoder.Height == oldTex.Height;
        bool exclusive = oldTex.RefCount == 1;

        if (sameSize && exclusive)
        {
            // UploadPixels regenerates the whole chain from the policy the texture was created with, so the fast path
            // stays correct for mipmapped textures too.
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = Texture.CreateFromDecoder(decoder, MipPolicyForSlot(slot));
            foreach (var primitive in _drawList)
                primitive.Texture(slot) = newTex;
        }
    }

    // ============================================================
    // Instance side: derived hooks
    // ============================================================

    /// <summary>Implemented by derived classes to append all PrimitiveData objects that should be drawn in the current frame into result.</summary>
    protected abstract override void CollectPrimitives(List<PrimitiveData> result);

    // ============================================================
    // Core hooks
    // (the Opaque / Fade / Transparent flow and the shadow-pass cache live in PrimitiveGroupCore;
    // these bind it to Metal)
    // ============================================================

    protected override string TraceBackend => "mtl";

    protected override int FrameCount => Device.frameCount;

    protected override unsafe ref MaterialParams MaterialSlot(PrimitiveData primitive, int frame)
        => ref Unsafe.AsRef<MaterialParams>((void*)primitive.MaterialBuffers[frame].Contents);

    /// <summary>Metal submits in collection order and never sorts the transparent bucket: a double-sided primitive is
    /// drawn front then back at its own position in the list.</summary>
    protected override TransparentOrder TransparentBucketOrder => TransparentOrder.Submission;

    protected override void ApplySurfaceState(in SurfaceState state)
    {
        var enc = Device.GraphicsEncoder;

        // Explicit switches, never casts: PipelineMode orders Opaque/Transparent/Fade while SurfaceMode orders
        // Opaque/Fade/Transparent, so a cast would silently swap Fade and Transparent.
        var mode = state.Mode switch
        {
            SurfaceMode.Opaque => PipelineMode.Opaque,
            SurfaceMode.Fade => PipelineMode.Fade,
            _ => PipelineMode.Transparent,
        };

        // The PSO variant can express only None and Back, so the front pass of the two-pass double-sided
        // transparent scheme is the one that needs an explicit rebind. The pass that follows it is rebound to Back
        // as well, exactly as the per-backend code did: redundant on the encoder, but it is what the draw trace of
        // this backend has always recorded.
        bool needsCull = state.Cull == SurfaceCull.Front
            || (state.Cull == SurfaceCull.Back && state.Mode == SurfaceMode.Transparent && _cullWasFront);

        Pipeline.SetPipeline(enc, mode, doubleSided: state.Cull == SurfaceCull.None, state.DepthWrite);

        _cullWasFront = state.Cull == SurfaceCull.Front;

        if (needsCull)
            Pipeline.SetCullMode(enc, state.Cull == SurfaceCull.Front ? MTLCullMode.Front : MTLCullMode.Back);
    }

    protected override void DrawMainPrimitive(PrimitiveData primitive)
        => DrawPrimitive(Device.GraphicsEncoder, primitive);

    /// <summary>Metal binds everything a shadow draw needs per primitive, so the pass opens no group-wide state.</summary>
    protected override void BeginShadowPass() { }

    protected override void DrawShadowPrimitive(PrimitiveData primitive, bool bindMaterial)
        => DrawShadowPrimitive(Device.GraphicsEncoder, primitive, bindMaterial);

    /// <summary>Metal binds the material block through the encoder, so a group whose primitives agree on it can bind
    /// once. The rule itself lives in <see cref="ShadowMaterialSharing.CanShare"/>.</summary>
    protected override bool CanShareShadowMaterial(List<PrimitiveData> primitives)
        => ShadowMaterialSharing.CanShare(primitives);

    /// <summary>Metal gates the previous-bone bit per primitive: a shell inherits IsSkinned from its source, and only a
    /// skinned shell has a palette to read at all.</summary>
    protected override bool ShellPrevBonesApplies(PrimitiveData primitive)
        => primitive.MaterialParams.IsSkinned != 0;

    /// <summary>Same three groups of boxes as before, in the same order: the host Bounds box, then the per-instance
    /// Bounds boxes, then the per-primitive Wireframe shells.</summary>
    protected override void DrawHighlights()
    {
        if (_boundsActive && _boundsBox != null)
            DrawHighlightBox(_boundsBox);
        DrawBoundsBoxes();
        if (_wireframeEnabled && _wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var highlight = _wireframeBoxes[i];
                if (highlight != null)
                    DrawHighlightBox(highlight);
            }
        }
    }

    // ============================================================
    // Unified highlighting: highlight boxes with separate face and edge primitives, lazily built with no extra PSO families, using Transparent for faces and Opaque for edges
    // ============================================================
    /// <summary>
    /// Unified highlighting for Bounds-box geometry on the face primitive:
    /// eight corners are baked into [-0.5, 0.5]^3 with corner indices encoded as x + y * 2 + z * 4,
    /// with 36 indices total.
    /// RenderMode = 0 for Unlit and AlphaMode = 2 for BLEND, so true transparency uses the Transparent PSO.
    /// The primitive is DoubleSided and binds White for all five textures with Use*Map = 0 because the Unlit path does not sample them.
    /// Geometry is reused from shared HighlightGeometry and matches DX and VK bit for bit.
    /// </summary>
    protected override PrimitiveData CreateBoundsFacePrimitive()
    {
        var primitive = new PrimitiveData
        {
            Vertices = HighlightGeometry.BuildBoxFaceVertices(),
            Indices = HighlightGeometry.BuildBoxFaceIndices().ToArray(),
            Use32BitIndices = false,
            DoubleSided = true,
            IsTransparent = true,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = new MaterialParams
            {
                RenderMode = 0u,
                AlphaMode = 2u,
                AlphaCutoff = 0.5f,
                BaseColor = new Vector4(1f, 1f, 1f, 0.3f),
            },
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>
    /// Unified highlighting for Bounds-box geometry on the edge primitive:
    /// twelve thin boxes are built, three axes times four edges per axis.
    /// Each edge extends one thickness beyond the corner along its axis, spanning [-0.5 - h, 0.5 + h],
    /// so all eight corners join seamlessly.
    /// RenderMode = 0 and AlphaMode = 0 for OPAQUE, so solid edges use the Opaque PSO with depth writes
    /// and do not pulse with face alpha, preserving the always-solid meaning of EdgeColor.
    /// </summary>
    protected override PrimitiveData CreateBoundsEdgesPrimitive()
    {
        var indices = new List<uint>(12 * 36);
        var primitive = new PrimitiveData
        {
            Vertices = HighlightGeometry.BuildBoxEdgesVertices(indices),
            Indices = indices.ToArray(),
            Use32BitIndices = false,
            DoubleSided = true,
            IsTransparent = false,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = new MaterialParams
            {
                RenderMode = 0u,
                AlphaMode = 0u,
                AlphaCutoff = 0.5f,
                BaseColor = new Vector4(1f, 0f, 0f, 1f),
            },
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting: creates the VB, IB, Material UBO, and Matrix UBO for box primitives and initializes them for all frames.
    /// This avoids garbage values on N-buffered frames, and Metal needs no DescriptorSet because bindings are issued per draw during rendering.</summary>
    void InitBoundsBoxGpuResources(PrimitiveData primitive)
    {
        primitive.VertexBuffer = Device.ResourceManager.CreateVertexBuffer(primitive.Vertices.ToArray());
        primitive.IndexBuffer = Device.ResourceManager.CreateIndexBuffer(primitive.Indices);
        primitive.BaseColorTexture = Device.White;
        primitive.NormalTexture = Device.White;
        primitive.MetallicRoughnessTexture = Device.White;
        primitive.OcclusionTexture = Device.White;
        primitive.EmissiveTexture = Device.White;

        CreateMatrixBuffer(primitive);
        CreateMaterialBuffer(primitive);

        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        for (int i = 0; i < Device.frameCount; i++)
        {
            WriteStruct(primitive.MatrixBuffers[i], matrices);
            WriteStruct(primitive.MaterialBuffers[i], primitive.MaterialParams);
        }
    }

    /// <summary>
    /// Unified highlighting:
    /// every frame writes the box world matrix plus face and edge colors into the box-owned N-buffered UBOs for the current frame only.
    /// There is no Changed gate because face alpha from SurfaceColor.W may pulse every frame,
    /// and the constant write cost matches ordinary primitives.
    /// PrevWorld comes from the CPU shadow copy in box.PrevWorld, and after writing, the current-frame world rolls into the shadow copy for next-frame history.
    /// The caller provides world:
    /// Bounds uses Scale(Extents × 2) × Translate(Center) after the caller checks for degenerate boxes,
    /// while Wireframe uses the always-valid model or instance world matrix.
    /// </summary>
    protected override void WriteHighlightBox(HighlightBox box, Matrix4x4 world, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            PrevWorld = Matrix4x4.Transpose(box.PrevWorld),
        };
        WriteStruct(box.Face.MatrixBuffers[fi], matrices);
        WriteStruct(box.Edges.MatrixBuffers[fi], matrices);

        box.Face.MaterialParams.BaseColor = faceColor;
        WriteStruct(box.Face.MaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        WriteStruct(box.Edges.MaterialBuffers[fi], box.Edges.MaterialParams);

        box.PrevWorld = world;
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting: draws one highlight box.
    /// When face alpha from SurfaceColor.W is greater than zero, the engine's two-pass double-sided transparency convention is used,
    /// drawing faces Front then Back.
    /// Zero means edge-only rendering and skips faces automatically.
    /// Edges use the Opaque path with CullNone and depth writes, so solid thin strips cover the faces and any interior geometry.
    /// Bones and morph data are bound directly through SetVertexBuffer, with the palette at VS buffer(6).</summary>
    protected override void DrawHighlightBox(HighlightBox box)
    {
        var face = box.Face;
        var edges = box.Edges;
        var enc = Device.GraphicsEncoder;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            Pipeline.SetCullMode(enc, MTLCullMode.Front);
            DrawPrimitive(enc, face);

            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            Pipeline.SetCullMode(enc, MTLCullMode.Back);
            DrawPrimitive(enc, face);
        }

        Pipeline.SetPipeline(enc, PipelineMode.Opaque, doubleSided: true);
        DrawPrimitive(enc, edges);
    }

    /// <summary>Unified highlighting: draws every instance Bounds box enabled in the current frame by calling DrawHighlightBox for each one.</summary>
    protected void DrawBoundsBoxes()
    {
        for (int i = 0; i < _boundsBoxDrawList.Count; i++)
            DrawHighlightBox(_instanceBoundsBoxes[_boundsBoxDrawList[i]]);
    }

    /// <summary>Unified highlighting: attaches the morph path to a shell primitive by expanding source deltas to shell-vertex layout.
    /// The shell-to-source vertex index mapping is recorded during shell construction.
    /// It sets HasMorphTargets, MorphTargetCount, and MorphVertexCount, where the latter equals the shell vertex count,
    /// and writes them back to Material UBOs for all frames.
    /// Weights are shared with the source and synchronized every frame by MTLModel.ApplyMorphTargetsIfNeeded.
    /// Metal has no DescriptorSet objects, so the morph-delta buffer is bound per draw through the morphBuffer parameter of DrawPrimitive,
    /// and after creation no existing bindings need to be rewritten.</summary>
    protected override void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(shell.MaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
    }

    /// <summary>The core's template hook, typed to this backend's PrimitiveData. The factory keeps its own name
    /// because the existing call sites read the same way.</summary>
    protected override PrimitiveData CreateSharedGeometryPrimitive(PrimitiveData template)
        => CreateSharedShellPrimitive(template);

    /// <summary>Unified highlighting: derives a shared-geometry box from a template primitive.
    /// It copies CPU-side vertex and index array references together with material and texture references,
    /// while GPU pointers stay at default values.
    /// PrimitiveData.Dispose is null-safe, so no double release occurs.
    /// Vertices and indices are immutable shared data, and Dispose only releases GPU buffers,
    /// so aliasing is safe because DrawPrimitive uses Indices.Length each draw to determine index count.
    /// It creates its own N-buffered Matrix and Material UBOs and initializes them for all frames,
    /// with identity matrices because instanced rendering resolves transforms by instance-stream slot.</summary>
    PrimitiveData CreateSharedShellPrimitive(PrimitiveData template)
    {
        var primitive = new PrimitiveData
        {
            Vertices = template.Vertices,
            Indices = template.Indices,
            Use32BitIndices = template.Use32BitIndices,
            DoubleSided = template.DoubleSided,
            IsTransparent = template.IsTransparent,
            LocalBoundsCenter = template.LocalBoundsCenter,
            LocalBoundsExtents = template.LocalBoundsExtents,
            MaterialParams = template.MaterialParams,
            BaseColorTexture = template.BaseColorTexture,
            NormalTexture = template.NormalTexture,
            MetallicRoughnessTexture = template.MetallicRoughnessTexture,
            OcclusionTexture = template.OcclusionTexture,
            EmissiveTexture = template.EmissiveTexture,
        };
        CreateMatrixBuffer(primitive);
        CreateMaterialBuffer(primitive);
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 stays all zeros on the instanced path because history comes from the double-buffered instance stream.
        };
        for (int i = 0; i < Device.frameCount; i++)
        {
            WriteStruct(primitive.MatrixBuffers[i], matrices);
            WriteStruct(primitive.MaterialBuffers[i], primitive.MaterialParams);
        }
        return primitive;
    }

    /// <summary>Unified highlighting on the instanced path:
    /// writes face and edge colors plus current-frame matrices into the per-instance shell box's own N-buffered UBOs for the current frame only,
    /// and records the current-frame face alpha.
    /// World stays identity because per-instance world matrices are resolved by instance-stream writeIndex slot.
    /// View, Projection, and PrevViewProjection must still be rewritten every frame from the current camera,
    /// because writing them only once during creation through CreateSharedShellPrimitive would permanently lock the shell to the camera-space VP at creation time,
    /// making Wireframe move with the camera instead of staying attached to the character.
    /// PrevWorld stays all zeros,
    /// because contract clause 8(d) routes per-instance world history through the double-buffered instance stream and shaders do not read b0 prevWorld on the instanced path.</summary>
    protected void WriteInstanceShell(HighlightBox box, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
        };
        WriteStruct(box.Face.MatrixBuffers[fi], matrices);
        WriteStruct(box.Edges.MatrixBuffers[fi], matrices);
        box.Face.MaterialParams.BaseColor = faceColor;
        WriteStruct(box.Face.MaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        WriteStruct(box.Edges.MaterialBuffers[fi], box.Edges.MaterialParams);
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting on the instanced path:
    /// draws one per-instance shell box with instanceCount = 1 plus an instance-stream slot offset given by instanceOffset.
    /// When face alpha from SurfaceColor.W is greater than zero, faces are drawn with the two-pass double-sided transparent convention.
    /// Zero means edge-only rendering and skips faces automatically.
    /// Edges use the Opaque path with CullNone and depth writes.
    /// Geometry comes from shared template geo, passing _shellGeometry for the rigid pool and _skinnedShellGeometry for the skinned pool,
    /// while the box itself owns no VB or IB, as described by CreateSharedShellPrimitive.
    /// Skinned shells with IsSkinned = 1 bind the real per-instance bone-frame rings for current and previous frames through virtual accessors,
    /// and compute boneOffset as sizeof(Matrix4x4) × stride × slot, mirroring the 21-parameter signature used by the transparent per-slot path.
    /// Non-skinned shells keep the global Identity buffers and offset 0.
    /// prevInstanceBuffer is provided by the caller and points to the other side of the double-buffered instance stream.
    /// HasPrevBones and HasPrevInstanceWorld are inherited from source material through the shell template and are
    /// republished every frame together with the main primitives, so a withdrawn history frame turns the shell off too.</summary>
    protected void DrawInstanceShellBox(HighlightBox box, HighlightBox geo, IMTLBuffer instanceBuffer, nuint instanceOffset, IMTLBuffer? prevInstanceBuffer)
    {
        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        var face = box.Face;
        var edges = box.Edges;
        bool skinnedShell = box.Face.MaterialParams.IsSkinned != 0;
        var instanceBones = skinnedShell ? InstanceBoneBufferForDraw(fi) : IdentityInstanceBoneBuffers[fi];
        var prevInstanceBones = skinnedShell ? PrevInstanceBoneBufferForDraw(fi) : IdentityInstanceBoneBuffers[fi];
        nuint boneOffset = 0;
        if (skinnedShell)
        {
            int slotIndex = (int)(instanceOffset / (nuint)Unsafe.SizeOf<InstanceTransformData>());
            boneOffset = (nuint)(Unsafe.SizeOf<Matrix4x4>() * ShellBonePaletteStride * slotIndex);
        }

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            Pipeline.SetCullMode(enc, MTLCullMode.Front);
            Pipeline.DrawPrimitive(enc, face, geo.Face.VertexBuffer, geo.Face.IndexBuffer,
                face.MatrixBuffers[fi], face.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], face.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], instanceBones,
                MTLPrimitiveType.Triangle, (nuint)face.Indices.Length,
                face.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                instanceBuffer, instanceOffset, 1, boneOffset, prevInstanceBuffer, prevInstanceBones, boneOffset);

            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            Pipeline.SetCullMode(enc, MTLCullMode.Back);
            Pipeline.DrawPrimitive(enc, face, geo.Face.VertexBuffer, geo.Face.IndexBuffer,
                face.MatrixBuffers[fi], face.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], face.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], instanceBones,
                MTLPrimitiveType.Triangle, (nuint)face.Indices.Length,
                face.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                instanceBuffer, instanceOffset, 1, boneOffset, prevInstanceBuffer, prevInstanceBones, boneOffset);
        }

        Pipeline.SetPipeline(enc, PipelineMode.Opaque, doubleSided: true);
        Pipeline.DrawPrimitive(enc, edges, geo.Edges.VertexBuffer, geo.Edges.IndexBuffer,
            edges.MatrixBuffers[fi], edges.MaterialBuffers[fi],
            LightConstantBuffers[fi], IdentityBoneBuffers[fi], edges.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], instanceBones,
            MTLPrimitiveType.Triangle, (nuint)edges.Indices.Length,
            edges.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            instanceBuffer, instanceOffset, 1, boneOffset, prevInstanceBuffer, prevInstanceBones, boneOffset);
    }

    /// <summary>Unified highlighting on the instanced path:
    /// draws every shell box whose Wireframe highlight is enabled this frame by calling DrawInstanceShellBox for each one.
    /// instanceBuffer is the instance stream, either the base-class _instanceBuffer or a per-primitive stream managed by a derived class such as MTLInstancedModel.
    /// Slot layout, using InstanceTransformData stride, is structurally identical across streams, so any of them can be used.
    /// Hybrid assets draw both rigid and skinned shells, taking one box from each pool for the same writeIndex.</summary>
    protected void DrawShellBoxes(IMTLBuffer instanceBuffer, IMTLBuffer? prevInstanceBuffer)
    {
        for (int i = 0; i < _shellBoxDrawList.Count; i++)
        {
            int idx = _shellBoxDrawList[i];
            nuint instOffset = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * idx);
            if ((uint)idx < (uint)_instanceShellBoxes.Count)
            {
                var box = _instanceShellBoxes[idx];
                if (box != null && _shellGeometry != null)
                    DrawInstanceShellBox(box, _shellGeometry, instanceBuffer, instOffset, prevInstanceBuffer);
            }
            if ((uint)idx < (uint)_skinnedInstanceShellBoxes.Count)
            {
                var box = _skinnedInstanceShellBoxes[idx];
                if (box != null && _skinnedShellGeometry != null)
                    DrawInstanceShellBox(box, _skinnedShellGeometry, instanceBuffer, instOffset, prevInstanceBuffer);
            }
        }
    }

    /// <summary>Unified highlighting: builds a shell primitive from vertices and indices.
    /// Material parameters are copied from the source primitive and then forced to Unlit plus either transparent BLEND or opaque OPAQUE behavior.
    /// The primitive is DoubleSided and binds White for all five textures because the Unlit path does not sample them.
    /// VB, IB, and both UBOs are created and initialized for all frames through InitBoundsBoxGpuResources.
    /// Skinning and instancing flags such as IsSkinned, BonePaletteStride, and IsInstanced are inherited from source material,
    /// which is critical for non-instanced per-primitive shell boxes to stay aligned with animation.</summary>
    protected override PrimitiveData CreateShellPrimitive(List<Vertex> vertices, uint[] indices, MaterialParams sourceParams, bool isTransparent)
    {
        var mp = sourceParams;
        mp.RenderMode = 0u;
        mp.AlphaMode = isTransparent ? 2u : 0u;
        mp.AlphaCutoff = 0.5f;

        var primitive = new PrimitiveData
        {
            Vertices = vertices,
            Indices = indices,
            // Index-buffer bit width must match ResourceManager.CreateIndexBuffer content detection,
            // which stores 16-bit indices when all values are at most 65535.
            // Shell-face vertex counts may stay below 65536, so hard-coding 32-bit would bind a 16-bit IB as UInt32,
            // scrambling indices and making faces disappear.
            // The root cause matches VK InitShellPrimitive and the fix follows VKPrimitiveGroup.
            Use32BitIndices = indices.Any(i => i > ushort.MaxValue),
            DoubleSided = true,
            IsTransparent = isTransparent,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = mp,
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    public abstract void Dispose();

    // ============================================================
    // GPU submission helpers, driven by the shared core
    // ============================================================

    void DrawShadowPrimitive(IMTLRenderCommandEncoder enc, PrimitiveData p, bool bindMaterial)
    {
        int fi = Device.FrameIndex;
        Pipeline.DrawShadowPrimitive(enc, p, p.VertexBuffer, p.IndexBuffer,
            p.MatrixBuffers[fi], p.MaterialBuffers[fi], IdentityBoneBuffers[fi],
            p.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], BonePaletteBuffers[fi],
            (nuint)p.Indices.Length, p.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
            null, 0, 1, 0, bindMaterial);
    }

    /// <summary>
    /// The unified exit for primitive drawing; after work package D it delegates directly to <see cref="Pipeline.DrawPrimitive"/>.
    /// Slot 3 stays on the shared identity placeholder now that no shader declares b3; the group palette goes to slot 6,
    /// where the non-instanced route addresses joint i from row zero, and slot 10 carries the previous-frame ring.
    /// Slot 2/9 use the identity instance stream, and the index type and offset are unchanged.
    /// This way the collection phase only needs to intercept at one place, Pipeline.DrawPrimitive, and all 3D draws of non-instanced models (including the highlight shell and shell box) enter the segmented queue.
    /// </summary>
    void DrawPrimitive(IMTLRenderCommandEncoder enc, PrimitiveData p)
    {
        int fi = Device.FrameIndex;
        var palette = BonePaletteBuffers;
        Pipeline.DrawPrimitive(enc, p, p.VertexBuffer, p.IndexBuffer,
            p.MatrixBuffers[fi], p.MaterialBuffers[fi], LightConstantBuffers[fi], IdentityBoneBuffers[fi],
            p.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], palette[fi],
            MTLPrimitiveType.Triangle, (nuint)p.Indices.Length,
            p.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
            null, 0, 1, 0, null, palette[PrevFrameIndex], 0);
    }

    // ============================================================
    // Outline2D mask-pass rendering, called group by group from Graphics.RenderOutlineMask
    // ============================================================

    /// <summary>
    /// Outline2D mask rendering for the non-instanced path:
    /// it draws all opaque primitives and skips true BLEND materials.
    /// The mask PSO is already routed by SetPipeline from ActivePassId,
    /// and this method writes outline color per group through FS buffer(0).
    /// Culling follows DoubleSided,
    /// and DSS uses the dedicated mask variant with no depth write plus LessEqual,
    /// mirroring DX and VK with depthWrite = false.
    /// Each primitive binds the group palette at VS buffer(6) and draws immediately,
    /// with slot 3 left on the shared placeholder.
    /// This mirrors DX and VK DrawOutlineMask one to one.
    /// </summary>
    public virtual void DrawOutlineMask()
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        DrawTrace.Group("mtl", "outline", Name, _drawList.Count);

        var enc = Device.GraphicsEncoder;
        Pipeline.SetOutlineMaskColor(enc, _outline2DColor);

        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent)
                continue;
            // On the OutlineMask pass, SetPipeline first checks ActivePassId and routes to the mask PSO,
            // mask DSS, and matching culling state.
            // Rebind mask DSS explicitly here as a safety net in case future SetPipeline changes forget it,
            // then draw primitive by primitive.
            Pipeline.SetPipeline(enc, PipelineMode.Opaque, p.DoubleSided);
            enc.SetDepthStencilState(Pipeline.OutlineMaskDepthState);
            DrawPrimitive(enc, p);
        }
    }
}
