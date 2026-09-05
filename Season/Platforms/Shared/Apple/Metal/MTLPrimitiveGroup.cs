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
/// - Whether bones exist, where MTLModel overrides BoneMatrixBuffers and uploads bone matrices in OnBeforeDraw.
///
/// Simplifications relative to Vulkan:
/// - No DescriptorSet objects are needed because DrawPrimitive binds directly through SetVertexBuffer, SetFragmentBuffer, and SetFragmentTexture.
/// - No EnsureReadyForRendering step is needed because StorageMode.Private plus BlitEncoder synchronization handles it automatically.
/// - No mapped-pointer cache is needed because IMTLBuffer.Contents is a persistent IntPtr and supports direct writes and reads.
/// </summary>
internal abstract class MTLPrimitiveGroup : IDisposable
{
    /// <summary>Each bone matrix occupies 64 bytes. Matching DX and VK, the UBO holds up to 100 bones.</summary>
    public const int MaxBones = 100;

    // === Globally shared camera for all Pbr3D primitives ===
    internal static Camera Camera;

    // === Globally shared lighting UBOs, N-buffered at FS slot 1 ===
    internal static IMTLBuffer[] LightConstantBuffers = null!;

    // === Globally shared identity-bone UBO for primitive groups without skinning, used as the dummy binding at VS slot 3 ===
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

    // === Shared instance state ===
    internal string Name = string.Empty;

    /// <summary>The most recent overall alpha written into material buffers, used to drive three-bucket PSO grouping.</summary>
    protected float _currentAlpha = 1.0f;

    /// <summary>The most recent color multiplier written into material buffers, used for Mesh3D.ColorTint synchronization and rewritten only on change.</summary>
    protected Vector4 _currentColorTint = Vector4.One;

    /// <summary>Whether the first Update has completed. When false, Draw skips immediately to avoid rendering with identity matrices.</summary>
    protected bool _transformInitialized;

    /// <summary>Reusable draw list that avoids allocating a new List every frame and triggering GC.</summary>
    readonly List<PrimitiveData> _drawList = new(64);

    /// <summary>Projected primitive list for the current shadow pass in 1-5.
    /// It is separate from the main-pass _drawList so the two paths never overwrite each other.
    /// The same list is replayed across the four atlas quadrants by slot and invalidated by <see cref="Season.Rendering.CascadedShadow.Epoch"/>, as described in DrawShadow.
    /// This mirrors DX _shadowDrawList one to one.</summary>
    readonly List<PrimitiveData> _shadowDrawList = new(64);

    /// <summary>The epoch already collected for _shadowDrawList, where int.MinValue means it has never been collected.</summary>
    int _shadowDrawListEpoch = int.MinValue;

    // === Unified highlighting: Bounds-box state, including the host box and lazily built instance-box pool; wireframe-shell state is added in phase 3 ===

    /// <summary>Whether the host, non-instanced, Bounds box is enabled this frame. Written during Update and used as a zero-cost gate during Draw.</summary>
    protected bool _boundsActive;

    /// <summary>Host, non-instanced, Bounds box. It is lazily created on the first enabled frame and remains resident afterward.</summary>
    protected HighlightBox _boundsBox = null!;

    /// <summary>Per-instance Bounds-box pool, indexed by compressed writeIndex. It grows lazily and remains resident until the group is released.</summary>
    protected readonly List<HighlightBox> _instanceBoundsBoxes = new();

    /// <summary>Compressed instance indices whose Bounds boxes are enabled this frame, rebuilt on each Update and used by Draw to render boxes one by one.</summary>
    protected readonly List<int> _boundsBoxDrawList = new();

    // === Unified highlighting: wireframe-shell state, including non-instanced per-primitive boxes and lazily built shared templates for instancing ===

    /// <summary>Whether host, non-instanced, Wireframe is enabled this frame. Written during Update and used as a zero-cost gate during Draw.</summary>
    protected bool _wireframeEnabled;

    /// <summary>Per-primitive wireframe shell boxes for the non-instanced path.
    /// They follow the CollectPrimitives order, use null placeholders for primitives with no valid triangles,
    /// are lazily created on the first enabled frame,
    /// and remain resident afterward without being rebuilt or released when toggled on and off at runtime.</summary>
    protected List<HighlightBox?>? _wireframeBoxes;

    /// <summary>Shared shell template for instancing, built from merged shell faces and edges of all non-skinned, non-morph primitives.
    /// It is lazily created on the first enabled frame and remains resident afterward,
    /// while per-instance shell boxes share its VB and IB.</summary>
    protected HighlightBox? _shellGeometry;

    /// <summary>Shared skinned-shell geometry for instancing, built by merging all skinned primitives that share the same Skin.
    /// With IsSkinned = 1, it uses the per-instance bone-palette path and therefore matches animation through the same vertex-shader skinning path as the main pass.
    /// It is lazily created once on the first enabled frame and remains resident afterward.
    /// Multi-skin assets, where each node owns a separate Skin, are skipped in phase 1 and remain null so later frames can retry.</summary>
    protected HighlightBox? _skinnedShellGeometry;

    /// <summary>Per-instance wireframe shell-box pool, indexed by compressed writeIndex.
    /// It grows lazily, shares template geometry, owns its own Matrix and Material UBOs,
    /// and uses null placeholders while the template is not yet ready.</summary>
    protected readonly List<HighlightBox?> _instanceShellBoxes = new();

    /// <summary>Per-instance pool of skinned wireframe shell boxes, sharing the skinned template geometry.
    /// It uses the same index space as _instanceShellBoxes,
    /// and hybrid assets draw one box from each pool for the same writeIndex.</summary>
    protected readonly List<HighlightBox?> _skinnedInstanceShellBoxes = new();

    /// <summary>Compressed instance indices whose Wireframe highlight is enabled this frame, rebuilt on each Update and used by Draw to render shell boxes one by one.</summary>
    protected readonly List<int> _shellBoxDrawList = new();

    /// <summary>The edgeWidth used by the most recent shell-geometry build. It is compared with host Highlight.EdgeWidth and triggers release plus rebuild when changed.</summary>
    protected float _builtShellEdgeWidth;

    // === Unified highlighting: Outline2D state on the mask path, where activation is collected by an independent Graphics pass ===

    /// <summary>Whether Outline2D mask rendering is active this frame. Written during Update and used as a zero-cost gate during DrawOutlineMask.</summary>
    protected bool _outline2DActive;

    /// <summary>Group-level outline color for the current frame, written into the mask per group. Multiple colors may coexist in the same frame and are resolved per pixel during composition.</summary>
    protected Vector4 _outline2DColor;

    /// <summary>Group-level outline width for the current frame. Frame-level aggregation takes the maximum so the widest outline remains fully visible.</summary>
    protected float _outline2DWidth;

    /// <summary>Unified entry point for Outline2D state, called by derived Update methods after host and instance state have been aggregated. This mirrors DX SetOutline2DState.</summary>
    protected void SetOutline2DState(bool active, Vector4 color, float width)
    {
        _outline2DActive = active;
        _outline2DColor = color;
        _outline2DWidth = width;
    }

    internal bool Outline2DActive => _outline2DActive;
    internal Vector4 Outline2DMaskColor => _outline2DColor;
    internal float Outline2DMaskWidth => _outline2DWidth;

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
        lightParams.Params0.Y = Device.HdrExposure;

        // Contract clause 6 of 2-3:
        // xy stores current-frame jitter in NDC, and zw stores inverse screen size
        // so the fragment shader can reconstruct NDC from [[position]].
        // When MotionVectors is disabled, JitterNdc remains zero,
        // and writing it is harmless because shaders with VELOCITY_OUTPUT = 0 do not read the field.
        var res = DeviceServices.BaseApp.DeviceResolution;
        var jitter = DeviceServices.BaseApp.Camera.JitterNdc;
        lightParams.VelocityParams = new Vector4(
            jitter.X, jitter.Y,
            res.X > 0 ? 1f / res.X : 0f,
            res.Y > 0 ? 1f / res.Y : 0f);

        // Contract clause 4 of 1-7:
        // inject environment parameters and resolve the current-frame radiance cube once per frame,
        // avoiding per-draw lookup in the rendering path.
        // When SceneEnvironment is null, EnvParams stays all zeros,
        // and the shader falls back per pixel to the 1-2 constant ambient light.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        env?.Apply(ref lightParams);
        MTLTextureCube.Active = env != null ? MTLTextureCube.Find(env.RadianceName) : null;

        // Contract clause 10 of 2-4:
        // inject DDGI GiParams0, GiParams1, and GiParams2 at a single point.
        // If not ready, leave them untouched and let consumers fall back.
        Season.Rendering.Effects.DdgiEffect.Apply(ref lightParams);

        // Step B of 2-5 at b11:
        // resolve sun disk, moon disk, and starlight into SkyParams0 through SkyParams3 through a single injection point.
        // When the StaticCube tier exits early, all four fields remain zero,
        // so the pixel-shader gate skyParams0.w > 0 stays false and leaves no residual path.
        Season.Rendering.SkyLighting.Apply(ref lightParams);

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
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;

        if (RenderQuality.Current.MotionVectors)
        {
            // Contract clause 4 of 2-3:
            // this is the only injection point for jitter.
            // UpdateTemporal first snapshots the previous-frame non-jittered ViewProjection and then rebuilds matrices,
            // baking jitter only into ProjectionJittered.
            // Frustum culling and CSM cascades still use the non-jittered camera3D.Projection and ViewProjection,
            // avoiding edge flicker and shadow shimmer.
            var res = DeviceServices.BaseApp.DeviceResolution;
            camera3D.UpdateTemporal(aspectRatio, res.X, res.Y);
            Camera.View = camera3D.View;
            Camera.Projection = camera3D.ProjectionJittered;
            Camera.PrevViewProjection = camera3D.PrevViewProjection;
        }
        else
        {
            camera3D.UpdateIfChanged(aspectRatio);
            Camera.View = camera3D.View;
            Camera.Projection = camera3D.Projection;
            // All zeros means no history, and even when a quality tier is disabled mid-run no stale matrices remain.
            Camera.PrevViewProjection = default;
        }

        // For 1-5, compute the CPU shadow-matrix chain after the camera update and before writing the lighting UBO.
        // When the feature is disabled or no light is active, Apply writes zeros.
        // Shadow sources are selected by indices in Params0.Z and Params0.W, written by the authorized Bake layer,
        // and light-type discrimination happens only here, matching DX and VK one to one.
        if (RenderQuality.Current.ShadowsEnabled)
        {
            CascadedShadow.BeginFrame();
            int dirIdx = (int)lightParams.Params0.Z;
            if (dirIdx >= 0 && dirIdx < lightParams.LightCount)
            {
                var dirType = lightParams.Lights[dirIdx].DirType;
                CascadedShadow.ComputeSun(camera3D, new Vector3(dirType.X, dirType.Y, dirType.Z));
            }
            int spotIdx = (int)lightParams.Params0.W;
            if (spotIdx >= 0 && spotIdx < lightParams.LightCount
                && lightParams.Lights[spotIdx].DirType.W == GpuLight.TypeSpot)
                CascadedShadow.ComputeSpot(in lightParams.Lights[spotIdx]);
            CascadedShadow.Apply(ref lightParams);
        }

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

    /// <summary>Derived override that returns the bone-UBO frame ring used by this primitive group. The default is the global Identity ring.</summary>
    protected virtual IMTLBuffer[] BoneMatrixBuffers => IdentityBoneBuffers;

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
        int vertexCount = vertexMap != null ? vertexMap.Count : baseVertices.Length;
        int targetCount = morphTargets.Count;
        int totalFloats = targetCount * vertexCount * 9;
        var deltaData = new float[totalFloats];

        for (int t = 0; t < targetCount; t++)
        {
            var target = morphTargets[t];
            int targetBase = t * vertexCount * 9;
            for (int v = 0; v < vertexCount; v++)
            {
                int srcIdx = vertexMap != null ? vertexMap[v] : v;
                int baseIdx = targetBase + v * 9;
                if (srcIdx < target.PositionDeltas.Length)
                {
                    deltaData[baseIdx] = target.PositionDeltas[srcIdx].X;
                    deltaData[baseIdx + 1] = target.PositionDeltas[srcIdx].Y;
                    deltaData[baseIdx + 2] = target.PositionDeltas[srcIdx].Z;
                }
                if (srcIdx < target.NormalDeltas.Length)
                {
                    deltaData[baseIdx + 3] = target.NormalDeltas[srcIdx].X;
                    deltaData[baseIdx + 4] = target.NormalDeltas[srcIdx].Y;
                    deltaData[baseIdx + 5] = target.NormalDeltas[srcIdx].Z;
                }
                if (srcIdx < target.TangentDeltas.Length)
                {
                    deltaData[baseIdx + 6] = target.TangentDeltas[srcIdx].X;
                    deltaData[baseIdx + 7] = target.TangentDeltas[srcIdx].Y;
                    deltaData[baseIdx + 8] = target.TangentDeltas[srcIdx].Z;
                }
            }
        }

        primitive.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer((nuint)(sizeof(float) * totalFloats));
        Device.ResourceManager.UpdateBuffer(primitive.MorphDeltasBuffer, deltaData);
        primitive.OwnsMorphDeltasBuffer = true;
    }

    /// <summary>Takes the first four node morph weights, matching the MSL-side limit where morphTargetCount is less than 4u.</summary>
    protected static Vector4 ExtractMorphWeights(GltfNodeBase? node)
    {
        if (node == null || node.Weights.Length == 0)
            return Vector4.Zero;

        var weights = node.Weights;
        return new Vector4(
            weights.Length > 0 ? weights[0] : 0f,
            weights.Length > 1 ? weights[1] : 0f,
            weights.Length > 2 ? weights[2] : 0f,
            weights.Length > 3 ? weights[3] : 0f);
    }

    /// <summary>
    /// Contract clause 8(b) of 2-3:
    /// previous-frame slot in the frame ring.
    /// The bone-UBO frame ring is rewritten in full every frame rather than incrementally,
    /// so slot [FrameIndex - 1] is exactly the palette submitted on the previous frame.
    /// It can therefore be used directly as the prev-bone source with no extra buffer or copy.
    /// </summary>
    protected static int PrevFrameIndex => (Device.FrameIndex + Device.frameCount - 1) % Device.frameCount;

    // ============================================================
    // Instance side: alpha synchronization
    // ============================================================

    /// <summary>
    /// Synchronizes overall alpha into the material buffer of every primitive:
    ///   BaseColor.W = OriginalBaseColorAlpha × alpha
    ///   AlphaCutoff = OriginalAlphaCutoff × alpha, which proportionally scales MASK cutoff and avoids clipping the whole object at low alpha
    /// It is called only when alpha changes, and writes all N-buffered frames to avoid flicker from stale values.
    /// </summary>
    protected void SyncAlpha(float alpha)
    {
        if (_currentAlpha == alpha)
            return;
        _currentAlpha = alpha;

        int n = Device.frameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var materialParams = ReadStruct<MaterialParams>(primitive.MaterialBuffers[i]);
                var baseColor = materialParams.BaseColor;
                baseColor.W = primitive.OriginalBaseColorAlpha * alpha;
                materialParams.BaseColor = baseColor;
                materialParams.AlphaCutoff = primitive.OriginalAlphaCutoff * alpha;
                WriteStruct(primitive.MaterialBuffers[i], materialParams);
            }
        }
    }

    // ============================================================
    // Instance side: color-multiplier synchronization for Mesh3D.ColorTint
    // ============================================================

    /// <summary>
    /// Synchronizes the mesh-level color multiplier into the material buffer of every primitive:
    ///   BaseColor.rgb = OriginalBaseColor.rgb × tint.rgb, while W stays untouched and the alpha chain remains owned solely by SyncAlpha
    /// It is called only when tint changes, and writes all N-buffered frames to avoid flicker from stale values.
    /// </summary>
    protected void SyncColorTint(Vector4 tint)
    {
        if (_currentColorTint == tint)
            return;
        _currentColorTint = tint;

        int n = Device.frameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var materialParams = ReadStruct<MaterialParams>(primitive.MaterialBuffers[i]);
                var baseColor = materialParams.BaseColor;
                baseColor.X = primitive.OriginalBaseColor.X * tint.X;
                baseColor.Y = primitive.OriginalBaseColor.Y * tint.Y;
                baseColor.Z = primitive.OriginalBaseColor.Z * tint.Z;
                materialParams.BaseColor = baseColor;
                WriteStruct(primitive.MaterialBuffers[i], materialParams);
            }
        }
    }

    // ============================================================
    // Instance side: material-texture replacement, with primitive lists supplied by derived classes through CollectPrimitives
    // ============================================================

    /// <summary>Gets the Texture reference stored in the specified slot of PrimitiveData.</summary>
    static Texture GetTextureBySlot(PrimitiveData p, TextureSlot slot) => slot switch
    {
        TextureSlot.BaseColor => p.BaseColorTexture,
        TextureSlot.Normal => p.NormalTexture,
        TextureSlot.MetallicRoughness => p.MetallicRoughnessTexture,
        TextureSlot.Occlusion => p.OcclusionTexture,
        TextureSlot.Emissive => p.EmissiveTexture,
        _ => p.BaseColorTexture
    };

    /// <summary>Sets the Texture reference stored in the specified slot of PrimitiveData.</summary>
    static void SetTextureBySlot(PrimitiveData p, TextureSlot slot, Texture tex)
    {
        switch (slot)
        {
            case TextureSlot.BaseColor: p.BaseColorTexture = tex; break;
            case TextureSlot.Normal: p.NormalTexture = tex; break;
            case TextureSlot.MetallicRoughness: p.MetallicRoughnessTexture = tex; break;
            case TextureSlot.Occlusion: p.OcclusionTexture = tex; break;
            case TextureSlot.Emissive: p.EmissiveTexture = tex; break;
        }
    }

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

        var oldTex = GetTextureBySlot(_drawList[0], slot);
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
                SetTextureBySlot(primitive, slot, newTex);
        }
    }

    /// <summary>Writes material-parameter overrides into the N-buffered Material UBO of every primitive.</summary>
    internal void SyncMaterialParams(float? metallic, float? roughness, Vector4? emissive)
    {
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0) return;

        int n = Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var mp = ReadStruct<MaterialParams>(primitive.MaterialBuffers[i]);
                if (metallic.HasValue) mp.MetallicFactor = metallic.Value;
                if (roughness.HasValue) mp.RoughnessFactor = roughness.Value;
                if (emissive.HasValue) mp.EmissiveFactor = emissive.Value;
                WriteStruct(primitive.MaterialBuffers[i], mp);
            }
        }
    }

    // ============================================================
    // Instance side: derived hooks
    // ============================================================

    /// <summary>Implemented by derived classes to append all PrimitiveData objects that should be drawn in the current frame into result.</summary>
    protected abstract void CollectPrimitives(List<PrimitiveData> result);

    /// <summary>
    /// Extra hook that runs before Draw.
    /// MTLModel overrides it to upload bone matrices into the current-frame bone UBO.
    /// The default implementation is empty because MTLMesh3D and other non-skinned primitive groups need no extra work.
    /// </summary>
    protected virtual void OnBeforeDraw() { }

    // ============================================================
    // Unified highlighting: highlight boxes with separate face and edge primitives, lazily built with no extra PSO families, using Transparent for faces and Opaque for edges
    // ============================================================

    /// <summary>
    /// Unified highlighting:
    /// one highlight box consists of two PrimitiveData objects, one for faces and one for edges.
    /// There are two geometric flavors:
    /// Bounds uses a unit cube in [-0.5, 0.5]^3, with world matrix Scale(Extents × 2) × Translate(Center).
    /// Wireframe uses shell faces and edge strips fitted to the surface in model-local space during phase 3.
    /// Faces are semi-transparent BLEND geometry rendered with the Transparent PSO,
    /// while edges are solid thin strips rendered with the Opaque PSO and depth writes enabled.
    /// PrevWorld is a CPU shadow copy because N-buffer UBOs must never be read back,
    /// and it feeds TAA and motion-vector velocity history.
    /// This is structurally identical to DX and VK HighlightBox.
    /// </summary>
    protected sealed class HighlightBox
    {
        public PrimitiveData Face;
        public PrimitiveData Edges;

        /// <summary>Face alpha for the current frame, coming from SurfaceColor.W. Faces are drawn only when this is greater than zero, and zero automatically means edge-only rendering. The write hook records it every frame.</summary>
        public float FaceAlpha;

        /// <summary>Previous-frame world matrix for the box, stored as a CPU shadow copy. On the first frame it is Identity and therefore acts as the zero-velocity sentinel.</summary>
        public Matrix4x4 PrevWorld = Matrix4x4.Identity;

        /// <summary>Host node of the source primitive for shell-based highlighting.
        /// Non-instanced per-primitive shell boxes record it so each frame can write node WorldTransform multiplied by the group world matrix,
        /// keeping node hierarchy scale, translation, and animation aligned with rendering.
        /// Null means identity, which applies to procedural Mesh3D primitives and instanced shared-template boxes.</summary>
        public GltfNodeBase? OwnerNode;

        /// <summary>Source primitive for shell-based highlighting on the non-instanced per-primitive path.
        /// It is used to synchronize morph weights:
        /// when source weights are written, the same weights are copied into the Material UBO of both shell primitives.
        /// Shell delta buffers are expanded to shell-vertex layout, while weights are shared with the source.</summary>
        public PrimitiveData? SourcePrimitive;
    }

    /// <summary>Unified highlighting: lazily builds the host Bounds box, meaning face plus edges. It is created once on the first enabled frame and remains resident afterward.</summary>
    protected HighlightBox CreateBoundsBox()
    {
        var box = new HighlightBox();
        box.Face = CreateBoxFacePrimitive();
        box.Edges = CreateBoxEdgesPrimitive();
        return box;
    }

    /// <summary>Unified highlighting: gets or creates the instance Bounds box for the given compressed writeIndex. The pool grows lazily and boxes remain resident until the group is released.</summary>
    protected HighlightBox AcquireBoundsBox(int index)
    {
        while (_instanceBoundsBoxes.Count <= index)
            _instanceBoundsBoxes.Add(CreateBoundsBox());
        return _instanceBoundsBoxes[index];
    }

    /// <summary>
    /// Unified highlighting for Bounds-box geometry on the face primitive:
    /// eight corners are baked into [-0.5, 0.5]^3 with corner indices encoded as x + y * 2 + z * 4,
    /// with 36 indices total.
    /// RenderMode = 0 for Unlit and AlphaMode = 2 for BLEND, so true transparency uses the Transparent PSO.
    /// The primitive is DoubleSided and binds White for all five textures with Use*Map = 0 because the Unlit path does not sample them.
    /// Geometry is reused from shared HighlightGeometry and matches DX and VK bit for bit.
    /// </summary>
    PrimitiveData CreateBoxFacePrimitive()
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
    PrimitiveData CreateBoxEdgesPrimitive()
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
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
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
    protected void WriteHighlightBox(HighlightBox box, Matrix4x4 world, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            PrevWorld = Matrix4x4.Transpose(box.PrevWorld),
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
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
    /// Bones and morph data are bound directly through SetVertexBuffer and do not require OnBeforeDraw.</summary>
    protected void DrawHighlightBox(HighlightBox box)
    {
        var face = box.Face;
        var edges = box.Edges;
        var enc = Device.GraphicsEncoder;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            enc.SetCullMode(MTLCullMode.Front);
            DrawPrimitive(enc, face);

            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            enc.SetCullMode(MTLCullMode.Back);
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

    /// <summary>
    /// Unified highlighting:
    /// lazily builds non-instanced wireframe highlight boxes at runtime.
    /// On the first frame that enables wireframe, it builds one shell box per primitive for all primitives collected through CollectPrimitives,
    /// preserving primitive order and using null placeholders for primitives without valid triangles.
    /// When fully disabled it uses no memory, and once built it stays resident without rebuild or release on runtime toggles.
    /// Each primitive gets its own box, so skinning parameters such as IsSkinned and BonePaletteStride are inherited from the source primitive,
    /// letting skinned models match animation precisely through the same bone transforms.
    /// Morph-target primitives also build shells:
    /// shell delta buffers are expanded to shell-vertex layout, with shell-to-source vertex mappings recorded during construction,
    /// and weights are synchronized from the source every frame through MTLModel.ApplyMorphTargetsIfNeeded,
    /// matching animation through the same vertex-shader morph path.
    /// edgeWidth comes from host Highlight.EdgeWidth as a model-scale ratio,
    /// and localSizeMax is the maximum local size of the host model, used as the scaling basis.
    /// Local thickness h is baked per primitive as edgeWidth × localSizeMax divided by node scale,
    /// using <see cref="HighlightGeometry.NodeScaleOf"/>.
    /// World-space edge width is therefore approximately edgeWidth multiplied by the model's maximum world size,
    /// keeping it consistent across assets.
    /// When it diverges from the host, release and rebuild.
    /// </summary>
    protected void EnsureWireframeHighlights(float edgeWidth, float localSizeMax)
    {
        if (_wireframeBoxes != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed:
            // release old shell geometry and rebuild it with the new width so the change takes effect immediately this frame.
            foreach (var box in _wireframeBoxes)
                DisposeHighlightBox(box);
            _wireframeBoxes = null;
        }
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;
        _wireframeBoxes = new List<HighlightBox?>(_drawList.Count);
        for (int i = 0; i < _drawList.Count; i++)
        {
            var source = _drawList[i];
            var box = source.Indices.Length >= 3 && source.Vertices.Count > 0
                ? CreateShellBox(source, HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode))
                : null;
            _wireframeBoxes.Add(box);
            // Record the node reference with the box.
            // Each frame uses its WorldTransform from the same rendering source,
            // and cloned primitives are collected through CollectPrimitives with the same group lifetime.
            if (box != null)
                box.OwnerNode = source.OwnerNode;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>
    /// Unified highlighting:
    /// lazily builds shared shell geometry for instanced templates.
    /// On the first frame that enables wireframe, source primitives are grouped into a rigid shell,
    /// where non-skinned primitives are merged and per-instance boxes share VB and IB,
    /// and a skinned shell,
    /// where primitives sharing the same Skin are merged, IsSkinned stays 1,
    /// BonePaletteStride is inherited from source material,
    /// and the per-instance bone-palette path keeps animation aligned precisely.
    /// Hybrid assets draw both shells, while purely skinned single-Skin assets output only the skinned shell.
    /// Morph-target primitives are skipped because morph weights are addressed by instance index
    /// and require shell-shaped delta buffers that merged geometry cannot express.
    /// This is documented behavior:
    /// for instanced models with morph targets, Wireframe highlighting covers only the remaining parts,
    /// while Bounds highlighting is unaffected.
    /// Multi-skin assets, where each node owns a separate Skin, skip the skinned shell
    /// because the merged template cannot express per-skin palette offsets.
    /// Phase 2 solves that only with a one-time CPU bake of per-vertex palette offsets during construction.
    /// When no usable primitives exist, both templates remain null and later frames retry.
    /// Shell primitives inherit HasPrevBones and HasPrevInstanceWorld from source material.
    /// On Metal, these prev flags are set once during Load and cold states are covered by zero-data sentinels,
    /// so lazily built shell templates become correct automatically by copying source MaterialParams
    /// with no runtime patch-up.
    /// edgeWidth comes from host Highlight.EdgeWidth as a model-scale ratio,
    /// and localSizeMax is the maximum local size of the template.
    /// Local thickness h is baked per primitive as edgeWidth × localSizeMax divided by node scale,
    /// using <see cref="HighlightGeometry.NodeScaleOf"/>,
    /// which keeps world-space edge width approximately equal to edgeWidth times the instance's maximum world size
    /// and therefore consistent across assets.
    /// When it diverges from the host, release and rebuild.
    /// </summary>
    protected void EnsureShellGeometry(float edgeWidth, float localSizeMax)
    {
        if (_shellGeometry != null || _skinnedShellGeometry != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed:
            // release all shared templates and instance shell-box pools, which share template geometry,
            // and rebuild them with the new width so the change takes effect immediately this frame.
            DisposeShellTemplatesAndPools();
        }
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        var rigidFaceVerts = new List<Vertex>();
        var rigidFaceIndices = new List<uint>();
        var rigidEdgeVerts = new List<Vertex>();
        var rigidEdgeIndices = new List<uint>();
        var skinFaceVerts = new List<Vertex>();
        var skinFaceIndices = new List<uint>();
        var skinEdgeVerts = new List<Vertex>();
        var skinEdgeIndices = new List<uint>();
        MaterialParams rigidParams = default;
        MaterialParams skinParams = default;
        bool anyRigid = false;
        bool anySkinned = false;
        bool multiSkin = false;
        GLTFSkin? sharedSkin = null;

        for (int i = 0; i < _drawList.Count; i++)
        {
            var source = _drawList[i];
            if (source.Indices.Length < 3 || source.Vertices.Count == 0)
                continue;
            if (source.MaterialParams.HasMorphTargets != 0)
                continue; // Skip morph-target primitives because merged instancing templates cannot represent per-primitive morph sets. See EnsureShellGeometry documentation.
            float h = HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode);
            if (source.MaterialParams.IsSkinned != 0)
            {
                if (multiSkin)
                    continue; // Multi-skin has already been detected, so skip the skinned shell entirely as documented for phase 1.
                // Primitives sharing the same Skin map through skinMap to the same cloned reference,
                // so ReferenceEquals is reliable here.
                var skin = source.OwnerNode?.Skin;
                if (skin == null)
                    continue; // Skinned flag is set but Skin data is missing, so skip defensively.
                if (sharedSkin == null)
                {
                    sharedSkin = skin;
                }
                else if (!ReferenceEquals(sharedSkin, skin))
                {
                    // Multi-skin asset:
                    // discard already accumulated skinned data and skip the skinned shell entirely.
                    // See the documentation and planning risk 1.
                    multiSkin = true;
                    anySkinned = false;
                    skinFaceVerts.Clear();
                    skinFaceIndices.Clear();
                    skinEdgeVerts.Clear();
                    skinEdgeIndices.Clear();
                    continue;
                }
                if (!anySkinned)
                    skinParams = source.MaterialParams;
                HighlightGeometry.AppendShellFace(skinFaceVerts, skinFaceIndices, source.Vertices, source.Indices, h);
                HighlightGeometry.AppendShellEdges(skinEdgeVerts, skinEdgeIndices, source.Vertices, source.Indices, h);
                anySkinned = true;
            }
            else
            {
                if (!anyRigid)
                    rigidParams = source.MaterialParams;
                HighlightGeometry.AppendShellFace(rigidFaceVerts, rigidFaceIndices, source.Vertices, source.Indices, h);
                HighlightGeometry.AppendShellEdges(rigidEdgeVerts, rigidEdgeIndices, source.Vertices, source.Indices, h);
                anyRigid = true;
            }
        }
        if (!anyRigid && !anySkinned)
            return; // No usable primitives: keep templates null and retry on later frames.

        if (anyRigid)
        {
            var shell = new HighlightBox();
            shell.Face = InitShellPrimitive(rigidFaceVerts, rigidFaceIndices.ToArray(), rigidParams, isTransparent: true);
            shell.Edges = InitShellPrimitive(rigidEdgeVerts, rigidEdgeIndices.ToArray(), rigidParams, isTransparent: false);
            _shellGeometry = shell;
        }
        if (anySkinned)
        {
            var shell = new HighlightBox();
            shell.Face = InitShellPrimitive(skinFaceVerts, skinFaceIndices.ToArray(), skinParams, isTransparent: true);
            shell.Edges = InitShellPrimitive(skinEdgeVerts, skinEdgeIndices.ToArray(), skinParams, isTransparent: false);
            _skinnedShellGeometry = shell;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>Unified highlighting: builds a Wireframe shell highlight box for one source primitive, consisting of shell faces and shell edges as two primitives.
    /// Vertices are copied field by field from the source, including bone indices and weights,
    /// so skinned models stay aligned through the same vertex-shader skinning path.
    /// Material parameters are copied from the source primitive and then forced to Unlit plus either transparent or solid behavior,
    /// while IsSkinned and BonePaletteStride are inherited as well.
    /// For morph-target source primitives, shell delta buffers are expanded to shell-vertex layout with per-vertex source indices recorded during construction,
    /// and weights are shared with the source and synchronized every frame by MTLModel,
    /// keeping morph animation aligned through the same vertex-shader morph path.
    /// edgeWidth is the already baked local thickness h,
    /// equal to Highlight.EdgeWidth multiplied by maximum local model size and divided by node scale as described by <see cref="HighlightGeometry.NodeScaleOf"/>.
    /// Edge strips therefore use a full width of 2 × h and shell faces expand outward by the same thickness.</summary>
    protected HighlightBox CreateShellBox(PrimitiveData source, float edgeWidth)
    {
        var faceVerts = new List<Vertex>(source.Vertices.Count);
        var faceIndices = new List<uint>(source.Indices.Length);
        var faceSrcIdx = new List<int>(source.Vertices.Count);
        HighlightGeometry.AppendShellFace(faceVerts, faceIndices, source.Vertices, source.Indices, edgeWidth, faceSrcIdx);

        var edgeVerts = new List<Vertex>(source.Indices.Length * 2);
        var edgeIndices = new List<uint>(source.Indices.Length * 2);
        var edgeSrcIdx = new List<int>(source.Indices.Length * 2);
        HighlightGeometry.AppendShellEdges(edgeVerts, edgeIndices, source.Vertices, source.Indices, edgeWidth, edgeSrcIdx);

        var box = new HighlightBox();
        box.Face = InitShellPrimitive(faceVerts, faceIndices.ToArray(), source.MaterialParams, isTransparent: true);
        box.Edges = InitShellPrimitive(edgeVerts, edgeIndices.ToArray(), source.MaterialParams, isTransparent: false);
        box.SourcePrimitive = source;
        if (source.MaterialParams.HasMorphTargets != 0 && source.MorphTargets != null && source.MorphTargets.Count > 0)
        {
            AttachShellMorph(box.Face, source, faceSrcIdx);
            AttachShellMorph(box.Edges, source, edgeSrcIdx);
        }
        return box;
    }

    /// <summary>Unified highlighting: attaches the morph path to a shell primitive by expanding source deltas to shell-vertex layout.
    /// The shell-to-source vertex index mapping is recorded during shell construction.
    /// It sets HasMorphTargets, MorphTargetCount, and MorphVertexCount, where the latter equals the shell vertex count,
    /// and writes them back to Material UBOs for all frames.
    /// Weights are shared with the source and synchronized every frame by MTLModel.ApplyMorphTargetsIfNeeded.
    /// Metal has no DescriptorSet objects, so the morph-delta buffer is bound per draw through the morphBuffer parameter of DrawPrimitive,
    /// and after creation no existing bindings need to be rewritten.</summary>
    void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            WriteStruct(shell.MaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
    }

    /// <summary>Unified highlighting: gets or creates the instance Wireframe shell box for the given compressed writeIndex.
    /// The pool grows lazily and uses shared VB and IB from template _shellGeometry,
    /// while the box's own VertexBuffer and IndexBuffer stay at default values.
    /// PrimitiveData.Dispose is null-safe, so no double release occurs.
    /// Only Matrix and Material UBOs are created locally,
    /// and matrices stay identity because the instancing shader ignores b0 world and resolves per-instance matrices by instance-stream slot.
    /// When the template is not ready because there are no non-skinned and non-morph primitives, this returns null and callers skip drawing.
    /// Empty slots that were created before the template became ready are automatically filled the first time they are used afterward,
    /// which also covers the path where runtime asset replacement makes shell-template retries succeed.</summary>
    protected HighlightBox? AcquireShellBox(int index)
    {
        if (_shellGeometry == null)
            return null;
        while (_instanceShellBoxes.Count <= index)
            _instanceShellBoxes.Add(CreateInstanceShellBox());
        var box = _instanceShellBoxes[index];
        if (box == null)
        {
            // Build a previously empty slot now that the template is ready.
            // This handles indices that were reserved while the template was unavailable.
            box = CreateInstanceShellBox();
            _instanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: builds the per-instance shell box for the shared template. See AcquireShellBox documentation.</summary>
    HighlightBox? CreateInstanceShellBox()
    {
        if (_shellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_shellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_shellGeometry.Edges);
        return box;
    }

    /// <summary>Unified highlighting: gets or creates the instance skinned Wireframe shell box for the given compressed writeIndex.
    /// It is structurally identical to AcquireShellBox but shares the VB and IB of _skinnedShellGeometry and uses the per-instance bone-palette path for skinned shells.
    /// When the template is not ready because no single-Skin skinned primitives exist or the asset has multiple skins,
    /// this returns null and callers skip drawing.</summary>
    protected HighlightBox? AcquireSkinnedShellBox(int index)
    {
        if (_skinnedShellGeometry == null)
            return null;
        while (_skinnedInstanceShellBoxes.Count <= index)
            _skinnedInstanceShellBoxes.Add(CreateSkinnedInstanceShellBox());
        var box = _skinnedInstanceShellBoxes[index];
        if (box == null)
        {
            // Build a previously empty slot now that the template is ready.
            // This handles indices that were reserved while the template was unavailable.
            box = CreateSkinnedInstanceShellBox();
            _skinnedInstanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: builds the per-instance shell box for the shared skinned template. See AcquireSkinnedShellBox documentation.</summary>
    HighlightBox? CreateSkinnedInstanceShellBox()
    {
        if (_skinnedShellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_skinnedShellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_skinnedShellGeometry.Edges);
        return box;
    }

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
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            // Contract clause 8(d) of 2-3:
            // PrevWorld in b0 stays all zeros on the instanced path because history comes from the double-buffered instance stream.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
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
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
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
    /// HasPrevBones and HasPrevInstanceWorld are inherited from source material through the shell template.
    /// On Metal, prev flags are set once during Load and cold states are covered by zero-data sentinels,
    /// so no runtime patch-up is needed.</summary>
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
            enc.SetCullMode(MTLCullMode.Front);
            Pipeline.DrawPrimitive(enc, face, geo.Face.VertexBuffer, geo.Face.IndexBuffer,
                face.MatrixBuffers[fi], face.MaterialBuffers[fi],
                LightConstantBuffers[fi], IdentityBoneBuffers[fi], face.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], instanceBones,
                MTLPrimitiveType.Triangle, (nuint)face.Indices.Length,
                face.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                instanceBuffer, instanceOffset, 1, boneOffset, prevInstanceBuffer, prevInstanceBones, boneOffset);

            Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
            enc.SetCullMode(MTLCullMode.Back);
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
    PrimitiveData InitShellPrimitive(List<Vertex> vertices, uint[] indices, MaterialParams sourceParams, bool isTransparent)
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

    /// <summary>Unified highlighting: releases shared shell templates, both rigid and skinned, together with the two instance shell-box pools that share their geometry.
    /// This is used both by edge-width-triggered rebuilds and by DisposeHighlights.</summary>
    void DisposeShellTemplatesAndPools()
    {
        if (_shellGeometry != null)
        {
            DisposeHighlightBox(_shellGeometry);
            _shellGeometry = null;
        }
        if (_skinnedShellGeometry != null)
        {
            DisposeHighlightBox(_skinnedShellGeometry);
            _skinnedShellGeometry = null;
        }
        foreach (var box in _instanceShellBoxes)
            DisposeHighlightBox(box);
        _instanceShellBoxes.Clear();
        foreach (var box in _skinnedInstanceShellBoxes)
            DisposeHighlightBox(box);
        _skinnedInstanceShellBoxes.Clear();
        _shellBoxDrawList.Clear();
    }

    /// <summary>Unified highlighting: releases all highlight GPU resources,
    /// including the host Bounds box, the instance Bounds-box pool,
    /// per-primitive Wireframe shell boxes, shared shell templates, and Wireframe instance-box pools.
    /// Box primitives own the VB, IB, and UBO resources they create,
    /// and PrimitiveData.Dispose reclaims all of them.</summary>
    protected void DisposeHighlights()
    {
        DisposeHighlightBox(_boundsBox);
        _boundsBox = null!;
        foreach (var box in _instanceBoundsBoxes)
            DisposeHighlightBox(box);
        _instanceBoundsBoxes.Clear();
        _boundsBoxDrawList.Clear();
        _boundsActive = false;

        if (_wireframeBoxes != null)
        {
            foreach (var box in _wireframeBoxes)
                DisposeHighlightBox(box);
            _wireframeBoxes = null;
        }
        DisposeShellTemplatesAndPools();
        _wireframeEnabled = false;
        _builtShellEdgeWidth = 0f;
    }

    static void DisposeHighlightBox(HighlightBox? box)
    {
        if (box == null)
            return;
        box.Face?.Dispose();
        box.Edges?.Dispose();
    }

    public abstract void Dispose();

    // ============================================================
    // Instance side: Draw with three-bucket grouping
    // ============================================================

    public void Draw()
    {
        if (!_transformInitialized)
            return;

        OnBeforeDraw();

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        var enc = Device.GraphicsEncoder;

        // Group into Opaque, Fade, and Transparent buckets.
        // When overall alpha is below 1, non-BLEND materials must use the Fade PSO,
        // which blends while still writing depth, instead of the Transparent PSO.
        // Depth writes stop complex overlapping meshes from over-blending and exposing interior geometry.
        bool forceFadeByAlpha = _currentAlpha < 1.0f;

        // 1. Opaque with depth writes.
        bool pipelineSet = false;
        bool currentDoubleSided = false;
        bool currentDepthWrite = true;
        if (!forceFadeByAlpha)
        {
            for (int i = 0; i < _drawList.Count; i++)
            {
                var p = _drawList[i];
                if (p.IsTransparent) continue;
                // Contract clause 7 of 2-2:
                // AoExempt primitives switch to OpaqueNoDepthState.
                // Only DSS changes, while the PSO stays the same.
                bool depthWrite = !p.AoExempt;
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite) { Pipeline.SetPipeline(enc, PipelineMode.Opaque, p.DoubleSided, depthWrite); pipelineSet = true; currentDoubleSided = p.DoubleSided; currentDepthWrite = depthWrite; }
                DrawPrimitive(enc, p);
            }
        }

        // 2. Fade, enabled only when _currentAlpha is below 1.
        // Non-BLEND materials use the Fade PSO with blending plus depth writes.
        if (forceFadeByAlpha)
        {
            pipelineSet = false;
            currentDoubleSided = false;
            currentDepthWrite = true;
            for (int i = 0; i < _drawList.Count; i++)
            {
                var p = _drawList[i];
                if (p.IsTransparent) continue;
                bool depthWrite = !p.AoExempt;
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite) { Pipeline.SetPipeline(enc, PipelineMode.Fade, p.DoubleSided, depthWrite); pipelineSet = true; currentDoubleSided = p.DoubleSided; currentDepthWrite = depthWrite; }
                DrawPrimitive(enc, p);
            }
        }

        // 3. Transparent for true BLEND materials, with no depth writes.
        pipelineSet = false;
        currentDoubleSided = false;
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (!p.IsTransparent) continue;
            if (p.DoubleSided)
            {
                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Front);
                DrawPrimitive(enc, p);

                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Back);
                pipelineSet = true;
                currentDoubleSided = false;
                DrawPrimitive(enc, p);
                continue;
            }

            if (!pipelineSet || currentDoubleSided != p.DoubleSided) { Pipeline.SetPipeline(enc, PipelineMode.Transparent, false); pipelineSet = true; currentDoubleSided = false; }
            DrawPrimitive(enc, p);
        }

        // Unified highlighting uses lazily built face and edge primitive groups,
        // independent of the CollectPrimitives and SyncAlpha paths.
        // Faces use the two-pass transparent path, edges use Opaque with depth writes,
        // and the two-color highlight finishes after all group surfaces have been drawn.
        // This covers the host Bounds box, instance boxes, and per-primitive Wireframe shell boxes.
        // When SurfaceColor.w is zero, rendering becomes edge-only because FaceAlpha gates faces off.
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

    /// <summary>
    /// Determines whether shadow primitives inside the group can share one material buffer at slot 4 and one morph buffer at slot 5.
    /// This is structurally identical to DXPrimitiveGroup.CanShareShadowMaterial,
    /// and the criteria come from enumerating the actual fields read by the same uber shader in the shadow VS,
    /// with MSL and HLSL reading the same set of flags.
    ///
    /// The shadow VS reads only five MaterialParams fields:
    /// renderMode, isInstanced, isSkinned, bonePaletteStride, and hasMorphTargets.
    /// Material color, alphaMode, alphaCutoff, and texture-enable flags are all fragment-shader concerns,
    /// and this pass has no fragment shader, so none of them are read.
    /// When those five fields match across the group,
    /// the buffer bound by the first primitive is byte-for-byte equivalent for all remaining primitives,
    /// so later primitives can skip two SetVertexBuffer calls.
    /// When hasMorphTargets is zero, morphTargetCount, morphVertexCount, and morphWeights become dead code,
    /// and the morph buffer is not read either, so both bindings can be skipped together.
    ///
    /// Primitives with morph data are always treated as non-shareable.
    /// The check is based on the fixed MorphTargets loaded at load time,
    /// not on the per-frame MaterialParams.HasMorphTargets value,
    /// so the result does not depend on whether shadow drawing happens before or after morph-weight updates.
    /// isSkinned is a per-node property rather than a model-level property, as seen in MTLModel skinning-material synchronization,
    /// so it must be compared per primitive.
    ///
    /// Transparent primitives are excluded from the test because the shadow pass skips them anyway,
    /// and letting them invalidate sharing for the whole group would waste work.
    /// The filter condition used by the test loop must match the draw loop exactly,
    /// or the two paths would operate on different primitive sets.
    /// </summary>
    protected static bool CanShareShadowMaterial(List<PrimitiveData> primitives)
    {
        PrimitiveData? first = null;
        for (int i = 0; i < primitives.Count; i++)
        {
            var p = primitives[i];
            if (p.IsTransparent)
                continue;

            if (p.MorphTargets != null && p.MorphTargets.Count > 0)
                return false;

            if (first == null)
            {
                first = p;
                continue;
            }

            if (p.MaterialParams.RenderMode != first.MaterialParams.RenderMode
                || p.MaterialParams.IsInstanced != first.MaterialParams.IsInstanced
                || p.MaterialParams.IsSkinned != first.MaterialParams.IsSkinned
                || p.MaterialParams.BonePaletteStride != first.MaterialParams.BonePaletteStride)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Shadow-pass rendering for 1-5, called by RenderShadowPass once per atlas slot inside a quadrant.
    /// It does not split into three buckets and does not switch PSOs because the shadow PSO has already been bound by RenderShadowPass.
    /// It skips IsTransparent primitives because true BLEND materials do not cast shadows under contract clause 7,
    /// and per-quadrant light-space culling has already been performed by shared Mesh3DBase.DrawShadow.
    ///
    /// The primitive list is cached once per pass under <see cref="Season.Rendering.CascadedShadow.Epoch"/>.
    /// RenderShadowPass calls this method repeatedly for atlas quadrants, meaning three cascades plus spotlight,
    /// while only viewport and light-space ViewProj at buffer(8) change between quadrants.
    /// The primitive set itself is constant, so CollectPrimitives runs only once per pass.
    /// MTLModel uses recursive glTF-node traversal plus AddRange, and MTLMesh3D uses AddRange;
    /// both are side-effect free and safe to replay.
    /// A separate field, _shadowDrawList, isolates shadow drawing from the main-pass _drawList,
    /// preventing Draw and DrawShadow from overwriting each other within the same frame.
    /// This mirrors DX DrawShadow one to one.
    /// </summary>
    public virtual void DrawShadow()
    {
        if (!_transformInitialized)
            return;

        if (_shadowDrawListEpoch != Season.Rendering.CascadedShadow.Epoch)
        {
            _shadowDrawList.Clear();
            CollectPrimitives(_shadowDrawList);
            _shadowDrawListEpoch = Season.Rendering.CascadedShadow.Epoch;
        }

        if (_shadowDrawList.Count == 0)
            return;

        OnBeforeDraw();

        // When material and morph state are identical within the group,
        // bind them only for the first primitive and let later primitives reuse the encoder state already in effect.
        // Otherwise fall back to per-primitive binding.
        // The test is pure field comparison with no virtual calls,
        // so it is intentionally recomputed every call instead of cached, leaving no invalidation window.
        bool shareMaterial = CanShareShadowMaterial(_shadowDrawList);
        bool materialBound = false;

        var enc = Device.GraphicsEncoder;
        for (int i = 0; i < _shadowDrawList.Count; i++)
        {
            var p = _shadowDrawList[i];
            if (p.IsTransparent) continue;
            DrawShadowPrimitive(enc, p, bindMaterial: !shareMaterial || !materialBound);
            materialBound = true;
        }
    }

    void DrawShadowPrimitive(IMTLRenderCommandEncoder enc, PrimitiveData p, bool bindMaterial)
    {
        int fi = Device.FrameIndex;
        Pipeline.DrawShadowPrimitive(enc, p, p.VertexBuffer, p.IndexBuffer,
            p.MatrixBuffers[fi], p.MaterialBuffers[fi], BoneMatrixBuffers[fi],
            p.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
            (nuint)p.Indices.Length, p.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
            null, 0, 1, 0, bindMaterial);
    }

    void DrawPrimitive(IMTLRenderCommandEncoder enc, PrimitiveData p)
    {
        int fi = Device.FrameIndex;
        var bones = BoneMatrixBuffers;
        var fallback = Device.White;
        var morphBuffer = p.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi];

        // VS buffer slots:
        // 0 = vertex stream, 1 = Matrices(b0), 2 = Instance(buff2), 3 = BoneMatrices(b3), 4 = MaterialParams, 5 = Morph, 6 = InstanceBones.
        // Contract clause 8(b) of 2-3 adds:
        // 9 = previous instance stream, using an identity placeholder for non-instanced paths,
        // and 10 = previous bone palette from frame-ring slot [fi - 1].
        enc.SetVertexBuffer(p.VertexBuffer, 0, 0);
        enc.SetVertexBuffer(p.MatrixBuffers[fi], 0, 1);
        enc.SetVertexBuffer(Pipeline.IdentityInstanceBuffer, 0, 2);
        enc.SetVertexBuffer(bones[fi], 0, 3);
        enc.SetVertexBuffer(p.MaterialBuffers[fi], 0, 4);
        enc.SetVertexBuffer(morphBuffer, 0, 5);
        enc.SetVertexBuffer(IdentityInstanceBoneBuffers[fi], 0, 6);
        enc.SetVertexBuffer(Pipeline.IdentityInstanceBuffer, 0, 9);
        enc.SetVertexBuffer(bones[PrevFrameIndex], 0, 10);

        // FS buffer slots: 1=SceneLights(b1), 2=MaterialParams(b2)
        enc.SetFragmentBuffer(LightConstantBuffers[fi], 0, 1);
        enc.SetFragmentBuffer(p.MaterialBuffers[fi], 0, 2);

        // FS texture slots: 0=BaseColor, 1=Normal, 2=MR, 3=AO, 4=Emissive
        enc.SetFragmentTexture((p.BaseColorTexture ?? fallback).Image, 0);
        enc.SetFragmentTexture((p.NormalTexture ?? fallback).Image, 1);
        enc.SetFragmentTexture((p.MetallicRoughnessTexture ?? fallback).Image, 2);
        enc.SetFragmentTexture((p.OcclusionTexture ?? fallback).Image, 3);
        enc.SetFragmentTexture((p.EmissiveTexture ?? fallback).Image, 4);

        // Indexed draw, where the index type matches PrimitiveData.Indices.
        enc.DrawIndexedPrimitives(
            primitiveType: MTLPrimitiveType.Triangle,
            indexCount: (nuint)p.Indices.Length,
            indexType: p.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
            indexBuffer: p.IndexBuffer,
            indexBufferOffset: 0);
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
    /// OnBeforeDraw is applied per primitive to provide b3 bone UBO binding,
    /// and drawing proceeds primitive by primitive.
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
            OnBeforeDraw();
            DrawPrimitive(enc, p);
        }
    }
}
