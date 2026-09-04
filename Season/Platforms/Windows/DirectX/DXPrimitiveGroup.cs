// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Silk.NET.Direct3D12;

namespace Season.Platforms.Windows.DirectX;

/// <summary>
/// Common base class for primitive groups rendered as a PrimitiveData list on
/// the Pbr3D path.
/// Derived classes (DXModel / DXMesh3D) mainly differ in:
///   - geometry / material source (glTF node tree vs Mesh3D.Surfaces)
///   - whether bones exist (DXModel overrides OnBeforeDraw to bind b3 BoneMatrices)
///   - how primitives are collected (derived CollectPrimitives implementation)
///
/// Shared responsibilities:
///   - static: camera, shared lighting CB
///     (dedicated to the Pbr3D path, also reused by DXSpriteQuad)
///   - per-instance: Matrix/Material CB creation, SyncAlpha, three-bucket Draw,
///     and DrawPrimitive
/// </summary>
internal unsafe abstract class DXPrimitiveGroup : IDisposable
{
    // === Globally shared: camera for all Pbr3D primitives ===
    internal static Season.Basic.Camera Camera;

    // === Globally shared: lighting CB (N-buffered) ===
    // The Pbr3D path and the DXSpriteQuad/Sprite family all read the same CB
    // from here to avoid duplicate copies.
    internal static ID3D12Resource*[] lightConstantBuffers;
    static byte*[] mappedLightConstantBuffers;

    // === Common per-instance state ===
    internal string Name;

    /// <summary>Overall alpha last written into the material buffer, used to
    /// drive the PSO three-bucket grouping.</summary>
    protected float _currentAlpha = 1.0f;

    /// <summary>Color multiplier last written into the material buffer, used
    /// for Mesh3D.ColorTint sync and rewritten only on change.</summary>
    protected Vector4 _currentColorTint = Vector4.One;

    /// <summary>Unified highlighting: whether wireframe highlighting
    /// (surface-fitted shell faces + edge strips) is enabled. Maintained by
    /// derived Update methods. Geometry is built lazily on the first enabled
    /// frame and stays resident afterward. It can be toggled at runtime, and
    /// has zero memory and zero draw cost when fully disabled.</summary>
    protected bool _wireframeEnabled;

    /// <summary>Whether the first Update has completed. When false, Draw is
    /// skipped to avoid rendering with the identity matrix.</summary>
    protected bool _transformInitialized;

    /// <summary>Reusable draw list to avoid per-frame List allocations and GC.</summary>
    private readonly List<PrimitiveData> _drawList = new List<PrimitiveData>(64);
    private readonly List<PrimitiveData> _singleSidedTransparentList = new List<PrimitiveData>(32);
    private readonly List<PrimitiveData> _doubleSidedTransparentList = new List<PrimitiveData>(32);

    /// <summary>1-5: projected primitive list for the shadow pass. It is
    /// separate from the main-pass _drawList, so the two pipelines never
    /// overwrite each other. The same list is replayed per atlas slot across
    /// the four atlas quadrants and invalidated by
    /// <see cref="CascadedShadow.Epoch"/>. See DrawShadow.</summary>
    private readonly List<PrimitiveData> _shadowDrawList = new List<PrimitiveData>(64);

    /// <summary>Epoch already collected into _shadowDrawList
    /// (int.MinValue means never collected).</summary>
    private int _shadowDrawListEpoch = int.MinValue;

    // === Unified highlighting (dual-color primitive group: faces + edges,
    // lazily built; no new PSO, faces use Transparent and edges use Opaque) ===
    // Highlight primitives live outside CollectPrimitives, so SyncAlpha and
    // SyncColorTint never touch their material CBs. Highlight alpha
    // (SurfaceColor.W pulsing) is fully decoupled from the model's overall
    // alpha, and DrawShadow naturally excludes them as well.
    // Two styles exist: Bounds = world-space AABB box (faces + edges);
    // Wireframe = surface-fitted shell faces + edge strips in model-local space,
    // with the world matrix coming from the model / instance matrix. Vertices
    // carry bone indices and weights so the same VS skinning path keeps them
    // tightly aligned to animation.
    // Both styles share the same color pair: faces use SurfaceColor
    // (w=0 makes Wireframe edge-only and skips face drawing), edges use EdgeColor.
    /// <summary>Whether the host Bounds box is enabled for this frame in the
    /// non-instanced path. Written during Update and used as a zero-cost gate
    /// during Draw.</summary>
    protected bool _boundsActive;

    /// <summary>Host Bounds box for the non-instanced path. Built lazily on the
    /// first enabled frame and kept resident afterward.</summary>
    protected HighlightBox _boundsBox = null!;

    /// <summary>Per-instance Bounds-box pool indexed by compacted writeIndex.
    /// Grows lazily and stays resident until the group is released.</summary>
    protected readonly List<HighlightBox> _instanceBoundsBoxes = new();

    /// <summary>Compacted instance indices whose Bounds boxes are enabled this
    /// frame. Rebuilt every Update and drawn box by box during Draw.</summary>
    protected readonly List<int> _boundsBoxDrawList = new();

    /// <summary>Host Wireframe highlight boxes for the non-instanced path
    /// (surface-fitted shell faces + edge strips, one group per primitive,
    /// built lazily in CollectPrimitives order, with null placeholders for
    /// primitives that have no valid triangles). Built lazily on the first
    /// enabled frame and kept resident afterward.</summary>
    protected List<HighlightBox>? _wireframeBoxes;

    /// <summary>Shared shell geometry for instanced templates, combining all
    /// non-skinned primitives. Per-instance boxes share its VB/IB and set
    /// OwnsGeometry=false. Built once lazily on the first enabled frame and
    /// kept resident afterward.</summary>
    protected HighlightBox? _shellGeometry;

    /// <summary>Shared skinned shell geometry for instanced templates,
    /// combining all skinned primitives that share the same Skin.
    /// With IsSkinned=1 it uses the per-instance bone-palette path and matches
    /// animation through the same VS skinning path as the main pass.
    /// Built once lazily on the first enabled frame and kept resident
    /// afterward. Multi-skin assets, where each node has its own Skin, are
    /// intentionally skipped in phase 1, so this stays null and is retried on
    /// later frames.</summary>
    protected HighlightBox? _skinnedShellGeometry;

    /// <summary>Per-instance Wireframe highlight-box pool using shared
    /// template shell geometry. Matrices are read through the instance-stream
    /// writeIndex slot and drawn per instance.</summary>
    protected readonly List<HighlightBox> _instanceShellBoxes = new();

    /// <summary>Per-instance Wireframe highlight-box pool using shared skinned
    /// template shell geometry. It shares the same index space as
    /// _instanceShellBoxes; for mixed assets the same writeIndex can own one box
    /// in each pool and draw both.</summary>
    protected readonly List<HighlightBox> _skinnedInstanceShellBoxes = new();

    /// <summary>Compacted instance indices whose Wireframe highlighting is
    /// enabled this frame. Rebuilt every Update and drawn box by box during
    /// Draw.</summary>
    protected readonly List<int> _shellBoxDrawList = new();

    /// <summary>Edge-strip width of the currently cached shell geometry
    /// (per-primitive boxes or shared instanced templates). Recorded at build
    /// time, and rebuilt when it no longer matches the host HighlightEdgeWidth
    /// so runtime width changes take effect immediately.</summary>
    protected float _builtShellEdgeWidth;

    /// <summary>Whether the screen-space outline mask is enabled. Written
    /// during Update and collected by Graphics in a separate pass.</summary>
    protected bool _outline2DActive;

    /// <summary>Outline2D color for the current object. In the first version,
    /// a single shared color is used per frame and resolved by upper layers.</summary>
    protected Vector4 _outline2DColor;

    /// <summary>Outline2D width for the current object, in pixels.</summary>
    protected float _outline2DWidth;

    // ============================================================
    // Static: lighting-CB lifetime and global camera / lighting updates
    // ============================================================

    public static void InitLights()
    {
        int n = (int)Device.frameCount;
        lightConstantBuffers = new ID3D12Resource*[n];
        mappedLightConstantBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            lightConstantBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<SceneLightParams>(),
                out mappedLightConstantBuffers[i]);
        }

        var defaultLight = new SceneLightParams
        {
            CameraPos = new Vector4(0, 0, -1, 1),
            Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Params0 = new Vector4(0, Device.HdrExposure, 0, 0),
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(mappedLightConstantBuffers[i], defaultLight);
    }

    public static void InitLightsDispose()
    {
        if (lightConstantBuffers != null)
        {
            for (int i = 0; i < lightConstantBuffers.Length; i++)
            {
                if (lightConstantBuffers[i] != null)
                {
                    lightConstantBuffers[i]->Unmap(0, null);
                    lightConstantBuffers[i]->Release();
                    lightConstantBuffers[i] = null;
                }
            }
            lightConstantBuffers = null;
            mappedLightConstantBuffers = null;
        }
    }

    /// <summary>Writes the lighting CB for the current frame using the 1-2
    /// SceneLightParams layout. Params0.Y carries HDR exposure
    /// (shader-side params0.y, used for inverse-ACES text compensation),
    /// VelocityParams carries this frame's sub-pixel jitter and inverse screen
    /// size (2-3 contract rule 6), and EnvParams + IrradianceSH9 carry 1-7
    /// environment lighting (contract rule 4). All three are injected once per
    /// frame here; writes from the app layer are ignored.</summary>
    public static void SetLighting(SceneLightParams lightParams)
    {
        int fi = (int)Device.FrameIndex;
        lightParams.Params0.Y = Device.HdrExposure;

        // 2-3 contract rule 6: xy = this frame's jitter in NDC, zw = inverse
        // screen size. The PS uses these to reconstruct NDC from SV_Position.
        // When MotionVectors are off, JitterNdc stays zero, and writing it is
        // harmless because shaders with VELOCITY_OUTPUT=0 do not read the field.
        var res = DeviceServices.BaseApp.DeviceResolution;
        var jitter = DeviceServices.BaseApp.Camera.JitterNdc;
        lightParams.VelocityParams = new Vector4(
            jitter.X, jitter.Y,
            res.X > 0 ? 1f / res.X : 0f,
            res.Y > 0 ? 1f / res.Y : 0f);

        // 1-7 contract rule 4: inject environment parameters and resolve this
        // frame's radiance cube once per frame so DrawPrimitive does not need
        // a per-draw lookup. When SceneEnvironment is null, EnvParams stays all
        // zero and the shader falls back per pixel to the 1-2 constant ambient.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        env?.Apply(ref lightParams);
        DXTextureCube.Active = env != null ? DXTextureCube.Find(env.RadianceName) : null;

        // 2-4 rule 10: inject DDGI GiParams0/1/2 once here. When not ready,
        // nothing is written and consumers fall back automatically.
        Season.Rendering.Effects.DdgiEffect.Apply(ref lightParams);

        // 2-5 Step B (b11): resolve sun / moon discs and starlight into
        // SkyParams0..3 once here. On the StaticCube tier, the path exits early,
        // all four fields stay zero, and the PS gate skyParams0.w > 0 remains
        // false with no stale data.
        Season.Rendering.SkyLighting.Apply(ref lightParams);

        // Resolve the irradiance atlas written this frame once per frame.
        // Compute 2D textures are registered in the Graphics instance's
        // DictionaryDXTexture under their full names, which is the same
        // dictionary used by CreateComputeTexture. The Device static dictionary
        // only contains file textures. This avoids per-draw lookups in
        // DrawPrimitive. When not ready or not found, set null so DrawPrimitive
        // falls back to White.
        if (Season.Rendering.Effects.DdgiEffect.Ready
            && Season.Basic.Graphics.Instance is Season.Platforms.Windows.Graphics winGraphics)
        {
            winGraphics.TryGetTexture(Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName, out Pipeline.DdgiAtlasActive);
            winGraphics.TryGetTexture(Season.Rendering.Effects.DdgiEffect.ActiveDepthName, out Pipeline.DdgiDepthActive);
        }
        else
        {
            Pipeline.DdgiAtlasActive = null;
            Pipeline.DdgiDepthActive = null;
        }

        // 2-5 Step C: resolve this frame's cloud-noise texture through the same
        // Graphics instance dictionary. It is baked only once in its lifetime,
        // but still resolved every frame so Active is cleared when
        // FrameSchedule.CloudNoiseTexture becomes null after Dispose or quality
        // downgrade. Otherwise a released texture handle could remain bound.
        // When null, DrawPrimitive falls back to White and the layer-count gate
        // keeps it out of shading.
        if (Season.Rendering.FrameSchedule.CloudNoiseTexture is string cloudNoiseName
            && Season.Basic.Graphics.Instance is Season.Platforms.Windows.Graphics cloudGraphics)
            cloudGraphics.TryGetTexture(cloudNoiseName, out Pipeline.CloudNoiseActive);
        else
            Pipeline.CloudNoiseActive = null;

        // 2-5 Step E: resolve this frame's aerial-perspective volume.
        // Do not use TryGetTexture here. Compute 3D textures are registered in
        // DXTexture3D's own static dictionary, separate from the 2D
        // DictionaryDXTexture (see 1-8), so only Find can be used.
        // This is also resolved every frame so Active is cleared when
        // FrameSchedule.AerialLutTexture becomes null after downgrade or Dispose.
        Pipeline.AerialLutActive = Season.Rendering.FrameSchedule.AerialLutTexture is string aerialName
            ? DXTexture3D.Find(aerialName)
            : null;

        Unsafe.Write(mappedLightConstantBuffers[fi], lightParams);
    }

    /// <summary>
    /// Called once per frame by the main loop: refreshes the camera
    /// view/projection and writes the lighting CB.
    /// The engine follows the glTF/OpenGL camera convention:
    /// left-handed space, with the camera standing on the +Z side and looking
    /// at the origin to see the model front face.
    /// </summary>
    public static void Update(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: matrix construction is centralized in the shared Camera3D layer.
        // It is change-gated, so static cameras rebuild nothing, while
        // FOV/near/far are still driven by BaseApp.Camera.
        // cameraPos/cameraTarget are forwarded from BaseApp.Camera.Position/Target
        // and kept in the signature for frame-loop compatibility.
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;

        // DPI compensation, matching the 2D rendering rule 1/CompositionScale.X:
        // multiply NDC xy by n and translate toward the upper-left corner.
        // This exactly reproduces the 2D "layout coordinates / scale" behavior,
        // meaning content scales around the screen's upper-left corner
        // (shrinking and shifting up-left).
        // Math: scaling by n around NDC upper-left (-1, +1) yields
        // x' = n*x + (n-1)*w, y' = n*y + (1-n)*w.
        // This only affects render matrices and does not pollute camera3D:
        // Frustum and shadow cascades still use the uncompensated matrices.
        // Multiplication order is fixed as Projection * DpiTransform under the
        // row-vector convention: project first, then apply the NDC affine transform.
        float n = 1f / DeviceServices.BaseApp.CompositionScale.X;
        var dpiTransform = Matrix4x4.CreateScale(n, n, 1f);
        dpiTransform.M41 = n - 1f;
        dpiTransform.M42 = 1f - n;

        if (RenderQuality.Current.MotionVectors)
        {
            // 2-3 contract rule 4: this is the only jitter injection point.
            // UpdateTemporal first snapshots the previous unjittered
            // ViewProjection, rebuilds matrices, and then bakes jitter only into
            // ProjectionJittered. Frustum culling and CSM cascades still use the
            // unjittered camera3D.Projection/ViewProjection to avoid edge shimmer
            // and shadow jitter.
            var res = DeviceServices.BaseApp.DeviceResolution;
            camera3D.UpdateTemporal(aspectRatio, res.X, res.Y);
            Camera.View = camera3D.View;
            Camera.Projection = Matrix4x4.Multiply(camera3D.ProjectionJittered, dpiTransform);
            // Apply the same compensation to history matrices so prev/current
            // stay aligned. Otherwise the TAA / MotionVectors velocity field drifts.
            Camera.PrevViewProjection = Matrix4x4.Multiply(camera3D.PrevViewProjection, dpiTransform);
        }
        else
        {
            camera3D.UpdateIfChanged(aspectRatio);
            Camera.View = camera3D.View;
            Camera.Projection = Matrix4x4.Multiply(camera3D.Projection, dpiTransform);
            // All-zero means no history. This prevents stale matrices from
            // surviving when the feature is disabled mid-run.
            Camera.PrevViewProjection = default;
        }

        // 1-5: CPU shadow-matrix computation chain. It runs after the camera is
        // updated and before the lighting CB is written. When shadows are off or
        // no light is active, Apply writes zeros.
        // Shadow sources are selected by the indices stored in Params0.Z/W
        // (written by the authoring layer), and the light-type decision is
        // centralized here.
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

        // Must go through SetLighting because it injects HdrExposure.
        // Writing the CB directly would leave hdrExposure at 0 in the shader, so
        // inverse-ACES text compensation would divide by 1e-4 and saturate all
        // text to white.
        SetLighting(lightParams);
    }

    // ============================================================
    // Instance: CB creation used during derived PrimitiveData initialization
    // ============================================================

    protected void CreateMatrixBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MatrixBuffers = new ID3D12Resource*[n];
        primitiveData.MappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out primitiveData.MappedMatrixBuffers[i]);
    }

    protected static void CreateMaterialBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MaterialBuffers = new ID3D12Resource*[n];
        primitiveData.MappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MaterialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out primitiveData.MappedMaterialBuffers[i]);
    }

    // ============================================================
    // Instance: alpha synchronization
    // ============================================================

    /// <summary>
    /// Synchronizes overall alpha into the material buffer of every primitive:
    ///   BaseColor.W = OriginalBaseColorAlpha x alpha
    ///   AlphaCutoff = OriginalAlphaCutoff   x alpha
    ///       (MASK scales proportionally to avoid clipping away the whole object
    ///       at low alpha)
    /// Called only when alpha changes. Writes every N-buffered frame to avoid
    /// flicker from reading stale values.
    /// </summary>
    protected void SyncAlpha(float alpha)
    {
        if (_currentAlpha == alpha)
            return;
        _currentAlpha = alpha;

        int n = (int)Device.frameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var materialParams = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[i]);
                var baseColor = materialParams.BaseColor;
                baseColor.W = primitive.OriginalBaseColorAlpha * alpha;
                materialParams.BaseColor = baseColor;
                materialParams.AlphaCutoff = primitive.OriginalAlphaCutoff * alpha;
                Unsafe.Write(primitive.MappedMaterialBuffers[i], materialParams);
            }
        }
    }

    // ============================================================
    // Instance: color-multiplier synchronization (Mesh3D.ColorTint)
    // ============================================================

    /// <summary>
    /// Synchronizes the mesh-level color multiplier into the material buffer of
    /// every primitive:
    ///   BaseColor.rgb = OriginalBaseColor.rgb x tint.rgb
    ///   (W is untouched; the alpha chain is owned exclusively by SyncAlpha)
    /// Called only when tint changes. Writes every N-buffered frame to avoid
    /// flicker from reading stale values.
    /// </summary>
    protected void SyncColorTint(Vector4 tint)
    {
        if (_currentColorTint == tint)
            return;
        _currentColorTint = tint;

        int n = (int)Device.frameCount;
        _drawList.Clear();
        CollectPrimitives(_drawList);

        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var materialParams = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[i]);
                var baseColor = materialParams.BaseColor;
                baseColor.X = primitive.OriginalBaseColor.X * tint.X;
                baseColor.Y = primitive.OriginalBaseColor.Y * tint.Y;
                baseColor.Z = primitive.OriginalBaseColor.Z * tint.Z;
                materialParams.BaseColor = baseColor;
                Unsafe.Write(primitive.MappedMaterialBuffers[i], materialParams);
            }
        }
    }

    // ============================================================
    // Unified highlighting: lazy shell-geometry construction for
    // non-instanced Wireframe (shared by Mesh3D / Model)
    // ============================================================

    /// <summary>
    /// Unified highlighting: lazily builds non-instanced Wireframe highlight
    /// boxes at runtime. On the first frame that enables wireframe, all
    /// primitives are processed one by one in CollectPrimitives order, with null
    /// placeholders for primitives that have no valid triangles. When fully
    /// disabled, this costs no memory. Once built, the geometry stays resident
    /// and is not rebuilt or released when toggled at runtime.
    /// Each primitive gets its own box, and skinning parameters
    /// (IsSkinned / BonePaletteStride) are inherited from the source primitive,
    /// so skinned models stay aligned through the same b3/t6 bone-transform path.
    /// Morph-target primitives also build shells: shell delta buffers are
    /// expanded to the shell-vertex layout, with shell-vertex to source-vertex
    /// mappings recorded during construction
    /// (see <see cref="CreateShellBox"/>). Weights are synchronized from the
    /// source every frame (see DXModel.ApplyMorphTargetsIfNeeded), so the same
    /// VS morph path stays tightly aligned to animation.
    /// edgeWidth comes from the host Highlight.EdgeWidth
    /// (a model-size proportion), and localSizeMax is the host model's maximum
    /// local dimension used as the scale baseline. The baked per-primitive local
    /// thickness is h = edgeWidth x localSizeMax / node scale
    /// (see <see cref="HighlightGeometry.NodeScaleOf"/>), giving a world-space
    /// edge width of approximately edgeWidth x model world max dimension, which
    /// stays consistent across assets. Rebuild when it no longer matches the host.
    /// </summary>
    protected void EnsureWireframeHighlights(float edgeWidth, float localSizeMax)
    {
        if (_wireframeBoxes != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release old shell geometry and rebuild with
            // the new width so it takes effect immediately this frame.
            foreach (var box in _wireframeBoxes)
                DisposeHighlightBox(box);
            _wireframeBoxes = null;
        }
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;
        _wireframeBoxes = new List<HighlightBox>(_drawList.Count);
        for (int i = 0; i < _drawList.Count; i++)
        {
            var source = _drawList[i];
            _wireframeBoxes.Add(source.Indices.Length >= 3 && source.Vertices.Count > 0
                ? CreateShellBox(source, HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode))
                : null);
            // Record the node reference on the box so WorldTransform can be
            // fetched each frame from the same source as rendering. Cloned
            // primitives are collected by CollectPrimitives and share the
            // group's lifetime.
            if (_wireframeBoxes[i] != null)
                _wireframeBoxes[i].OwnerNode = source.OwnerNode;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>
    /// Unified highlighting: lazily builds shared shell geometry for instanced
    /// templates on the first frame that enables wireframe. It groups by source
    /// and builds a rigid shell from merged non-skinned primitives, with
    /// per-instance boxes sharing VB/IB, plus a skinned shell from merged
    /// primitives that share the same Skin. The skinned shell inherits
    /// IsSkinned=1 and BonePaletteStride from the source material and uses the
    /// per-instance bone-palette path to stay tightly aligned to animation.
    /// Mixed assets draw both shells, while assets that are purely skinned with a
    /// single Skin only emit the skinned shell.
    /// Morph-target primitives are skipped because morph weights are indexed per
    /// instance and require shell-shape delta buffers that merged geometry cannot
    /// express. This is documented behavior: for instanced models with morph,
    /// Wireframe highlighting covers only the remaining parts, while Bounds
    /// highlighting is unaffected.
    /// Multi-skin assets, where each node has an independent Skin, skip the
    /// skinned shell because the merged template cannot express per-skin palette
    /// offsets. Phase 2 handles this with a one-time CPU fixup of per-vertex
    /// palette offsets at build time. When no usable primitives exist, keep null
    /// and retry on later frames.
    /// edgeWidth comes from the host Highlight.EdgeWidth and localSizeMax is the
    /// template's maximum local dimension. The per-primitive local thickness is
    /// h = edgeWidth x localSizeMax / node scale
    /// (see <see cref="HighlightGeometry.NodeScaleOf"/>), so world edge width is
    /// approximately edgeWidth x instance world max dimension and stays
    /// consistent across assets. Rebuild when it no longer matches the host.
    /// </summary>
    protected void EnsureShellGeometry(float edgeWidth, float localSizeMax)
    {
        if (_shellGeometry != null || _skinnedShellGeometry != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release all shared templates and instance
            // shell-box pools, which share template geometry, and rebuild with
            // the new width so it takes effect immediately this frame.
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
                continue; // Skip morph-target primitives because merged instanced
                          // templates cannot express per-primitive morph sets.
            float h = HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode);
            if (source.MaterialParams.IsSkinned != 0)
            {
                if (multiSkin)
                    continue; // Multi-skin already detected: skip the skinned shell entirely.
                // For primitives sharing the same Skin, OwnerNode.Skin is mapped
                // through the same skinMap to the same cloned reference, so
                // ReferenceEquals is reliable here.
                var skin = source.OwnerNode?.Skin;
                if (skin == null)
                    continue; // Defensive skip: marked skinned but has no Skin data.
                if (sharedSkin == null)
                {
                    sharedSkin = skin;
                }
                else if (!ReferenceEquals(sharedSkin, skin))
                {
                    // Multi-skin asset: discard accumulated skinned data and
                    // skip the skinned shell entirely (see the doc and plan risk 1).
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
            return; // No usable primitives: keep null and retry on later frames.
    
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

    // ============================================================
    // Instance: material-texture replacement
    // (derived classes provide primitive lists through CollectPrimitives)
    // ============================================================

    /// <summary>Gets the DXTexture reference from the specified PrimitiveData slot.</summary>
    static DXTexture GetTextureBySlot(PrimitiveData p, TextureSlot slot) => slot switch
    {
        TextureSlot.BaseColor => p.BaseColorTexture,
        TextureSlot.Normal => p.NormalTexture,
        TextureSlot.MetallicRoughness => p.MetallicRoughnessTexture,
        TextureSlot.Occlusion => p.OcclusionTexture,
        TextureSlot.Emissive => p.EmissiveTexture,
        _ => p.BaseColorTexture
    };

    /// <summary>Sets the DXTexture reference on the specified PrimitiveData slot.</summary>
    static void SetTextureBySlot(PrimitiveData p, TextureSlot slot, DXTexture tex)
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
    /// Replaces the texture on the specified slot for all primitives.
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
            // Fast path
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = DXTexture.CreateFromDecoder(decoder);
            foreach (var primitive in _drawList)
                SetTextureBySlot(primitive, slot, newTex);
        }
    }

    /// <summary>
    /// Writes material-parameter overrides into the N-buffered Material CB of
    /// every primitive.
    /// </summary>
    internal void SyncMaterialParams(float? metallic, float? roughness, Vector4? emissive)
    {
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0) return;

        int n = (int)Device.frameCount;
        for (int i = 0; i < n; i++)
        {
            foreach (var primitive in _drawList)
            {
                var mp = Unsafe.Read<MaterialParams>(primitive.MappedMaterialBuffers[i]);
                if (metallic.HasValue) mp.MetallicFactor = metallic.Value;
                if (roughness.HasValue) mp.RoughnessFactor = roughness.Value;
                if (emissive.HasValue) mp.EmissiveFactor = emissive.Value;
                Unsafe.Write(primitive.MappedMaterialBuffers[i], mp);
            }
        }
    }

    // ============================================================
    // Instance: derived hooks
    // ============================================================

    /// <summary>Derived classes append all PrimitiveData that should be drawn
    /// this frame into `result`.</summary>
    protected abstract void CollectPrimitives(List<PrimitiveData> result);

    /// <summary>
    /// Additional root-signature binding hook before Draw.
    /// DXModel overrides this to bind slot 8 (b3 BoneMatrices).
    /// The default implementation is empty because DXMesh3D and other
    /// non-skinned primitive groups need nothing extra here.
    /// </summary>
    protected virtual void OnBeforeDraw() { }

    /// <summary>
    /// Derived classes may override this to return the bone StructuredBuffer
    /// SRV handle. Both regular DXModel and DXInstancedModel use the same
    /// dynamic bone-palette path.
    /// </summary>
    protected virtual GpuDescriptorHandle GetBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier B): derived classes may override this to
    /// return the previous bone-palette SB SRV handle.</summary>
    protected virtual GpuDescriptorHandle GetPrevBoneSrvHandle() => default;

    /// <summary>2-3 Step C (tier C-b completion): derived classes may override
    /// this to return the previous morph-weights SB SRV handle.</summary>
    protected virtual GpuDescriptorHandle GetPrevMorphSrvHandle() => default;

    public abstract void Dispose();

    // ============================================================
    // Unified highlighting: highlight boxes
    // (dual-color primitive groups of faces + edges, lazily built, with no new
    // PSO. Faces use Transparent, edges use Opaque.)
    // ============================================================

    /// <summary>
    /// Unified highlighting: one highlight box consists of two PrimitiveData
    /// instances, one for faces and one for edges.
    /// Two geometry flavors exist:
    /// Bounds = unit cube [-0.5,0.5]^3
    ///   (world matrix = Scale(Extents*2) x Translate(Center));
    /// Wireframe = surface-fitted shell faces + edge strips in model-local
    ///   space, with the world matrix provided by the model / instance matrix.
    ///   Vertices carry bone indices and weights, so they stay aligned to
    ///   animation through the same VS skinning path as the source primitive.
    /// Faces are blended semi-transparent geometry using the Transparent PSO.
    /// Edges are solid thin strips using the Opaque PSO with depth writes.
    /// PrevWorld comes from a CPU shadow copy because N-buffered CBs must never
    /// be read back. It is used for the TAA / motion-vector velocity field.
    /// </summary>
    protected sealed class HighlightBox
    {
        public PrimitiveData Face;
        public PrimitiveData Edges;

        /// <summary>Face alpha for this frame, taken from SurfaceColor.W.
        /// Faces are drawn only when it is &gt; 0. When it is 0, the box becomes
        /// edge-only. Recorded every frame by the write hook.</summary>
        public float FaceAlpha;

        /// <summary>Box world matrix from the previous frame, stored as a CPU
        /// shadow copy. On the first frame it is Identity, which acts as the
        /// zero-velocity sentinel.</summary>
        public Matrix4x4 PrevWorld = Matrix4x4.Identity;

        /// <summary>Owner node of the shell source primitive. Recorded for
        /// non-instanced per-primitive shell boxes. Each frame it supplies
        /// WorldTransform x group world matrix from the same source as rendering,
        /// keeping node hierarchy scaling, translation, and animation aligned.
        /// Null means identity, which is used for Mesh3D procedural primitives
        /// and instanced shared-template boxes.</summary>
        public GltfNodeBase? OwnerNode;

        /// <summary>Shell source primitive for non-instanced per-primitive
        /// shell boxes. Used for morph-weight synchronization: when source
        /// weights are written, the same set is propagated into the Material CBs
        /// of both shell primitives. Shell delta buffers are expanded to the
        /// shell-vertex layout, while weights are shared with the source.</summary>
        public PrimitiveData? SourcePrimitive;
    }

    /// <summary>Unified highlighting: lazily builds the host Bounds box
    /// (faces + edges). Called once on the first enabled frame and kept
    /// resident afterward.</summary>
    protected HighlightBox CreateBoundsBox()
    {
        var box = new HighlightBox();
        box.Face = CreateBoxFacePrimitive();
        box.Edges = CreateBoxEdgesPrimitive();
        return box;
    }

    /// <summary>Unified highlighting: gets or creates the instance Bounds box
    /// for the given compacted writeIndex. The pool grows lazily and stays
    /// resident until the group is released.</summary>
    protected HighlightBox AcquireBoundsBox(int index)
    {
        while (_instanceBoundsBoxes.Count <= index)
            _instanceBoundsBoxes.Add(CreateBoundsBox());
        return _instanceBoundsBoxes[index];
    }


    /// <summary>Unified highlighting: builds a Wireframe shell highlight box
    /// for a single source primitive, consisting of shell faces and edge strips.
    /// Source vertices are copied field by field, including bone indices and
    /// weights, so skinned models stay aligned through the same VS skinning
    /// path. Material parameters are copied from the source primitive and then
    /// forced to Unlit plus transparent or opaque mode as needed, while
    /// IsSkinned and BonePaletteStride are inherited.
    /// For morph-target source primitives, shell delta buffers are expanded to
    /// the shell-vertex layout, with the source-vertex index recorded per shell
    /// vertex at build time. Weights are shared with the source and synced every
    /// frame by DXModel, so the same VS morph path stays tightly aligned to morph
    /// animation.
    /// edgeWidth is the baked local thickness h =
    /// Highlight.EdgeWidth x model max local dimension / node scale
    /// (see <see cref="HighlightGeometry.NodeScaleOf"/>). Edge strips have a full
    /// width of 2*h, and shell faces use the same outward expansion thickness.</summary>
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

    /// <summary>Unified highlighting: attaches the morph path to a shell
    /// primitive by expanding source deltas to the shell-vertex layout. The
    /// shell-vertex to source-vertex mapping is recorded when the shell is
    /// built. This sets HasMorphTargets, MorphTargetCount, and
    /// MorphVertexCount (= shell vertex count), then writes them back to the
    /// Material CB for all frames. Weights are shared with the source and synced
    /// every frame by DXModel.ApplyMorphTargetsIfNeeded.</summary>
    void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(shell.MappedMaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
    }

    /// <summary>
    /// Phase 3: packs morph-target deltas into the
    /// StructuredBuffer&lt;float&gt; layout
    /// [targetIndex * vertexCount + vertexIndex] * 9 floats = pos.xyz + normal.xyz + tangent.xyz
    /// When vertexMap is not null, deltas are expanded through that mapping.
    /// In the shell-vertex layout, the delta for shell vertex v comes from
    /// source delta [vertexMap[v]], so vertexCount = vertexMap.Count.
    /// On the source-primitive path, no mapping is passed and the layout is the
    /// identity mapping, so vertexCount = baseVertices.Length.
    /// 2-3 Step C: the previous position used by velocity reuses the same delta
    /// data, because morph deltas are static per-target geometric differences.
    /// There is no concept of a "previous-frame delta"; only weights change.
    /// So capacity does not need to be doubled, matching VK/Metal.
    /// </summary>
    protected static void CreateMorphDeltaBuffer(PrimitiveData primitiveData, Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets, IReadOnlyList<int>? vertexMap = null)
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
                    deltaData[baseIdx    ] = target.PositionDeltas[srcIdx].X;
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

        // Create upload buffer
        primitiveData.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer(
            HeapType.Upload, (ulong)(deltaData.Length * sizeof(float)),
            ResourceStates.GenericRead);
        fixed (float* pSrc = deltaData)
        {
            void* pDst;
            primitiveData.MorphDeltasBuffer->Map(0, null, &pDst);
            Unsafe.CopyBlock(pDst, pSrc, (uint)(deltaData.Length * sizeof(float)));
            primitiveData.MorphDeltasBuffer->Unmap(0, null);
        }

        // Allocate SRV descriptor
        primitiveData.MorphDescriptorId = Device.DescriptorAllocator.Allocate();
        var cpuHandle = Device.SrvHeapManager.GetCpuHandle(primitiveData.MorphDescriptorId);
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 0x00001688u,
            Buffer = new BufferSrv
            {
                FirstElement = 0,
                NumElements = (uint)deltaData.Length,
                StructureByteStride = (uint)sizeof(float),
                Flags = BufferSrvFlags.None
            }
        };
        Device.D3dDevice->CreateShaderResourceView(primitiveData.MorphDeltasBuffer, &srvDesc, cpuHandle);
        primitiveData.MorphDeltasSrvHandle = Device.SrvHeapManager.GetGpuHandle(primitiveData.MorphDescriptorId);
    }

    /// <summary>Unified highlighting: gets or creates the instance Wireframe
    /// shell box for the given compacted writeIndex using a lazily growing pool.
    /// VB/IB are shared from the template _shellGeometry, so the box's own
    /// VertexBuffer/IndexBuffer stay null. PrimitiveData.Dispose is null-safe,
    /// so there is no double release. Only Material CBs are created locally;
    /// matrices come from the group-level shared CB.
    /// Returns null when the template is not ready because no non-skinned,
    /// non-morph primitives exist, and callers should skip adding it to the draw
    /// list. Empty slots created before the template is ready are patched up
    /// automatically after it becomes available, which covers runtime asset
    /// replacement followed by successful shell-template rebuild.</summary>
    protected HighlightBox? AcquireShellBox(int index)
    {
        if (_shellGeometry == null)
            return null;
        while (_instanceShellBoxes.Count <= index)
            _instanceShellBoxes.Add(CreateInstanceShellBox());
        var box = _instanceShellBoxes[index];
        if (box == null)
        {
            // Fill a previously empty slot. These are indices reserved while the
            // template was not ready and are backfilled on first use after it becomes ready.
            box = CreateInstanceShellBox();
            _instanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: builds the per-instance shell box that
    /// shares the template geometry. See AcquireShellBox.</summary>
    HighlightBox? CreateInstanceShellBox()
    {
        if (_shellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_shellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_shellGeometry.Edges);
        return box;
    }

    /// <summary>Unified highlighting: gets or creates the skinned instance
    /// Wireframe shell box for the given compacted writeIndex, following the
    /// same structure as AcquireShellBox. VB/IB are shared from the skinned
    /// template _skinnedShellGeometry, and the shell uses the per-instance
    /// bone-palette path.
    /// Returns null when the template is not ready because there are no
    /// single-Skin skinned primitives or the asset is multi-skin, and callers
    /// should skip adding it to the draw list.</summary>
    protected HighlightBox? AcquireSkinnedShellBox(int index)
    {
        if (_skinnedShellGeometry == null)
            return null;
        while (_skinnedInstanceShellBoxes.Count <= index)
            _skinnedInstanceShellBoxes.Add(CreateSkinnedInstanceShellBox());
        var box = _skinnedInstanceShellBoxes[index];
        if (box == null)
        {
            // Fill a previously empty slot reserved while the template was not
            // ready. It is backfilled on first use after the template becomes ready.
            box = CreateSkinnedInstanceShellBox();
            _skinnedInstanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: builds the per-instance shell box that
    /// shares the skinned template geometry. See AcquireSkinnedShellBox.</summary>
    HighlightBox? CreateSkinnedInstanceShellBox()
    {
        if (_skinnedShellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_skinnedShellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_skinnedShellGeometry.Edges);
        return box;
    }

    /// <summary>Unified highlighting: releases shared shell templates
    /// (rigid + skinned) and both instance shell-box pools, which share template
    /// geometry. Used both by edge-width rebuilds and DisposeHighlights.</summary>
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

    /// <summary>2-3 Step C: synchronizes previous-state flags for shell
    /// primitives. HasPrevBones, HasPrevInstanceWorld, and HasPrevMorph on shell
    /// Face/Edges must be enabled together with the main primitive, or the shell
    /// loses its motion trail. This explicitly covers plan risk 2.
    /// Both shared templates and both instance-box pools are covered, because a
    /// pooled box may have been created before previous-state data became ready
    /// and still carry stale flags, so each box may need patching.
    /// With the changed guard and all-frame N-buffered writes, the per-frame
    /// cost is negligible.</summary>
    protected void SyncShellPrevFlags(bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        if (_shellGeometry != null)
            SyncShellBoxPrevFlags(_shellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        if (_skinnedShellGeometry != null)
            SyncShellBoxPrevFlags(_skinnedShellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _instanceShellBoxes)
            SyncShellBoxPrevFlags(box, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _skinnedInstanceShellBoxes)
            SyncShellBoxPrevFlags(box, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
    }

    static void SyncShellBoxPrevFlags(HighlightBox box, bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        SyncShellPrimPrevFlags(box.Face, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        SyncShellPrimPrevFlags(box.Edges, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
    }

    static void SyncShellPrimPrevFlags(PrimitiveData primitive, bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        bool changed = false;
        if (primitive.MaterialParams.HasPrevInstanceWorld == 0 && hasPrevInstanceWorld)
        {
            primitive.MaterialParams.HasPrevInstanceWorld = 1;
            changed = true;
        }
        if (primitive.MaterialParams.HasPrevBones == 0 && hasPrevBones)
        {
            primitive.MaterialParams.HasPrevBones = 1;
            changed = true;
        }
        if (primitive.MaterialParams.HasPrevMorph == 0 && hasPrevMorph)
        {
            primitive.MaterialParams.HasPrevMorph = 1;
            changed = true;
        }
        if (changed)
        {
            for (int f = 0; f < Device.frameCount; f++)
                Unsafe.Write(primitive.MappedMaterialBuffers[f], primitive.MaterialParams);
        }
    }

    /// <summary>Unified highlighting: derives a shared-geometry box from a
    /// template primitive by copying references to CPU vertex/index arrays,
    /// VB/IB views, and material / texture references, while leaving held GPU
    /// pointers null. PrimitiveData.Dispose is null-safe, so no double release
    /// occurs. Vertices and indices are immutable shared data, and Dispose only
    /// releases GPU buffers, so aliasing is safe. DrawPrimitive reads
    /// Indices.Length every draw to determine index count.
    /// A local N-buffered Material CB is created and initialized for all frames.
    /// Matrix CB is unnecessary because instanced rendering uses the group-level
    /// shared matrix CB plus instance-stream slots.</summary>
    PrimitiveData CreateSharedShellPrimitive(PrimitiveData template)
    {
        var primitive = new PrimitiveData
        {
            Vertices = template.Vertices,
            Indices = template.Indices,
            VertexBufferView = template.VertexBufferView,
            IndexBufferView = template.IndexBufferView,
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
        CreateMaterialBuffer(primitive);
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        return primitive;
    }

    /// <summary>
    /// Unified highlighting: writes the box world matrix plus the dual face/edge
    /// colors into the box's own N-buffered CB every frame for the current fi.
    /// There is no change gate because face alpha, taken from SurfaceColor.W,
    /// pulses every frame and the steady write cost matches normal primitives.
    /// PrevWorld comes from the CPU shadow copy in box.PrevWorld, and after
    /// writing, the current world is rolled into the shadow for the next frame.
    /// `world` is supplied by the caller:
    /// Bounds = Scale(Extents*2) x Translate(Center), after the caller checks
    /// for degenerate boxes;
    /// Wireframe = model / instance world matrix, which is always valid.
    /// </summary>
    protected void WriteHighlightBox(HighlightBox box, Matrix4x4 world, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(world),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            PrevWorld = Matrix4x4.Transpose(box.PrevWorld),
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };
        Unsafe.Write(box.Face.MappedMatrixBuffers[fi], matrices);
        Unsafe.Write(box.Edges.MappedMatrixBuffers[fi], matrices);

        box.Face.MaterialParams.BaseColor = faceColor;
        Unsafe.Write(box.Face.MappedMaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        Unsafe.Write(box.Edges.MappedMaterialBuffers[fi], box.Edges.MaterialParams);

        box.PrevWorld = world;
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting: draws a single highlight box. When face
    /// alpha (SurfaceColor.W) is &gt; 0, faces are drawn using the engine's
    /// double-sided transparent 2-pass convention (Front -> Back). When it is 0,
    /// the box automatically becomes edge-only and faces are skipped.
    /// Edges use the Opaque path with CullNone and depth writes, so solid thin
    /// strips cover the faces and any interior geometry.</summary>
    protected void DrawHighlightBox(HighlightBox box, ID3D12Resource* lightCB)
    {
        int fi = (int)Device.FrameIndex;
        var face = box.Face;
        var edges = box.Edges;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
            OnBeforeDraw();
            Pipeline.DrawPrimitive(face, lightCB, face.MatrixBuffers[fi], null, 1, 0,
                GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());

            Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
            OnBeforeDraw();
            Pipeline.DrawPrimitive(face, lightCB, face.MatrixBuffers[fi], null, 1, 0,
                GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
        }

        Pipeline.SetPipeline(PipelineMode.Opaque, PipelineCullVariant.None, depthWrite: true);
        OnBeforeDraw();
        Pipeline.DrawPrimitive(edges, lightCB, edges.MatrixBuffers[fi], null, 1, 0,
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
    }

    /// <summary>Unified highlighting: draws all instance boxes whose Bounds
    /// highlighting is enabled this frame, one by one through DrawHighlightBox.</summary>
    protected void DrawBoundsBoxes(ID3D12Resource* lightCB)
    {
        for (int i = 0; i < _boundsBoxDrawList.Count; i++)
            DrawHighlightBox(_instanceBoundsBoxes[_boundsBoxDrawList[i]], lightCB);
    }

    /// <summary>Unified highlighting: releases all highlight GPU resources,
    /// including the host Bounds box, the instance Bounds-box pool,
    /// per-primitive Wireframe shell boxes, shared shell templates
    /// (rigid / skinned), and Wireframe instance-box pools
    /// (rigid / skinned).</summary>
    protected void DisposeHighlights()
    {
        DisposeHighlightBox(_boundsBox);
        _boundsBox = null;
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
        _outline2DActive = false;
    }

    static void DisposeHighlightBox(HighlightBox? box)
    {
        if (box == null)
            return;
        box.Face?.Dispose();
        box.Edges?.Dispose();
    }

    /// <summary>
    /// Unified highlighting, Bounds-box geometry: face PrimitiveData.
    /// Eight corners are baked into [-0.5,0.5]^3, using bit-coded corner indices
    /// x + y*2 + z*4, with 36 indices total.
    /// RenderMode=0 (Unlit) and AlphaMode=2 (BLEND), so true transparency uses
    /// the Transparent PSO. DoubleSided is enabled, and all five textures are
    /// White with Use*Map=0 because Unlit does not sample them.
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
    /// Unified highlighting, Bounds-box geometry: edge PrimitiveData.
    /// Uses 12 thin boxes, four on each of the three axes. Each one extends by
    /// one thickness beyond the corners along its axis
    /// ([-0.5-h, 0.5+h]) so all eight corners join seamlessly.
    /// RenderMode=0 and AlphaMode=0 (OPAQUE), so solid edges use the Opaque PSO
    /// with depth writes and do not pulse with face alpha. EdgeColor stays
    /// semantically solid.
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



    /// <summary>Unified highlighting: builds a shell primitive from vertices and
    /// indices. Material settings are copied from the source primitive and then
    /// forced to Unlit plus transparent (BLEND) or opaque (OPAQUE). It uses
    /// DoubleSided and White for all five textures, since Unlit does not sample
    /// them. VB/IB and both CBs are created and initialized for all frames by
    /// InitBoundsBoxGpuResources.
    /// Skinning and instancing flags such as IsSkinned, BonePaletteStride, and
    /// IsInstanced are inherited from the source material, which is critical for
    /// keeping non-instanced per-primitive shell boxes tightly aligned to
    /// animation.</summary>
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
            Use32BitIndices = true,
            DoubleSided = true,
            IsTransparent = isTransparent,
            LocalBoundsCenter = Vector3.Zero,
            LocalBoundsExtents = new Vector3(0.5f),
            MaterialParams = mp,
        };
        InitBoundsBoxGpuResources(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting: creates VB/IB/Material CB/Matrix CB for
    /// box primitives and initializes them for all frames to avoid garbage reads
    /// under N-buffering.</summary>
    void InitBoundsBoxGpuResources(PrimitiveData primitive)
    {
        primitive.VertexBuffer = Device.CreateVertexBuffer(primitive.Vertices.ToArray(), out primitive.VertexBufferView);
        primitive.IndexBuffer = Device.CreateIndexBuffer(primitive.Indices, out primitive.IndexBufferView);
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
            Unsafe.Write(primitive.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }
    }

    // ============================================================
    // Unified highlighting: Outline2D state
    // (mask channel, with active state collected by Graphics in a separate pass)
    // ============================================================

    protected void SetOutline2DState(bool active, Vector4 color, float width)
    {
        _outline2DActive = active;
        _outline2DColor = color;
        _outline2DWidth = width;
    }

    protected bool HasOutline2D => _outline2DActive;
    protected Vector4 Outline2DColor => _outline2DColor;
    protected float Outline2DWidth => _outline2DWidth;
    internal bool Outline2DActive => _outline2DActive;
    internal Vector4 Outline2DMaskColor => _outline2DColor;
    internal float Outline2DMaskWidth => _outline2DWidth;

    // ============================================================
    // Instance: Draw (three-bucket grouping)
    // ============================================================

    static float ComputeTransparentSortDepth(PrimitiveData primitiveData)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = Unsafe.Read<MatrixBuffer>(primitiveData.MappedMatrixBuffers[fi]);
        var world = Matrix4x4.Transpose(matrices.World);
        var center = Vector3.Transform(primitiveData.LocalBoundsCenter, world);

        var cameraPos = new Vector3(Camera.View.M41, Camera.View.M42, Camera.View.M43);
        if (DeviceServices.BaseApp != null)
            cameraPos = DeviceServices.BaseApp.CameraPos;

        Vector3 cameraForward;
        if (DeviceServices.BaseApp != null)
        {
            cameraForward = DeviceServices.BaseApp.CameraTarget - DeviceServices.BaseApp.CameraPos;
        }
        else
        {
            cameraForward = Vector3.UnitZ;
        }

        if (cameraForward.LengthSquared() < 1e-6f)
            cameraForward = Vector3.UnitZ;
        else
            cameraForward = Vector3.Normalize(cameraForward);

        return Vector3.Dot(center - cameraPos, cameraForward);
    }

    static int CompareTransparentPrimitives(PrimitiveData a, PrimitiveData b)
    {
        return ComputeTransparentSortDepth(b).CompareTo(ComputeTransparentSortDepth(a));
    }

    public virtual void Draw()
    {
        if (!_transformInitialized)
            return;

        // Collect all PrimitiveData for this frame, reusing the member list to avoid GC.
        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        var lightCB = lightConstantBuffers[(int)Device.FrameIndex];

        // Group into Opaque / Fade (forced when overall alpha < 1) /
        // Transparent (true BLEND materials).
        // Important fix: when Alpha < 1, non-BLEND materials use the Fade PSO
        // (blending + depth writes) instead of the Transparent PSO. Depth
        // writes prevent excessive transparency and internal-geometry bleed
        // caused by sequential blending of overlapping mesh layers in complex models.
        bool forceFadeByAlpha = _currentAlpha < 1.0f;

        // 1. Opaque
        // Writes depth. Under 2-2 rule 7, AoExempt primitives use the NoDepth
        // variant and do not write depth.
        bool pipelineSet = false;
        bool currentDoubleSided = false;
        bool currentDepthWrite = true;
        if (!forceFadeByAlpha)
        {
            for (int i = 0; i < _drawList.Count; i++)
            {
                var p = _drawList[i];
                if (p.IsTransparent) continue;
                bool depthWrite = !p.AoExempt;
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite)
                {
                    Pipeline.SetPipeline(PipelineMode.Opaque,
                        p.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite);
                    // Important fix: OnBeforeDraw must be called after
                    // SetPipeline. When the root signature changes,
                    // SetPipeline calls SetGraphicsRootSignature internally,
                    // which clears all currently bound parameters including
                    // slot 8 BoneMatrices. Rebinding afterward is required.
                    OnBeforeDraw();
                    pipelineSet = true;
                    currentDoubleSided = p.DoubleSided;
                    currentDepthWrite = depthWrite;
                }
                DrawPrimitive(p, lightCB);
            }
        }

        // 2. Fade
        // Enabled only when _currentAlpha < 1. Non-BLEND materials use the
        // Fade PSO with blending and depth writes.
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
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite)
                {
                    Pipeline.SetPipeline(PipelineMode.Fade,
                        p.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite);
                    OnBeforeDraw();
                    pipelineSet = true;
                    currentDoubleSided = p.DoubleSided;
                    currentDepthWrite = depthWrite;
                }
                DrawPrimitive(p, lightCB);
            }
        }

        // 3. Transparent
        // True BLEND materials. No depth writes.
        _singleSidedTransparentList.Clear();
        _doubleSidedTransparentList.Clear();
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (!p.IsTransparent) continue;
            if (p.DoubleSided)
                _doubleSidedTransparentList.Add(p);
            else
                _singleSidedTransparentList.Add(p);
        }

        _singleSidedTransparentList.Sort(CompareTransparentPrimitives);
        _doubleSidedTransparentList.Sort(CompareTransparentPrimitives);

        pipelineSet = false;
        currentDoubleSided = false;
        for (int i = 0; i < _singleSidedTransparentList.Count; i++)
        {
            var p = _singleSidedTransparentList[i];
            if (!pipelineSet || currentDoubleSided)
            {
                Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
                OnBeforeDraw();
                pipelineSet = true;
                currentDoubleSided = false;
            }
            DrawPrimitive(p, lightCB);
        }

        pipelineSet = false;
        currentDoubleSided = true;
        for (int i = 0; i < _doubleSidedTransparentList.Count; i++)
        {
            var p = _doubleSidedTransparentList[i];
            if (p.DoubleSided)
            {
                Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Front);
                OnBeforeDraw();
                DrawPrimitive(p, lightCB);

                Pipeline.SetPipeline(PipelineMode.Transparent, PipelineCullVariant.Back);
                OnBeforeDraw();
                pipelineSet = true;
                currentDoubleSided = false;
                DrawPrimitive(p, lightCB);
                continue;
            }
        }

        // Unified highlighting:
        // lazily built face+edge primitive groups that live outside the
        // CollectPrimitives / SyncAlpha chain. Faces use transparent 2-pass
        // rendering, edges use Opaque with depth writes, and the dual-color
        // highlight is drawn after all regular surfaces in the group.
        // This covers the host Bounds box plus per-primitive Wireframe shell
        // boxes. When SurfaceColor.w = 0, the shell becomes edge-only and face
        // drawing is skipped through the FaceAlpha gate.
        if (_boundsActive && _boundsBox != null)
            DrawHighlightBox(_boundsBox, lightCB);
        if (_wireframeEnabled && _wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var highlight = _wireframeBoxes[i];
                if (highlight != null)
                    DrawHighlightBox(highlight, lightCB);
            }
        }
    }

    void DrawPrimitive(PrimitiveData primitiveData, ID3D12Resource* lightConstantBuffer)
    {
        int fi = (int)Device.FrameIndex;
        // Unified entry point: regular draws use instanceCount=1 and
        // instanceBuffer=null, which automatically selects the identity buffer.
        Pipeline.DrawPrimitive(primitiveData, lightConstantBuffer, primitiveData.MatrixBuffers[fi], null, 1, 0,
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());
    }

    public virtual void DrawOutlineMask()
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        var lightCB = lightConstantBuffers[(int)Device.FrameIndex];
        bool pipelineSet = false;
        bool currentDoubleSided = false;

        // Rewrite outline color per group through root constant b6.
        // Mask pixels carry the group's color, and the composite pass picks
        // color per pixel from it, allowing multiple colors in the same frame.
        Pipeline.SetOutlineMaskColor(_outline2DColor);

        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent)
                continue;

            if (!pipelineSet || currentDoubleSided != p.DoubleSided)
            {
                Pipeline.SetPipeline(PipelineMode.Opaque,
                    p.DoubleSided ? PipelineCullVariant.None : PipelineCullVariant.Back, depthWrite: false);
                OnBeforeDraw();
                pipelineSet = true;
                currentDoubleSided = p.DoubleSided;
            }

            DrawPrimitive(p, lightCB);
        }
    }

    // ============================================================
    // Instance: 1-5 shadow-pass rendering
    // ============================================================

    /// <summary>
    /// Determines whether shadow primitives in the same group can share one
    /// b2 (root parameter 2) and one t5 (root parameter 9).
    ///
    /// The shadow VS only reads five fields from b2:
    /// renderMode, isInstanced, isSkinned, bonePaletteStride, and
    /// hasMorphTargets
    /// (Pipeline.cs VSMain, L1041/1045/1085/1087/1114/1230).
    /// Material color, alphaMode, alphaCutoff, and texture-enable flags are all
    /// PS-side, and this pass uses an empty PS, so none of them are read.
    /// When these five fields are identical across the group, the b2 bound by
    /// the first primitive is byte-for-byte equivalent for the rest, so later
    /// primitives can skip both the b2 and t5 bindings.
    /// When hasMorphTargets==0, morphTargetCount, morphVertexCount, and
    /// morphWeights all become dead code, and t5 is never sampled either, so
    /// it can be skipped together with b2.
    ///
    /// Primitives with morph data are always treated as non-shareable.
    /// The decision is based on the MorphTargets fixed at load time rather than
    /// the per-frame MaterialParams.HasMorphTargets flag, so the conclusion does
    /// not depend on whether the shadow pass runs before or after morph-weight writes.
    /// isSkinned is node-level rather than model-level
    /// (see DXModel.SyncSkinningMaterialParams), so it must be compared per primitive.
    ///
    /// Transparent primitives do not participate in this decision because the
    /// shadow pass skips them anyway. Letting them disqualify the whole group
    /// would waste the optimization.
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
    /// Shadow-pass draw path. Under contract rule 7 it performs no frustum
    /// culling: all opaque primitives are drawn, and true BLEND materials are
    /// skipped because primitives that do not write depth should not cast
    /// shadows. The shadow-pass entry has already set the PSO and root
    /// signature. This method only supplies OnBeforeDraw (b3 bone CBV),
    /// group-invariant bindings, and per-primitive draws.
    ///
    /// The primitive list is cached per pass using <see cref="CascadedShadow.Epoch"/>.
    /// RenderShadowPass calls this method repeatedly for atlas quadrants
    /// (three cascades plus spotlight). Between quadrants only the viewport and
    /// light-space ViewProj change, while the primitive set stays the same, so
    /// CollectPrimitives only runs once per pass. For DXModel it is a recursive
    /// glTF node-tree walk plus AddRange, and for DXMesh3D it is AddRange only.
    /// Both are side-effect free and safe to replay.
    /// </summary>
    public virtual void DrawShadow()
    {
        if (!_transformInitialized)
            return;

        if (_shadowDrawListEpoch != CascadedShadow.Epoch)
        {
            _shadowDrawList.Clear();
            CollectPrimitives(_shadowDrawList);
            _shadowDrawListEpoch = CascadedShadow.Epoch;
        }

        if (_shadowDrawList.Count == 0)
            return;

        OnBeforeDraw();

        // Group-invariant t6/t8/t9/t10 are bound once for the group.
        // This class has no instance stream, so t9 always uses the default zero-value SB.
        Pipeline.SetShadowGroupBindings(
            GetBoneSrvHandle(), GetPrevBoneSrvHandle(), default, GetPrevMorphSrvHandle());

        // When b2/t5 are identical within the group, only the first primitive
        // binds them and the rest reuse the state. Otherwise binding falls back
        // to per-primitive. The decision is a plain field comparison with no
        // virtual dispatch, so it is recomputed every call instead of cached,
        // avoiding stale-state windows.
        bool shareMaterial = CanShareShadowMaterial(_shadowDrawList);
        bool materialBound = false;

        int fi = (int)Device.FrameIndex;
        for (int i = 0; i < _shadowDrawList.Count; i++)
        {
            var p = _shadowDrawList[i];
            if (p.IsTransparent)
                continue;
            Pipeline.DrawShadowPrimitive(p, p.MatrixBuffers[fi], null, 1, 0,
                bindMaterial: !shareMaterial || !materialBound);
            materialBound = true;
        }
    }
}
