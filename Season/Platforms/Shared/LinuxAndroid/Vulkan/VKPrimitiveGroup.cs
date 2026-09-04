// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Models;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Season.Platforms.Shared.LinuxAndroid.Vulkan;

/// <summary>
/// Common base class on the Pbr3D path for primitive groups rendered from a PrimitiveData list
/// (Vulkan backend).
/// Aligned 1:1 with DXPrimitiveGroup:
///   - Static: camera, shared lighting UBOs (N-buffered), and the dummy identity-bone UBO used by Mesh3D
///   - Instance: Matrix/Material UBO creation, AllocateAndWriteDescriptorSets, SyncAlpha,
///     and three-bucket grouped drawing
/// Derived differences:
///   - Geometry/material source (VKModel uses the glTF node tree, VKMesh3D uses Mesh3D.Surfaces)
///   - Whether bones exist (VKModel overrides BoneMatrixBuffers / uploads bone matrices in OnBeforeDraw)
/// </summary>
internal unsafe abstract class VKPrimitiveGroup : IDisposable
{
    /// <summary>
    /// Placeholder size kept for compatibility with the legacy b3 BoneMatrices UBO.
    /// The real skinned path has been unified on a dynamic bone storage buffer,
    /// so this no longer defines the model bone-count limit.
    /// </summary>
    public const int MaxBones = 100;

    // Global shared state: camera for all Pbr3D primitives
    internal static Season.Basic.Camera Camera;

    // Global shared state: lighting UBOs (N-buffered)
    internal static BufferResource[] LightConstantBuffers = null!;

    static byte*[] _mappedLightConstantBuffers = null!;

    // Global shared state: "identity bone" UBO shared by non-skinned primitive groups such as Mesh3D
    // (bound to b3 as a dummy buffer)
    internal static BufferResource[] IdentityBoneBuffers = null!;

    static byte*[] _mappedIdentityBoneBuffers = null!;

    // 2-4 clause 10: DDGI irradiance atlas for the current frame
    // (compute 2D texture, binding 17).
    // SetLighting resolves it once per frame, mirroring VKTextureCube.Active;
    // when null (feature disabled or not ready), Bound falls back to Device.White,
    // and actual sampling is gated in the shader by DDGI_ENABLED + giParams.
    internal static Texture? DdgiAtlasActive;

    /// <summary>2-4 clause 10: atlas to bind to binding 17 for the current frame
    /// (prefer Active, otherwise fall back to White). Never null.</summary>
    internal static Texture DdgiAtlasBound => DdgiAtlasActive ?? Device.White;

    /// <summary>2-4 clause 10: descriptor binding index for the DDGI irradiance atlas
    /// (GLSL `sampler2D ddgiAtlas`).</summary>
    internal const uint DdgiAtlasBinding = 17;

    // 2-4 Step 3: DDGI depth-moment atlas for the current frame
    // (compute 2D texture rg16float, binding 18).
    // Follows the same pattern as DdgiAtlasActive: resolved in SetLighting each frame,
    // falls back to Device.White when null,
    // and actual sampling is runtime-gated by giParams2.y.
    internal static Texture? DdgiDepthActive;

    /// <summary>2-4 Step 3: depth atlas to bind to binding 18 for the current frame
    /// (prefer Active, otherwise fall back to White). Never null.</summary>
    internal static Texture DdgiDepthBound => DdgiDepthActive ?? Device.White;

    /// <summary>2-4 Step 3: descriptor binding index for the DDGI depth-moment atlas
    /// (GLSL `sampler2D ddgiDepth`).</summary>
    internal const uint DdgiDepthBinding = 18;

    // 2-5 Step C: cloud noise for the current frame
    // (compute 2D texture rgba8unorm, binding 19).
    // Same pattern as DdgiAtlasActive: resolved in SetLighting each frame;
    // when null (feature disabled or not ready), Bound falls back to Device.White.
    // Actual sampling is runtime-gated by cloudParams0.w (layer count);
    // a full-white fallback must not be treated as real noise, or density would max out.
    internal static Texture? CloudNoiseActive;

    /// <summary>2-5 Step C: cloud noise to bind to binding 19 for the current frame
    /// (prefer Active, otherwise fall back to White). Never null.</summary>
    internal static Texture CloudNoiseBound => CloudNoiseActive ?? Device.White;

    /// <summary>2-5 Step C: descriptor binding index for cloud noise
    /// (GLSL `sampler2D cloudNoise`, wrap immutable).</summary>
    internal const uint CloudNoiseBinding = 19;

    // 2-5 Step E: aerial-perspective froxel volume for the current frame
    // (compute 3D texture rgba16float, binding 20).
    // Resolution goes through the VKTexture3D static dictionary
    // (3D and 2D dictionaries are separate, matching DXTexture3D.Find semantics; see 1-8).
    // When null, Bound falls back to VKTexture3D.DummyBlack
    // (the identity element for additive composition); apParams0.x only gates sampling.
    internal static VKTexture3D? AerialLutActive;

    /// <summary>2-5 Step E: AP volume to bind to binding 20 for the current frame
    /// (prefer Active, otherwise fall back to DummyBlack). Never null.</summary>
    internal static VKTexture3D AerialLutBound => AerialLutActive ?? VKTexture3D.DummyBlack;

    /// <summary>2-5 Step E: descriptor binding index for the AP 3D LUT
    /// (GLSL `sampler3D aerialLut`, linear-clamp immutable).</summary>
    internal const uint AerialLutBinding = 20;

    /// <summary>Refresh binding 17 of <paramref name="set"/> to the current view of
    /// <see cref="DdgiAtlasBound"/>.
    /// Only updates when ViewVersion changes
    /// (compare version instead of handle, for the same reason as VKTextureCube.RefreshBinding).
    /// The atlas ping-pongs frame by frame, so the version almost always changes;
    /// therefore the current frame slot is refreshed every frame.
    /// After disabling the feature and falling back to White, the version converges as well.</summary>
    internal static void RefreshDdgiBinding(DescriptorSet set, ref ulong cachedVersion)
    {
        var atlas = DdgiAtlasBound;
        if (atlas.ViewVersion == cachedVersion)
            return;
        var info = new DescriptorImageInfo
        { ImageView = atlas.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = DdgiAtlasBinding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
        cachedVersion = atlas.ViewVersion;
    }

    /// <summary>2-4 Step 3: refresh binding 18 of <paramref name="set"/> to the current view of
    /// <see cref="DdgiDepthBound"/>.
    /// Follows the same pattern as <see cref="RefreshDdgiBinding"/>
    /// (only updates when ViewVersion changes; the depth atlas ping-pongs in sync).</summary>
    internal static void RefreshDdgiDepthBinding(DescriptorSet set, ref ulong cachedVersion)
    {
        var atlas = DdgiDepthBound;
        if (atlas.ViewVersion == cachedVersion)
            return;
        var info = new DescriptorImageInfo
        { ImageView = atlas.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = DdgiDepthBinding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
        cachedVersion = atlas.ViewVersion;
    }

    /// <summary>2-5 Step C: refresh binding 19 of <paramref name="set"/> to the current view of
    /// <see cref="CloudNoiseBound"/>.
    /// Follows the same pattern as <see cref="RefreshDdgiBinding"/>
    /// (only updates when ViewVersion changes).
    /// Cloud noise is baked only once in its lifetime, so usually only the first frame updates it;
    /// after Dispose or quality downgrade, Active falls back to null, Bound switches to White,
    /// and the version becomes inactive accordingly.</summary>
    internal static void RefreshCloudNoiseBinding(DescriptorSet set, ref ulong cachedVersion)
    {
        var noise = CloudNoiseBound;
        if (noise.ViewVersion == cachedVersion)
            return;
        var info = new DescriptorImageInfo
        { ImageView = noise.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = CloudNoiseBinding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
        cachedVersion = noise.ViewVersion;
    }

    /// <summary>2-5 Step E: refresh binding 20 of <paramref name="set"/> to the current view of
    /// <see cref="AerialLutBound"/>.
    /// Follows the same pattern as <see cref="RefreshCloudNoiseBinding"/>.
    /// The AP volume is also baked only once in its lifetime;
    /// when not ready, Bound falls back to DummyBlack with an unchanged version,
    /// so it converges after a single write.</summary>
    internal static void RefreshAerialLutBinding(DescriptorSet set, ref ulong cachedVersion)
    {
        var lut = AerialLutBound;
        if (lut.ViewVersion == cachedVersion)
            return;
        var info = new DescriptorImageInfo
        { ImageView = lut.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = AerialLutBinding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };
        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 1, &write, 0, null);
        cachedVersion = lut.ViewVersion;
    }
    internal string Name = string.Empty;

    /// <summary>Most recent overall Alpha written into the material buffer,
    /// used to drive PSO three-bucket grouping.</summary>
    protected float _currentAlpha = 1.0f;

    /// <summary>Most recent color multiplier written into the material buffer
    /// (used for Mesh3D.ColorTint synchronization, rewritten only when changed).</summary>
    protected Vector4 _currentColorTint = Vector4.One;

    /// <summary>Whether the first Update has completed.
    /// When false, Draw skips directly to avoid rendering with identity matrices.</summary>
    protected bool _transformInitialized;

    /// <summary>Reusable draw list to avoid GC from allocating a new List every frame.</summary>
    readonly List<PrimitiveData> _drawList = new(64);

    /// <summary>1-5: primitive list projected in the current shadow pass.
    /// It is separate from the main-pass _drawList, so the two chains never overwrite each other.
    /// The same list is replayed per slot across the four atlas quadrants,
    /// and invalidated by <see cref="Season.Rendering.CascadedShadow.Epoch"/>; see DrawShadow.
    /// Mirrors DX-side _shadowDrawList 1:1.</summary>
    readonly List<PrimitiveData> _shadowDrawList = new(64);

    /// <summary>Generation number already collected into _shadowDrawList
    /// (`int.MinValue` means never collected).</summary>
    int _shadowDrawListEpoch = int.MinValue;

    // Unified highlighting: Bounds-box state
    // (host box + instance box pool, lazily created; Wireframe shell state added in Phase 3)

    /// <summary>Whether the host (non-instanced) Bounds box is enabled in this frame
    /// (written during Update, zero-cost gated during Draw).</summary>
    protected bool _boundsActive;

    /// <summary>Host (non-instanced) Bounds box; lazily created on the first enabled frame
    /// and then kept resident.</summary>
    protected HighlightBox _boundsBox = null!;

    /// <summary>Per-instance Bounds-box pool
    /// (indexed by compressed writeIndex; lazily grows and remains resident until the group is released).</summary>
    protected readonly List<HighlightBox> _instanceBoundsBoxes = new();

    /// <summary>Compressed instance indices that enabled Bounds boxes in this frame
    /// (rebuilt every Update, drawn box by box during Draw using this list).</summary>
    protected readonly List<int> _boundsBoxDrawList = new();

    // Unified highlighting: Wireframe shell state
    // (non-instanced per-primitive boxes + instanced shared templates, lazily created)

    /// <summary>Whether host (non-instanced) Wireframe is enabled in this frame
    /// (written during Update, zero-cost gated during Draw).</summary>
    protected bool _wireframeEnabled;

    /// <summary>Non-instanced per-primitive Wireframe shell boxes
    /// (aligned with CollectPrimitives order; primitives without valid triangles use null placeholders;
    /// lazily created on the first enabled frame and then kept resident,
    /// with no rebuild/release on runtime toggles).</summary>
    protected List<HighlightBox?>? _wireframeBoxes;

    /// <summary>Instanced shared shell template
    /// (shell faces/edge strips merged from all non-skinned, non-morph primitives;
    /// lazily created on the first enabled frame and then kept resident;
    /// per-instance shell boxes share its VB/IB).</summary>
    protected HighlightBox? _shellGeometry;

    /// <summary>Shared skinned shell geometry for instanced templates
    /// (merged from all skinned primitives that share the same Skin;
    /// IsSkinned=1 uses the per-instance bone-palette path and matches animation exactly
    /// through the same VS skinning path as the main pass).
    /// Lazily created once on the first enabled frame and then kept resident.
    /// Multi-skin assets (each node has its own Skin) are skipped in Phase 1,
    /// so this remains null and retries on later frames.</summary>
    protected HighlightBox? _skinnedShellGeometry;

    /// <summary>Per-instance Wireframe shell-box pool
    /// (indexed by compressed writeIndex; lazily grows, shares template geometry,
    /// creates its own Matrix/Material UBOs, and uses null placeholders while the template is not ready).</summary>
    protected readonly List<HighlightBox?> _instanceShellBoxes = new();

    /// <summary>Per-instance skinned Wireframe shell-box pool
    /// (shares skinned template geometry; uses the same index space as _instanceShellBoxes;
    /// in mixed assets, the same writeIndex can hold one box in each pool and both are drawn).</summary>
    protected readonly List<HighlightBox?> _skinnedInstanceShellBoxes = new();

    /// <summary>Compressed instance indices that enabled Wireframe in this frame
    /// (rebuilt every Update, drawn box by box during Draw using this list).</summary>
    protected readonly List<int> _shellBoxDrawList = new();

    /// <summary>edgeWidth used for the most recent shell-geometry build.
    /// Compared against the host Highlight.EdgeWidth; if changed, release and rebuild.</summary>
    protected float _builtShellEdgeWidth;

    // Unified highlighting: Outline2D state
    // (mask pass; activation is collected by a separate Graphics pass)

    /// <summary>Whether the Outline2D mask is active in this frame
    /// (written during Update, zero-cost gated during DrawOutlineMask).</summary>
    protected bool _outline2DActive;

    /// <summary>Group-level outline color for this frame
    /// (written group by group into the mask; for multi-color frames,
    /// the composite pass picks colors per pixel).</summary>
    protected Vector4 _outline2DColor;

    /// <summary>Group-level outline width for this frame
    /// (frame-level aggregation takes the maximum to ensure the widest outline stays fully visible).</summary>
    protected float _outline2DWidth;

    /// <summary>Unified entry point for Outline2D state
    /// (called by derived Update methods after host/instance aggregation,
    /// mirroring DX `SetOutline2DState`).</summary>
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
    // Static: lighting / identity-bone UBO lifetime + global camera/lighting updates
    // ============================================================

    public static void InitLights()
    {
        int n = (int)Device.frameCount;
        LightConstantBuffers = new BufferResource[n];
        _mappedLightConstantBuffers = new byte*[n];

        for (int i = 0; i < n; i++)
        {
            LightConstantBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<SceneLightParams>(),
                out _mappedLightConstantBuffers[i]);
        }

        var defaultLight = new SceneLightParams
        {
            CameraPos = new Vector4(0, 0, -1, 1),
            Ambient = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            Params0 = new Vector4(0, Device.HdrExposure, 0, 0),
        };
        for (int i = 0; i < n; i++)
            Unsafe.Write(_mappedLightConstantBuffers[i], defaultLight);

        // Identity-bone UBO (100 64B matrices = 6400B), filled with Identity on all frames
        IdentityBoneBuffers = new BufferResource[n];
        _mappedIdentityBoneBuffers = new byte*[n];
        ulong boneSize = (ulong)(Unsafe.SizeOf<Matrix4x4>() * MaxBones);
        var identity = Matrix4x4.Identity;

        for (int i = 0; i < n; i++)
        {
            IdentityBoneBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)boneSize, out _mappedIdentityBoneBuffers[i]);
            for (int j = 0; j < MaxBones; j++)
                Unsafe.Write(_mappedIdentityBoneBuffers[i] + j * sizeof(float) * 16, identity);
        }
    }

    public static void InitLightsDispose()
    {
        var vk = Device.Vk;
        var d = Device.LogicalDevice;
        var rm = Device.ResourceManager;

        if (LightConstantBuffers != null)
        {
            for (int i = 0; i < LightConstantBuffers.Length; i++)
            {
                if (LightConstantBuffers[i].Memory.Handle != 0)
                    vk.UnmapMemory(d, LightConstantBuffers[i].Memory);
                rm?.DestroyBuffer(LightConstantBuffers[i]);
            }
            LightConstantBuffers = null!;
            _mappedLightConstantBuffers = null!;
        }

        if (IdentityBoneBuffers != null)
        {
            for (int i = 0; i < IdentityBoneBuffers.Length; i++)
            {
                if (IdentityBoneBuffers[i].Memory.Handle != 0)
                    vk.UnmapMemory(d, IdentityBoneBuffers[i].Memory);
                rm?.DestroyBuffer(IdentityBoneBuffers[i]);
            }
            IdentityBoneBuffers = null!;
            _mappedIdentityBoneBuffers = null!;
        }
    }

    /// <summary>Write the lighting UBO for the current frame
    /// (1-2 layout `SceneLightParams`, 976B).
    /// `Params0.Y` carries HDR exposure (shader-side `params0.y`, used for text inverse-ACES compensation),
    /// and `VelocityParams` carries the current-frame subpixel jitter plus inverse screen size
    /// (2-3 contract clause 6).
    /// Both are injected once per frame at a single point; writes from the app side are ineffective.</summary>
    public static void SetLighting(SceneLightParams lightParams)
    {
        int fi = (int)Device.FrameIndex;
        lightParams.Params0.Y = Device.HdrExposure;

        // 2-3 contract clause 6:
        // xy = current-frame jitter (NDC), zw = 1 / screen size
        // (used by PS to reconstruct NDC from SV_Position).
        // When MotionVectors are disabled, JitterNdc stays zero,
        // and writing it is harmless because shaders with VELOCITY_OUTPUT=0 do not read this field.
        var res = DeviceServices.BaseApp.DeviceResolution;
        var jitter = DeviceServices.BaseApp.Camera.JitterNdc;
        lightParams.VelocityParams = new Vector4(
            jitter.X, jitter.Y,
            res.X > 0 ? 1f / res.X : 0f,
            res.Y > 0 ? 1f / res.Y : 0f);

        // 1-7 contract clause 4:
        // inject environment parameters + resolve the current-frame radiance cube once per frame,
        // so DrawPrimitive avoids a lookup per draw.
        // When SceneEnvironment is null, EnvParams stays all zero,
        // and the shader falls back per pixel to the 1-2 constant ambient term.
        var env = DeviceServices.BaseApp.SceneEnvironment;
        env?.Apply(ref lightParams);
        VKTextureCube.Active = env != null ? VKTextureCube.Find(env.RadianceName) : null;

        // 2-4 clause 10: single-point injection of DDGI GiParams0/1/2
        // (not written when not ready; consumers fall back).
        Season.Rendering.Effects.DdgiEffect.Apply(ref lightParams);

        // 2-5 Step B (b11): single-point injection of SkyParams0..3
        // for sun/moon disks + star field.
        // In the StaticCube tier, the whole path returns early,
        // so the four fields stay all zero and the PS gate `skyParams0.w > 0` stays false with no residue.
        Season.Rendering.SkyLighting.Apply(ref lightParams);

        // 2-4 clause 10: resolve the current-frame DDGI irradiance atlas once per frame
        // (mirrors VKTextureCube.Active).
        // When not ready it stays null, binding 17 falls back to Device.White,
        // and actual sampling is gated by DDGI_ENABLED.
        DdgiAtlasActive = Season.Rendering.Effects.DdgiEffect.Ready
            ? Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveIrradianceName)
            : null;

        // 2-4 Step 3: resolve the current-frame DDGI depth-moment atlas once per frame,
        // following the same pattern as irradiance.
        // When not ready it stays null, binding 18 falls back to Device.White,
        // and Chebyshev sampling is runtime-gated by giParams2.y.
        DdgiDepthActive = Season.Rendering.Effects.DdgiEffect.Ready
            ? Graphics.FindDdgiAtlas(Season.Rendering.Effects.DdgiEffect.ActiveDepthName)
            : null;

        // 2-5 Step C: resolve the current-frame cloud noise
        // (through the same 2D compute dictionary, once per frame).
        // It is baked only once in its lifetime, but still resolved every frame:
        // after Dispose or quality downgrade, when FrameSchedule.CloudNoiseTexture becomes null,
        // Active must be cleared in sync, otherwise an already-freed texture handle would keep being bound
        // (same note as on the DX side).
        // When null, binding 19 falls back to Device.White,
        // and actual sampling is gated by cloudParams0.w (layer count) -
        // SkyLighting.Apply writes the layer count only when the name is non-null.
        CloudNoiseActive = Season.Rendering.FrameSchedule.CloudNoiseTexture is string cloudNoiseName
            ? Graphics.FindDdgiAtlas(cloudNoiseName)
            : null;

        // 2-5 Step E: resolve the current-frame aerial-perspective volume.
        // This does not use FindDdgiAtlas:
        // compute 3D textures are registered in VKTexture3D's own static dictionary
        // (isolated from the 2D DictionaryVKTexture; see 1-8), so only Find can be used here.
        // It is also resolved every frame:
        // after quality downgrade or Dispose, when FrameSchedule.AerialLutTexture becomes null,
        // Active must be cleared in sync.
        AerialLutActive = Season.Rendering.FrameSchedule.AerialLutTexture is string aerialName
            ? VKTexture3D.Find(aerialName)
            : null;

        Unsafe.Write(_mappedLightConstantBuffers[fi], lightParams);
    }

    /// <summary>Called once per frame from the main loop:
    /// refresh camera view/projection and write the lighting UBO.</summary>
    public static void Update(float time, Vector3 cameraPos, Vector3 cameraTarget, SceneLightParams lightParams)
    {
        // 1-3: matrix construction is unified through the shared Camera3D layer
        // (Changed-gated, zero rebuild for a stationary camera; FOV/near/far are driven by BaseApp.Camera).
        // The cameraPos/cameraTarget parameters are forwarded from BaseApp.Camera.Position/Target;
        // the signature is kept for frame-loop compatibility.
        var camera3D = DeviceServices.BaseApp.Camera;
        var aspectRatio = DeviceServices.BaseApp.DeviceResolution.X / (float)DeviceServices.BaseApp.DeviceResolution.Y;

        if (RenderQuality.Current.MotionVectors)
        {
            // 2-3 contract clause 4: the only injection point for jitter.
            // UpdateTemporal first snapshots the previous frame's unjittered ViewProjection,
            // then rebuilds matrices and bakes jitter only into ProjectionJittered.
            // Frustum culling and CSM cascades still use the unjittered
            // camera3D.Projection/ViewProjection to avoid edge flicker and shadow jitter.
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
            // All zero = no history; even if the feature is turned off mid-run,
            // no stale matrix is left behind
            Camera.PrevViewProjection = default;
        }

        // 1-5: CPU shadow-matrix computation chain
        // (after the camera update and before writing the lighting UBO;
        // Apply writes zero when the feature is disabled or there is no active light).
        // Shadow sources are selected by indices stored in Params0.Z/W
        // (written by the authorization-layer bake), and type dispatch is resolved here at a single point.
        // Mirrors the DX side 1:1.
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

        // Must go through SetLighting, which includes HdrExposure injection.
        // Writing the UBO directly would make the shader read params0.y = 0,
        // causing text inverse-ACES compensation to divide by 1e-4
        // and saturate all text to white
        // (same failure seen on the DX side, RenderQuality contract 5).
        SetLighting(lightParams);
    }

    // ============================================================
    // Instance: UBO creation (called by derived classes during PrimitiveData initialization)
    // ============================================================

    protected void CreateMatrixBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MatrixBuffers = new BufferResource[n];
        primitiveData.MappedMatrixBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MatrixBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MatrixBuffer>(),
                out primitiveData.MappedMatrixBuffers[i]);
    }

    protected void CreateMaterialBuffer(PrimitiveData primitiveData)
    {
        int n = (int)Device.frameCount;
        primitiveData.MaterialBuffers = new BufferResource[n];
        primitiveData.MappedMaterialBuffers = new byte*[n];
        for (int i = 0; i < n; i++)
            primitiveData.MaterialBuffers[i] = Device.ResourceManager.CreateConstantBuffer(
                (uint)Unsafe.SizeOf<MaterialParams>(),
                out primitiveData.MappedMaterialBuffers[i]);
    }

    /// <summary>Derived override: return the bone-UBO frame ring used by this primitive group.
    /// Defaults to the global Identity buffers.</summary>
    protected virtual BufferResource[] BoneMatrixBuffers => IdentityBoneBuffers;

    /// <summary>Derived override: return the storage-buffer frame ring used for instanced skinning.
    /// Defaults to the global Identity buffers.</summary>
    protected virtual BufferResource[] InstanceBoneBuffers => Pipeline.IdentityInstanceBoneBuffers;

    protected virtual BufferResource[] MorphDeltasBuffers => Pipeline.DefaultMorphDeltasBuffers;

    /// <summary>
    /// Allocate one DescriptorSet per frame for the primitive and write it once:
    ///   binding 0: matrix UBO[fi]
    ///   binding 1: light UBO[fi]
    ///   binding 2: material UBO[fi]
    ///   binding 3: bone UBO[fi] (falls back to global Identity when no bones exist)
    ///   binding 4..8: five PBR texture ImageViews (CombinedImageSampler, sampler provided by PImmutableSamplers)
    ///   binding 9: instanced bone storage buffer[fi] (falls back to global Identity when no instance bones exist)
    ///   binding 10: morph-delta storage buffer (falls back to the global zero buffer when there is no morph)
    ///   binding 11: shared TextDrawParams UBO
    ///   binding 12: shadow atlas (when shadows are enabled)
    ///   binding 13/14/15: previous-frame SSBO data
    ///     (prev bones / instanceWorld / morphWeights, defaulting to zero placeholders)
    ///   binding 16 (1-7): environment radiance cube (1x1 all-black dummy when no environment map exists)
    ///   binding 17 (2-4): DDGI irradiance atlas (falls back to White when not ready)
    ///   binding 18 (2-4 Step 3): DDGI depth-moment atlas (falls back to White when not ready)
    ///   binding 19 (2-5 Step C): cloud noise (falls back to White when not ready, gated by layer count)
    ///   binding 20 (2-5 Step E): AP 3D LUT (falls back to 1x1x1 DummyBlack when not ready)
    /// </summary>
    protected void AllocateAndWriteDescriptorSets(PrimitiveData p)
    {
        int n = (int)Device.frameCount;
        p.DescriptorSets = new DescriptorSet[n];
        // 1-7: binding 16 version cache aligned with DescriptorSets
        // (all zero = not written yet; WriteDescriptorSet fills it with the cube version written this time)
        p.EnvCubeViewVersions = new ulong[n];
        // 2-4 clause 10: binding 17 version cache aligned with DescriptorSets
        // (same semantics as EnvCubeViewVersions).
        p.DdgiAtlasViewVersions = new ulong[n];
        // 2-4 Step 3: binding 18 depth-atlas version cache aligned with DescriptorSets
        // (same semantics).
        p.DdgiDepthViewVersions = new ulong[n];
        // 2-5 Step C: binding 19 cloud-noise version cache aligned with DescriptorSets
        // (same semantics).
        p.CloudNoiseViewVersions = new ulong[n];
        // 2-5 Step E: binding 20 AP-volume version cache aligned with DescriptorSets
        // (same semantics).
        p.AerialLutViewVersions = new ulong[n];
        var bones = BoneMatrixBuffers;
        var instanceBones = InstanceBoneBuffers;
        var morphDeltas = MorphDeltasBuffers;

        for (int fi = 0; fi < n; fi++)
        {
            p.DescriptorSets[fi] = Device.DescriptorAllocator.AllocateSet(Pipeline.SetLayout);
            WriteDescriptorSet(p, fi, bones, instanceBones, morphDeltas);
        }
    }

    protected void RewriteDescriptorSets(PrimitiveData p)
    {
        if (p.DescriptorSets == null)
            return;

        var bones = BoneMatrixBuffers;
        var instanceBones = InstanceBoneBuffers;
        var morphDeltas = MorphDeltasBuffers;
        for (int fi = 0; fi < p.DescriptorSets.Length; fi++)
            WriteDescriptorSet(p, fi, bones, instanceBones, morphDeltas);
    }

    /// <summary>Rewrite DescriptorSets for all shell primitives
    /// (both shared templates + both instance-box pools).
    /// After bone-buffer resize/grow, shell descriptor sets may still point to old buffers
    /// and read freed memory (plan risk 3), so they must be rewritten in the same batch
    /// as the main primitives.
    /// The binding 13/14/15 switch after prev SSBOs become ready is synchronized here as well.
    /// Safe when templates are not ready because RewriteDescriptorSets exits early internally.</summary>
    protected void RewriteShellDescriptorSets()
    {
        if (_shellGeometry != null)
            RewriteShellBoxDescriptorSets(_shellGeometry);
        if (_skinnedShellGeometry != null)
            RewriteShellBoxDescriptorSets(_skinnedShellGeometry);
        foreach (var box in _instanceShellBoxes)
            if (box != null)
                RewriteShellBoxDescriptorSets(box);
        foreach (var box in _skinnedInstanceShellBoxes)
            if (box != null)
                RewriteShellBoxDescriptorSets(box);
    }

    void RewriteShellBoxDescriptorSets(HighlightBox box)
    {
        RewriteDescriptorSets(box.Face);
        RewriteDescriptorSets(box.Edges);
    }

    void WriteDescriptorSet(PrimitiveData p, int fi, BufferResource[] bones, BufferResource[] instanceBones, BufferResource[] morphDeltas)
    {
        var matrixInfo = new DescriptorBufferInfo
        { Buffer = p.MatrixBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var lightInfo = new DescriptorBufferInfo
        { Buffer = LightConstantBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var materialInfo = new DescriptorBufferInfo
        { Buffer = p.MaterialBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var boneInfo = new DescriptorBufferInfo
        { Buffer = bones[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var instanceBoneInfo = new DescriptorBufferInfo
        { Buffer = instanceBones[fi].Buffer, Offset = 0, Range = Vk.WholeSize };
        var morphInfo = new DescriptorBufferInfo
        {
            Buffer = p.MorphDeltasBuffer.Buffer.Handle != 0 ? p.MorphDeltasBuffer.Buffer : morphDeltas[fi].Buffer,
            Offset = 0,
            Range = Vk.WholeSize
        };
        var textDrawParamsInfo = new DescriptorBufferInfo
        { Buffer = Pipeline.DefaultTextDrawParamsBuffer.Buffer, Offset = 0, Range = Vk.WholeSize };

        var imgInfos = stackalloc DescriptorImageInfo[5];
        imgInfos[0] = new DescriptorImageInfo
        { ImageView = p.BaseColorTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[1] = new DescriptorImageInfo
        { ImageView = p.NormalTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[2] = new DescriptorImageInfo
        { ImageView = p.MetallicRoughnessTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[3] = new DescriptorImageInfo
        { ImageView = p.OcclusionTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        imgInfos[4] = new DescriptorImageInfo
        { ImageView = p.EmissiveTexture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var set = p.DescriptorSets[fi];
        var writes = stackalloc WriteDescriptorSet[21];
        writes[0] = MakeBufferWrite(set, 0, DescriptorType.UniformBuffer, &matrixInfo);
        writes[1] = MakeBufferWrite(set, 1, DescriptorType.UniformBuffer, &lightInfo);
        writes[2] = MakeBufferWrite(set, 2, DescriptorType.UniformBuffer, &materialInfo);
        writes[3] = MakeBufferWrite(set, 3, DescriptorType.UniformBuffer, &boneInfo);
        for (int i = 0; i < 5; i++)
            writes[4 + i] = MakeImageWrite(set, (uint)(4 + i), imgInfos + i);
        writes[9] = MakeBufferWrite(set, 9, DescriptorType.StorageBuffer, &instanceBoneInfo);
        writes[10] = MakeBufferWrite(set, 10, DescriptorType.StorageBuffer, &morphInfo);
        writes[11] = MakeBufferWrite(set, 11, DescriptorType.UniformBuffer, &textDrawParamsInfo);

        // 1-5: binding 12 shadow atlas.
        // ShadowMap is created during app initialization before any model is loaded
        // (same contract as WindowsApp), so it must be non-null when ShadowsEnabled is true.
        // The comparison sampler is immutable and already part of the layout,
        // so only DepthView is provided here.
        // When shadows are disabled, ShadowMap is null, so a placeholder must be supplied:
        // stackalloc memory is not zero-initialized,
        // and an unwritten writes[12] would contain stack garbage,
        // causing the WSL Vulkan driver to abort on an invalid descriptor.
        var fallback = Device.White;
        var shadowInfo = default(DescriptorImageInfo);
        if (FrameSchedule.ShadowMap is VKRenderTarget shadowRt && shadowRt.DepthView.Handle != 0)
        {
            shadowInfo = new DescriptorImageInfo
            { ImageView = shadowRt.DepthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        else
        {
            // Shadows disabled: fill binding 12 with the White placeholder texture
            // to avoid UB from stack garbage
            shadowInfo = new DescriptorImageInfo
            { ImageView = fallback.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }
        writes[12] = MakeImageWrite(set, 12, &shadowInfo);

        // 2-3 Step C: previous-frame SSBO data (binding 13/14/15).
        // Derived classes can override this to provide the actual prev buffers.
        // The base class returns default zero-value placeholders;
        // VKInstancedModel overrides them with the real prev bone/morph/instanceWorld SBs.
        var prevBoneInfo = GetPrevBoneBufferInfo(fi);
        var prevInstanceWorldInfo = GetPrevInstanceWorldBufferInfo(fi);
        var prevMorphWeightsInfo = GetPrevMorphWeightsBufferInfo(fi);
        writes[13] = MakeBufferWrite(set, 13, DescriptorType.StorageBuffer, &prevBoneInfo);
        writes[14] = MakeBufferWrite(set, 14, DescriptorType.StorageBuffer, &prevInstanceWorldInfo);
        writes[15] = MakeBufferWrite(set, 15, DescriptorType.StorageBuffer, &prevMorphWeightsInfo);

        // 1-7: binding 16 environment radiance cube.
        // Pipeline.Init has already prebuilt DummyBlack, so Bound is never null,
        // and this descriptor is always valid
        // (same reason as above: writes is not zero-initialized, so leaving it empty would mean stack garbage).
        // The written version is synchronized into EnvCubeViewVersions[fi],
        // so DrawPrimitive will not refresh the same cube again every frame.
        var envCube = VKTextureCube.Bound;
        var envCubeInfo = new DescriptorImageInfo
        { ImageView = envCube.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[16] = MakeImageWrite(set, VKTextureCube.EnvCubeBinding, &envCubeInfo);
        if (p.EnvCubeViewVersions != null)
            p.EnvCubeViewVersions[fi] = envCube.ViewVersion;

        // 2-4 clause 10: binding 17 DDGI irradiance atlas.
        // Same pattern as envCube: the shader references it statically,
        // so a valid placeholder must be written when it is not ready
        // (Bound falls back to White and is never null;
        // leaving it empty would mean stack garbage -> WSL abort).
        var ddgiAtlas = DdgiAtlasBound;
        var ddgiInfo = new DescriptorImageInfo
        { ImageView = ddgiAtlas.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[17] = MakeImageWrite(set, DdgiAtlasBinding, &ddgiInfo);
        if (p.DdgiAtlasViewVersions != null)
            p.DdgiAtlasViewVersions[fi] = ddgiAtlas.ViewVersion;

        // 2-4 Step 3: binding 18 DDGI depth-moment atlas.
        // Same pattern as binding 17
        // (Bound falls back to White, is never null,
        // and leaving it empty would mean stack garbage -> WSL abort).
        // Actual Chebyshev sampling is runtime-gated by giParams2.y.
        var ddgiDepth = DdgiDepthBound;
        var ddgiDepthInfo = new DescriptorImageInfo
        { ImageView = ddgiDepth.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[18] = MakeImageWrite(set, DdgiDepthBinding, &ddgiDepthInfo);
        if (p.DdgiDepthViewVersions != null)
            p.DdgiDepthViewVersions[fi] = ddgiDepth.ViewVersion;

        // 2-5 Step C: binding 19 cloud noise.
        // Same pattern as binding 17
        // (Bound falls back to White, is never null,
        // and leaving it empty would mean stack garbage -> WSL abort).
        // Actual sampling is runtime-gated by cloudParams0.w (layer count).
        var cloudNoise = CloudNoiseBound;
        var cloudNoiseInfo = new DescriptorImageInfo
        { ImageView = cloudNoise.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[19] = MakeImageWrite(set, CloudNoiseBinding, &cloudNoiseInfo);
        if (p.CloudNoiseViewVersions != null)
            p.CloudNoiseViewVersions[fi] = cloudNoise.ViewVersion;

        // 2-5 Step E: binding 20 aerial-perspective 3D LUT.
        // Same pattern as binding 17
        // (Bound falls back to DummyBlack, is never null,
        // and leaving it empty would mean stack garbage -> WSL abort).
        // Actual sampling is runtime-gated by apParams0.x (only skipping sampling).
        var aerialLut = AerialLutBound;
        var aerialLutInfo = new DescriptorImageInfo
        { ImageView = aerialLut.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        writes[20] = MakeImageWrite(set, AerialLutBinding, &aerialLutInfo);
        if (p.AerialLutViewVersions != null)
            p.AerialLutViewVersions[fi] = aerialLut.ViewVersion;

        Device.Vk.UpdateDescriptorSets(Device.LogicalDevice, 21, writes, 0, null);
    }

    /// <summary>2-3 Step C: derived classes may override this to return the prev bone SSBO
    /// for binding 13. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevBoneBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevBoneBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    /// <summary>2-3 Step C: derived classes may override this to return the prev instanceWorld SSBO
    /// for binding 14. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevInstanceWorldBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevInstanceWorldBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    /// <summary>2-3 Step C: derived classes may override this to return the prev morphWeights SSBO
    /// for binding 15. The base class returns the default zero-value buffer.</summary>
    protected virtual DescriptorBufferInfo GetPrevMorphWeightsBufferInfo(int fi)
        => new() { Buffer = Pipeline.DefaultPrevMorphWeightsBuffers[fi].Buffer, Offset = 0, Range = Vk.WholeSize };

    static WriteDescriptorSet MakeBufferWrite(DescriptorSet set, uint binding, DescriptorType descriptorType, DescriptorBufferInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = descriptorType,
            DescriptorCount = 1,
            PBufferInfo = info
        };
    }

    static WriteDescriptorSet MakeImageWrite(DescriptorSet set, uint binding, DescriptorImageInfo* info)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = info
        };
    }

    // ============================================================
    // Instance: Alpha synchronization
    // ============================================================

    /// <summary>
    /// Synchronize the overall alpha to the material buffer of all primitives:
    ///   BaseColor.W = OriginalBaseColorAlpha × alpha
    ///   AlphaCutoff = OriginalAlphaCutoff x alpha
    ///     (MASK scales proportionally to avoid clipping the whole object at low alpha)
    /// Called only when alpha changes; writes all N-buffered frames to avoid flicker from stale values.
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
    /// Synchronize the mesh-level color multiplier to the material buffer of all primitives:
    ///   BaseColor.rgb = OriginalBaseColor.rgb x tint.rgb
    ///     (W is untouched; the Alpha chain is owned exclusively by SyncAlpha)
    /// Called only when tint changes; writes all N-buffered frames to avoid flicker from stale values.
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
    // Unified highlighting: lazy creation of non-instanced Wireframe shell geometry
    // (shared by Mesh3D/Model)
    // ============================================================

    /// <summary>
    /// Unified highlighting: lazily create non-instanced Wireframe highlight boxes at runtime.
    /// On the first frame where Wireframe is enabled, build one box per primitive
    /// (collected through CollectPrimitives and aligned with primitive order;
    /// primitives without valid triangles use null placeholders.
    /// Memory stays at zero while disabled; once built, boxes stay resident and are not rebuilt or released
    /// when toggled at runtime).
    /// Each primitive gets its own box, and skinning parameters
    /// (IsSkinned/BonePaletteStride) are inherited from the source primitive,
    /// so skinned models stay perfectly aligned with animation under the same bone transforms.
    /// Morph-target primitives also build shells:
    /// the shell-delta buffer is expanded according to shell-vertex layout
    /// (the shell-vertex <-> source-vertex index mapping is recorded during construction;
    /// see <see cref="CreateShellBox"/>),
    /// and weights are synchronized with the source every frame
    /// (see VKModel.ApplyMorphTargetsIfNeeded), so the same VS morph path stays perfectly aligned with animation.
    /// edgeWidth is the host Highlight.EdgeWidth (model-size proportion),
    /// and localSizeMax is the host model's largest local dimension (scale baseline).
    /// Per primitive, the baked local thickness is
    /// h = edgeWidth x localSizeMax / node scale
    /// (see <see cref="HighlightGeometry.NodeScaleOf"/>),
    /// so world-space edge width is approximately edgeWidth x the model's largest world dimension,
    /// consistent across assets.
    /// If it no longer matches the host, release and rebuild.
    /// </summary>
    protected void EnsureWireframeHighlights(float edgeWidth, float localSizeMax)
    {
        if (_wireframeBoxes != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release old shell geometry and rebuild at the new width
            // (takes effect immediately this frame)
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
            // Record the node reference with the box
            // (WorldTransform is fetched per frame from the same source as rendering;
            // cloned primitives are collected through CollectPrimitives and share the group's lifetime)
            if (box != null)
                box.OwnerNode = source.OwnerNode;
        }
        _builtShellEdgeWidth = edgeWidth;
    }

    /// <summary>
    /// Unified highlighting: lazily create shared shell geometry for instanced templates.
    /// On the first frame where Wireframe is enabled, build a **rigid shell**
    /// (merged from non-skinned primitives, with VB/IB shared by per-instance boxes)
    /// and a **skinned shell**
    /// (merged from skinned primitives that share the same Skin;
    /// IsSkinned=1 + BonePaletteStride are inherited from source materials,
    /// and the per-instance bone-palette path matches animation exactly).
    /// Mixed assets draw both shells;
    /// pure skinned single-Skin assets output only the skinned shell.
    /// Morph-target primitives are skipped
    /// (morph weights are addressed by instance index and require shell-shape delta buffers,
    /// which merged geometry cannot express).
    /// Documented behavior: for instanced models with morph, Wireframe highlighting covers only the remaining parts;
    /// Bounds style is unaffected.
    /// Multi-skin assets (each node has its own Skin) skip the skinned shell
    /// because a merged template cannot express per-skin palette offsets;
    /// in Phase 2 this is solved once on the CPU by baking per-vertex palette offsets at build time.
    /// When no usable primitive exists, keep null and retry on later frames.
    /// edgeWidth is the host Highlight.EdgeWidth (model-size proportion),
    /// and localSizeMax is the template's largest local dimension.
    /// Per primitive, local thickness is
    /// h = edgeWidth x localSizeMax / node scale
    /// (see <see cref="HighlightGeometry.NodeScaleOf"/>),
    /// so world-space edge width is approximately edgeWidth x the instance's largest world dimension,
    /// consistent across assets.
    /// If it no longer matches the host, release and rebuild.
    /// </summary>
    protected void EnsureShellGeometry(float edgeWidth, float localSizeMax)
    {
        if (_shellGeometry != null || _skinnedShellGeometry != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: release all shared templates and instance shell-box pools
            // (boxes share template geometry), then rebuild at the new width
            // (takes effect immediately this frame)
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
                continue; // Skip morph-target primitives
                          // (instanced merged templates cannot express per-primitive morph sets; see EnsureShellGeometry docs)
            float h = HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, source.OwnerNode);
            if (source.MaterialParams.IsSkinned != 0)
            {
                if (multiSkin)
                    continue; // Multi-skin already detected: skip skinned shells as a whole
                              // (documented in Phase 1)
                // OwnerNode.Skin of primitives with the same Skin is mapped through the same skinMap source
                // to the same cloned reference, so ReferenceEquals is reliable
                var skin = source.OwnerNode?.Skin;
                if (skin == null)
                    continue; // Marked skinned but missing Skin data: defensively skip
                if (sharedSkin == null)
                {
                    sharedSkin = skin;
                }
                else if (!ReferenceEquals(sharedSkin, skin))
                {
                    // Multi-skin asset: discard already accumulated skinned data
                    // and skip the skinned shell as a whole (see docs and plan risk 1)
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
            return; // No usable primitive: keep null and retry on later frames
    
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
    // Instance: material texture replacement
    // (derived classes provide the primitive list through CollectPrimitives)
    // ============================================================

    static Texture GetTextureBySlot(PrimitiveData p, TextureSlot slot) => slot switch
    {
        TextureSlot.BaseColor => p.BaseColorTexture,
        TextureSlot.Normal => p.NormalTexture,
        TextureSlot.MetallicRoughness => p.MetallicRoughnessTexture,
        TextureSlot.Occlusion => p.OcclusionTexture,
        TextureSlot.Emissive => p.EmissiveTexture,
        _ => p.BaseColorTexture
    };

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

    /// <summary>Replace the texture in the specified slot on all Primitives.
    /// The current implementation always uses the recreate path.</summary>
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
            oldTex.UploadPixels(decoder.PixelSpan);
        }
        else
        {
            var newTex = Texture.CreateFromDecoder(decoder);
            foreach (var primitive in _drawList)
            {
                SetTextureBySlot(primitive, slot, newTex);
                AllocateAndWriteDescriptorSets(primitive);
            }
        }
    }

    /// <summary>Write material-parameter overrides into the N-buffered Material UBO
    /// of all Primitives.</summary>
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

    /// <summary>Derived classes append all PrimitiveData that need to be drawn in the current frame to result.</summary>
    protected abstract void CollectPrimitives(List<PrimitiveData> result);

    /// <summary>
    /// Extra hook before Draw.
    /// VKModel overrides this to write bone matrices into the current frame's bone UBO.
    /// The default implementation is empty:
    /// VKMesh3D and other non-skinned primitive groups need no extra work here.
    /// </summary>
    protected virtual void OnBeforeDraw() { }

    // ============================================================
    // Unified highlighting: highlight boxes
    // (face + edge dual-color primitive group, lazily created; no new PSO -
    // faces use Transparent, edges use Opaque)
    // ============================================================

    /// <summary>
    /// Unified highlighting: one highlight box = two PrimitiveData objects, face + edges.
    /// Two geometry flavors exist:
    /// Bounds = unit cube [-0.5, 0.5]^3
    /// (world matrix = Scale(Extents x 2) x Translate(Center));
    /// Wireframe = shell faces + edge strips fitted to the surface
    /// (in model local space, Phase 3).
    /// Faces are semi-transparent Blend and use the Transparent PSO;
    /// edges are solid thin strips using the Opaque PSO with depth writes.
    /// PrevWorld is kept in a CPU shadow copy
    /// (N-buffered UBOs must never be read back),
    /// for TAA / motion-vector velocity fields.
    /// Isomorphic to DX HighlightBox.
    /// </summary>
    protected sealed class HighlightBox
    {
        public PrimitiveData Face;
        public PrimitiveData Edges;

        /// <summary>Face alpha for this frame (`SurfaceColor.W`).
        /// Faces are drawn only when > 0; when = 0 the box becomes edge-only automatically.
        /// Recorded every frame by the Write hook.</summary>
        public float FaceAlpha;

        /// <summary>Box world matrix from the previous frame
        /// (CPU shadow copy; first-frame Identity = zero-velocity sentinel).</summary>
        public Matrix4x4 PrevWorld = Matrix4x4.Identity;

        /// <summary>Host node of the shell source primitive
        /// (recorded for non-instanced per-primitive shell boxes).
        /// Each frame, its WorldTransform x group world matrix is written from the same source as rendering,
        /// so node hierarchy scaling/translation/animation stay perfectly aligned.
        /// null = identity (Mesh3D procedural primitives / instanced shared-template boxes).</summary>
        public GltfNodeBase? OwnerNode;

        /// <summary>Shell source primitive
        /// (recorded for non-instanced per-primitive shell boxes),
        /// used to synchronize morph weights.
        /// When source weights are written, the same weights are synchronized into the Material UBOs
        /// of both shell primitives
        /// (shell delta buffers are expanded by shell-vertex layout and share weights with the source).</summary>
        public PrimitiveData? SourcePrimitive;
    }

    /// <summary>Unified highlighting: lazily create the host Bounds box
    /// (face + edges; called once on the first enabled frame, then kept resident).</summary>
    protected HighlightBox CreateBoundsBox()
    {
        var box = new HighlightBox();
        box.Face = CreateBoxFacePrimitive();
        box.Edges = CreateBoxEdgesPrimitive();
        return box;
    }

    /// <summary>Unified highlighting: get/create the instance Bounds box for the compressed writeIndex
    /// (lazy-growing pool, resident until the group is released).</summary>
    protected HighlightBox AcquireBoundsBox(int index)
    {
        while (_instanceBoundsBoxes.Count <= index)
            _instanceBoundsBoxes.Add(CreateBoundsBox());
        return _instanceBoundsBoxes[index];
    }

    /// <summary>Unified highlighting: build a Wireframe shell highlight box for a single source primitive
    /// (two primitives: shell faces + edge strips).
    /// Vertices are copied field by field from the source vertices, including bone indices/weights,
    /// so skinned models stay perfectly aligned under the same VS skinning path.
    /// Material parameters are copied from the source primitive, then forced to Unlit / transparent or solid;
    /// IsSkinned/BonePaletteStride are inherited as well.
    /// For morph-target source primitives, shell-delta buffers are expanded by shell-vertex layout
    /// (source-vertex indices are recorded per shell vertex during construction),
    /// and weights are shared with the source and synchronized every frame by VKModel,
    /// so the same VS morph path stays perfectly aligned with morph animation.
    /// edgeWidth is the baked local thickness h
    /// (= Highlight.EdgeWidth x the model's largest local dimension / node scale;
    /// see <see cref="HighlightGeometry.NodeScaleOf"/>).
    /// Total edge-strip width = 2 x h, and shell-face extrusion thickness uses the same value.</summary>
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

    /// <summary>Unified highlighting: attach a morph path to shell primitives.
    /// Source deltas are expanded by shell-vertex layout
    /// (the shell-vertex <-> source-vertex index mapping is recorded when the shell is built),
    /// then HasMorphTargets/MorphTargetCount/MorphVertexCount (= shell vertex count) are set
    /// and written back to the Material UBO for all frames.
    /// Weights are shared with the source and synchronized every frame by VKModel.ApplyMorphTargetsIfNeeded.
    /// DescriptorSet binding 10 (morph delta storage buffer) initially points to the default zero buffer
    /// through InitBoundsBoxGpuResources;
    /// after creating a custom delta buffer it must be rewritten
    /// because Vulkan descriptors are fixed at build time, unlike DX-style per-draw SRVs.</summary>
    void AttachShellMorph(PrimitiveData shell, PrimitiveData source, IReadOnlyList<int> sourceIndices)
    {
        shell.MaterialParams.HasMorphTargets = 1;
        shell.MaterialParams.MorphTargetCount = source.MaterialParams.MorphTargetCount;
        shell.MaterialParams.MorphVertexCount = (uint)shell.Vertices.Count;
        for (int i = 0; i < Device.frameCount; i++)
            Unsafe.Write(shell.MappedMaterialBuffers[i], shell.MaterialParams);
        CreateMorphDeltaBuffer(shell, null!, source.MorphTargets!, sourceIndices);
        RewriteDescriptorSets(shell);
    }

    /// <summary>
    /// Phase 3: pack morph-target deltas into `storage buffer<float>` layout
    /// [targetIndex * vertexCount + vertexIndex] * 9 floats = pos.xyz + normal.xyz + tangent.xyz
    /// When vertexMap is non-null, expand by mapping
    /// (for shell-vertex layout: delta of shell vertex v = source delta[vertexMap[v]],
    /// vertex count = vertexMap.Count;
    /// source-primitive path without vertexMap = identity mapping, vertex count = baseVertices.Length).
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

        ulong size = (ulong)(sizeof(float) * totalFloats);
        primitive.MorphDeltasBuffer = Device.ResourceManager.CreateBuffer(
            size,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        void* mapped;
        if (Device.Vk.MapMemory(Device.LogicalDevice, primitive.MorphDeltasBuffer.Memory, 0, size, 0, &mapped) != Result.Success)
            throw new Exception("vkMapMemory (MorphDeltasBuffer) failed");

        fixed (float* src = deltaData)
            Unsafe.CopyBlock(mapped, src, (uint)size);

        Device.Vk.UnmapMemory(Device.LogicalDevice, primitive.MorphDeltasBuffer.Memory);
        primitive.OwnsMorphDeltasBuffer = true;
    }

    /// <summary>Unified highlighting: get/create the instance Wireframe shell box for the compressed writeIndex
    /// (lazy-growing pool).
    /// Shares VB/IB from the template _shellGeometry,
    /// while the box's own VertexBuffer/IndexBuffer are kept default-valued.
    /// PrimitiveData.Dispose is null-safe, so there is no double free.
    /// It creates only its own Matrix/Material UBOs + DescriptorSet,
    /// and uses identity matrices
    /// (instanced-path shaders ignore b0 world; per-instance matrices are addressed by instance-stream slot).
    /// Returns null when the template is not ready
    /// (no non-skinned/non-morph primitives), and the caller then skips adding it to the draw list.
    /// Empty slots created before the template was ready are automatically backfilled later
    /// once the template becomes ready.</summary>
    protected HighlightBox? AcquireShellBox(int index)
    {
        if (_shellGeometry == null)
            return null;
        while (_instanceShellBoxes.Count <= index)
            _instanceShellBoxes.Add(CreateInstanceShellBox());
        var box = _instanceShellBoxes[index];
        if (box == null)
        {
            // Backfill an empty slot
            // (index reserved while the template was not ready; filled on first use after readiness)
            box = CreateInstanceShellBox();
            _instanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: create the per-instance shell box for a shared template
    /// (see AcquireShellBox docs).</summary>
    HighlightBox? CreateInstanceShellBox()
    {
        if (_shellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_shellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_shellGeometry.Edges);
        return box;
    }

    /// <summary>Unified highlighting: get/create the instance skinned Wireframe shell box
    /// for the compressed writeIndex
    /// (same structure as AcquireShellBox, sharing VB/IB from _skinnedShellGeometry;
    /// skinned shells use the per-instance bone-palette path).
    /// Returns null when the template is not ready
    /// (no single-Skin skinned primitives or multi-skin assets), and the caller then skips it.</summary>
    protected HighlightBox? AcquireSkinnedShellBox(int index)
    {
        if (_skinnedShellGeometry == null)
            return null;
        while (_skinnedInstanceShellBoxes.Count <= index)
            _skinnedInstanceShellBoxes.Add(CreateSkinnedInstanceShellBox());
        var box = _skinnedInstanceShellBoxes[index];
        if (box == null)
        {
            // Backfill an empty slot
            // (index reserved while the template was not ready; filled on first use after readiness)
            box = CreateSkinnedInstanceShellBox();
            _skinnedInstanceShellBoxes[index] = box;
        }
        return box;
    }

    /// <summary>Unified highlighting: create the per-instance shell box for a shared skinned template
    /// (see AcquireSkinnedShellBox docs).</summary>
    HighlightBox? CreateSkinnedInstanceShellBox()
    {
        if (_skinnedShellGeometry == null)
            return null;
        var box = new HighlightBox();
        box.Face = CreateSharedShellPrimitive(_skinnedShellGeometry.Face);
        box.Edges = CreateSharedShellPrimitive(_skinnedShellGeometry.Edges);
        return box;
    }

    /// <summary>Unified highlighting: derive a shared-geometry box from a template primitive
    /// by copying CPU-side vertex/index array references and material/texture references,
    /// while leaving owned GPU handles at default values.
    /// PrimitiveData.Dispose is null-safe, so there is no double free.
    /// Vertices/indices are immutable shared data and Dispose only releases GPU buffers,
    /// so aliasing is safe
    /// (DrawPrimitive reads Indices.Length each draw to determine index count).
    /// It creates its own N-buffered Matrix/Material UBOs,
    /// initializes all frames, and writes the DescriptorSet once.
    /// Matrices stay identity, and instanced drawing uses the instance-stream slot.</summary>
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
            // 2-3 Step C (track C-a): per-instance previous world matrices already come from
            // the prev instance world SB,
            // so b0.PrevWorld stays all zero
            // because the instanced-path shader does not read b0 prevWorld.
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };
        for (int i = 0; i < Device.frameCount; i++)
        {
            Unsafe.Write(primitive.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }
        AllocateAndWriteDescriptorSets(primitive);
        return primitive;
    }

    /// <summary>Unified highlighting (instanced): write face/edge dual colors plus current-frame matrices
    /// into the per-instance shell box's own N-buffered UBO
    /// (current frame fi only), and record this frame's face alpha.
    /// World stays identity
    /// (per-instance world matrices are addressed by instance-stream writeIndex slot),
    /// but View/Projection/PrevViewProjection must be rewritten every frame with the current camera.
    /// Writing them only once at creation time (CreateSharedShellPrimitive) would lock the shell
    /// to the camera-space VP transform at creation time,
    /// making Wireframe move with the camera instead of staying attached to the character
    /// (found on real Metal hardware; Vulkan shares the same fix).
    /// PrevWorld stays all zero
    /// (2-3 Step C track C-a: instanced-path per-instance historical world matrices come from
    /// the prev instance world SB, and the shader does not read b0 prevWorld).</summary>
    protected void WriteInstanceShell(HighlightBox box, Vector4 faceColor, Vector4 edgeColor)
    {
        int fi = (int)Device.FrameIndex;
        var matrices = new MatrixBuffer
        {
            World = Matrix4x4.Transpose(Matrix4x4.Identity),
            View = Matrix4x4.Transpose(Camera.View),
            Projection = Matrix4x4.Transpose(Camera.Projection),
            PrevViewProjection = Matrix4x4.Transpose(Camera.PrevViewProjection),
        };
        Unsafe.Write(box.Face.MappedMatrixBuffers[fi], matrices);
        Unsafe.Write(box.Edges.MappedMatrixBuffers[fi], matrices);
        box.Face.MaterialParams.BaseColor = faceColor;
        Unsafe.Write(box.Face.MappedMaterialBuffers[fi], box.Face.MaterialParams);
        box.Edges.MaterialParams.BaseColor = edgeColor;
        Unsafe.Write(box.Edges.MappedMaterialBuffers[fi], box.Edges.MaterialParams);
        box.FaceAlpha = faceColor.W;
    }

    /// <summary>Unified highlighting (instanced): draw one per-instance shell box
    /// with instanceCount=1 + startInstance slot.
    /// When face alpha (`SurfaceColor.W`) is > 0, draw faces with the double-sided transparent 2-pass rule
    /// (= 0 becomes edge-only automatically and skips faces);
    /// edges use Opaque (CullNone + depth write).
    /// Geometry comes from the shared template geo
    /// (_shellGeometry for the rigid pool, _skinnedShellGeometry for the skinned pool;
    /// the box itself does not own VB/IB, see CreateSharedShellPrimitive).
    /// For skinned shells, slot addressing through gl_InstanceIndex
    /// (including firstInstance) is automatically correct,
    /// so no extra base constant is needed on the draw side.</summary>
    protected void DrawInstanceShellBox(HighlightBox box, HighlightBox geo, BufferResource instanceBuffer, uint startInstance)
    {
        var cmd = Device.GraphicsCommandBuffer;
        int fi = (int)Device.FrameIndex;
        var face = box.Face;
        var edges = box.Edges;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
            Pipeline.DrawPrimitive(cmd, face, geo.Face.VertexBuffer.Buffer, geo.Face.IndexBuffer.Buffer,
                face.DescriptorSets[fi], (uint)face.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
            Pipeline.DrawPrimitive(cmd, face, geo.Face.VertexBuffer.Buffer, geo.Face.IndexBuffer.Buffer,
                face.DescriptorSets[fi], (uint)face.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
        }

        Pipeline.SetPipeline(cmd, PipelineMode.Opaque, CullModeFlags.None, depthWrite: true);
        Pipeline.DrawPrimitive(cmd, edges, geo.Edges.VertexBuffer.Buffer, geo.Edges.IndexBuffer.Buffer,
            edges.DescriptorSets[fi], (uint)edges.Indices.Length, instanceBuffer.Buffer, 1, startInstance);
    }

    /// <summary>Unified highlighting (instanced): draw all instance shell boxes
    /// that enabled Wireframe in the current frame, box by box through DrawInstanceShellBox.
    /// instanceBuffer is the instance stream -
    /// either the base-class _instanceBuffer, or a per-primitive stream owned by a derived class
    /// such as VKInstancedModel.
    /// Slot layout is isomorphic (80B stride), so any such stream can be used.
    /// Mixed assets (rigid + skinned primitives) draw both shells:
    /// the same writeIndex is fetched once from the rigid pool and once from the skinned pool.</summary>
    protected void DrawShellBoxes(BufferResource instanceBuffer)
    {
        for (int i = 0; i < _shellBoxDrawList.Count; i++)
        {
            int idx = _shellBoxDrawList[i];
            if ((uint)idx < (uint)_instanceShellBoxes.Count)
            {
                var box = _instanceShellBoxes[idx];
                if (box != null && _shellGeometry != null)
                    DrawInstanceShellBox(box, _shellGeometry, instanceBuffer, (uint)idx);
            }
            if ((uint)idx < (uint)_skinnedInstanceShellBoxes.Count)
            {
                var box = _skinnedInstanceShellBoxes[idx];
                if (box != null && _skinnedShellGeometry != null)
                    DrawInstanceShellBox(box, _skinnedShellGeometry, instanceBuffer, (uint)idx);
            }
        }
    }

    /// <summary>Unified highlighting: build shell primitives from vertices/indices.
    /// Material parameters are copied from the source primitive, then forced to
    /// Unlit + transparent (BLEND) / opaque (OPAQUE),
    /// DoubleSided + five White textures (Unlit does not sample them).
    /// VB/IB/two UBOs are created and fully initialized through InitBoundsBoxGpuResources.
    /// Skinning and instancing flags such as IsSkinned/BonePaletteStride/IsInstanced
    /// are inherited from the source material,
    /// which is critical for non-instanced per-primitive shell boxes to stay perfectly aligned with skinned animation.</summary>
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
            // IB bit width must match the content detection in ResourceManager.CreateIndexBuffer
            // (store as 16-bit when all indices <= 65535).
            // Shell-face vertex count may be < 65536, so hardcoding 32-bit here would bind
            // a 16-bit IB as Uint32 and break indices, making faces invisible
            // (DX does not have this issue).
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

    /// <summary>
    /// Unified highlighting (Bounds-box geometry): face PrimitiveData for the box.
    /// The 8 corner points are baked in [-0.5, 0.5]^3
    /// (corner-index bit encoding x + y*2 + z*4), with 36 indices.
    /// RenderMode=0 (Unlit) + AlphaMode=2 (BLEND) means truly transparent faces using the Transparent PSO,
    /// DoubleSided + five White textures (Use*Map=0, Unlit does not sample them).
    /// Geometry is reused from the shared HighlightGeometry layer
    /// and matches DX bit for bit.
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
    /// Unified highlighting (Bounds-box geometry): edge PrimitiveData for the box.
    /// Uses 12 thin boxes (3 axes x 4 edges per axis),
    /// extending one thickness past the corner points along the axis ([-0.5-h, 0.5+h])
    /// so all 8 corners connect seamlessly.
    /// RenderMode=0 + AlphaMode=0 (OPAQUE) means solid edges using the Opaque PSO with depth writes,
    /// independent from face-alpha pulsing
    /// (EdgeColor is always interpreted as solid).
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

    /// <summary>Unified highlighting: create VB/IB/Material UBO/Matrix UBO for box primitives,
    /// initialize all frames
    /// (to avoid reading garbage under N-buffering),
    /// and write the DescriptorSet once.</summary>
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
            Unsafe.Write(primitive.MappedMatrixBuffers[i], matrices);
            Unsafe.Write(primitive.MappedMaterialBuffers[i], primitive.MaterialParams);
        }

        AllocateAndWriteDescriptorSets(primitive);
    }

    /// <summary>
    /// Unified highlighting: each frame, write the box world matrix plus face/edge dual colors
    /// into the box's own N-buffered UBO (current frame fi only).
    /// There is no Changed gate:
    /// face alpha (`SurfaceColor.W`) pulses every frame, so the steady write cost matches normal primitives.
    /// PrevWorld comes from the CPU shadow copy (`box.PrevWorld`);
    /// after writing, the current-frame world is rolled into the shadow copy for the next frame.
    /// `world` is supplied by the caller
    /// (Bounds = Scale(Extents x 2) x Translate(Center), with degenerate-box checks done by the caller;
    /// Wireframe = model/instance world matrix, always valid).
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

    /// <summary>Unified highlighting: draw a single highlight box.
    /// When face alpha (`SurfaceColor.W`) is > 0, draw faces with the engine's double-sided transparent
    /// 2-pass rule (Front -> Back);
    /// when = 0, it becomes edge-only automatically and skips faces.
    /// Edges use Opaque (CullNone + depth writes),
    /// so the solid thin strips cover both faces and interior geometry.
    /// Bones/morph data are already bound through DescriptorSet,
    /// so OnBeforeDraw is not needed.</summary>
    protected void DrawHighlightBox(HighlightBox box)
    {
        var face = box.Face;
        var edges = box.Edges;
        var cmd = Device.GraphicsCommandBuffer;

        if (box.FaceAlpha > 0f)
        {
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
            DrawPrimitive(cmd, face);
            Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
            DrawPrimitive(cmd, face);
        }

        Pipeline.SetPipeline(cmd, PipelineMode.Opaque, CullModeFlags.None, depthWrite: true);
        DrawPrimitive(cmd, edges);
    }

    /// <summary>Unified highlighting: draw all instance boxes that enabled Bounds boxes in the current frame
    /// by calling DrawHighlightBox per box.</summary>
    protected void DrawBoundsBoxes()
    {
        for (int i = 0; i < _boundsBoxDrawList.Count; i++)
            DrawHighlightBox(_instanceBoundsBoxes[_boundsBoxDrawList[i]]);
    }

    /// <summary>Unified highlighting: release shared shell templates
    /// (rigid + skinned) and both instance shell-box pools
    /// (boxes share template geometry).
    /// Used both for edge-width-triggered rebuilds and DisposeHighlights.</summary>
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

    /// <summary>2-3 Step C: synchronize prev flags on shell primitives.
    /// HasPrevBones / HasPrevInstanceWorld / HasPrevMorph on shell Face/Edges
    /// must be enabled in sync with the main primitives,
    /// otherwise shells would have no motion trail
    /// (plan risk 2, explicitly covered here).
    /// Covers both shared templates and both instance-box pools
    /// (pooled boxes may have been created before prev became ready, so stale flags must be patched box by box).
    /// With a changed guard + writes across all N-buffered frames,
    /// per-frame call cost is negligible.</summary>
    protected void SyncShellPrevFlags(bool hasPrevInstanceWorld, bool hasPrevBones, bool hasPrevMorph)
    {
        if (_shellGeometry != null)
            SyncShellBoxPrevFlags(_shellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        if (_skinnedShellGeometry != null)
            SyncShellBoxPrevFlags(_skinnedShellGeometry, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _instanceShellBoxes)
            if (box != null)
                SyncShellBoxPrevFlags(box, hasPrevInstanceWorld, hasPrevBones, hasPrevMorph);
        foreach (var box in _skinnedInstanceShellBoxes)
            if (box != null)
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

    /// <summary>Unified highlighting: release all highlight GPU resources
    /// (host Bounds box + instance Bounds box pool +
    /// per-primitive Wireframe shell boxes + shared shell templates + Wireframe instance-box pools).
    /// Box-primitives own their own resources
    /// (VB/IB/UBO/DescriptorSet), and PrimitiveData.Dispose reclaims them in full;
    /// deferred-release semantics are handled by ResourceManager.DestroyBuffer.</summary>
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
    // Instance: Draw (three-bucket grouping)
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

        var cmd = Device.GraphicsCommandBuffer;

        // Grouping: Opaque / Fade (forced when overall Alpha < 1) / Transparent (true BLEND materials)
        // Key fix: when Alpha < 1, non-BLEND materials use the Fade PSO
        // (blending + depth writes) instead of the Transparent PSO.
        // Depth writes prevent multi-layer overlapping meshes in complex models
        // from blending one after another into "over-transparency / interior geometry leakage".
        bool forceFadeByAlpha = _currentAlpha < 1.0f;

        // 1. Opaque
        // Writes depth; under 2-2 clause 7, AoExempt primitives use the NoDepth variant and do not write depth
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
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite) { Pipeline.SetPipeline(cmd, PipelineMode.Opaque, p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite); pipelineSet = true; currentDoubleSided = p.DoubleSided; currentDepthWrite = depthWrite; }
                DrawPrimitive(cmd, p);
            }
        }

        // 2. Fade
        // Enabled only when _currentAlpha < 1:
        // non-BLEND materials use the Fade PSO with blending + depth writes
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
                if (!pipelineSet || currentDoubleSided != p.DoubleSided || currentDepthWrite != depthWrite) { Pipeline.SetPipeline(cmd, PipelineMode.Fade, p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite); pipelineSet = true; currentDoubleSided = p.DoubleSided; currentDepthWrite = depthWrite; }
                DrawPrimitive(cmd, p);
            }
        }

        // 3. Transparent
        // True BLEND materials, no depth writes
        pipelineSet = false;
        currentDoubleSided = false;
        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (!p.IsTransparent) continue;
            if (p.DoubleSided)
            {
                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.FrontBit);
                DrawPrimitive(cmd, p);
                Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit);
                pipelineSet = true;
                currentDoubleSided = false;
                DrawPrimitive(cmd, p);
                continue;
            }

            if (!pipelineSet || currentDoubleSided != p.DoubleSided) { Pipeline.SetPipeline(cmd, PipelineMode.Transparent, CullModeFlags.BackBit); pipelineSet = true; currentDoubleSided = false; }
            DrawPrimitive(cmd, p);
        }

        // Unified highlighting
        // (lazily created face+edge primitive groups, independent from the CollectPrimitives/SyncAlpha chain;
        // transparent faces use 2-pass rendering + edges use Opaque with depth writes,
        // so dual-color highlighting is finished after all surfaces in this group):
        // host Bounds box + per-instance boxes + per-primitive Wireframe shell boxes
        // (`SurfaceColor.w = 0` means edge-only; face drawing is skipped at draw time through FaceAlpha gating)
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

    void DrawPrimitive(CommandBuffer cmd, PrimitiveData p)
    {
        int fi = (int)Device.FrameIndex;
        // Unified entry point: regular drawing uses instanceCount=1, instanceBuffer=default
        // and automatically falls back to IdentityInstanceBuffer
        Pipeline.DrawPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
            p.DescriptorSets[fi], (uint)p.Indices.Length, default, 1, 0);
    }

    // ============================================================
    // Instance: Shadow-pass drawing
    // (contract clause 7: no frustum culling)
    // ============================================================

    /// <summary>
    /// Shadow-pass drawing
    /// (under contract clause 7, the per-quadrant light-space culling entry point lives in shared Mesh3DBase.DrawShadow):
    /// draw all opaque primitives and skip true BLEND materials
    /// (anything that does not write depth does not cast shadows).
    /// PSO/push constants are already set uniformly by the shadow-pass entry;
    /// this method only adds OnBeforeDraw (b3 bone UBO) and per-primitive draws.
    /// Mirrors DX DrawShadow 1:1.
    ///
    /// Primitive lists are cached per pass using <see cref="Season.Rendering.CascadedShadow.Epoch"/>.
    /// RenderShadowPass calls this method repeatedly per atlas quadrant
    /// (3 cascades + spotlight), changing only the push-constant light-space ViewProj and viewport.
    /// The primitive set itself is identical,
    /// so CollectPrimitives runs only once per pass
    /// (VKModel does recursive glTF-node traversal + AddRange,
    /// VKMesh3D does AddRange; both are side-effect free and safe to replay).
    /// The main-pass _drawList is isolated from this path through the dedicated _shadowDrawList field,
    /// so Draw() and DrawShadow() never overwrite each other within the same frame.
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

        var cmd = Device.GraphicsCommandBuffer;
        for (int i = 0; i < _shadowDrawList.Count; i++)
        {
            var p = _shadowDrawList[i];
            if (p.IsTransparent)
                continue;
            DrawShadowPrimitive(cmd, p);
        }
    }

    void DrawShadowPrimitive(CommandBuffer cmd, PrimitiveData p)
    {
        int fi = (int)Device.FrameIndex;
        Pipeline.DrawShadowPrimitive(cmd, p, p.VertexBuffer.Buffer, p.IndexBuffer.Buffer,
            p.DescriptorSets[fi], (uint)p.Indices.Length, default, 1, 0);
    }

    // ============================================================
    // Outline2D mask-pass drawing
    // (called group by group from Graphics.RenderOutlineMask)
    // ============================================================

    /// <summary>
    /// Outline2D mask drawing (non-instanced):
    /// draw all opaque primitives and skip true BLEND materials.
    /// PSO/push constants (outline color) are already set uniformly by this method
    /// (Opaque + depthWrite=false, mirroring DX DrawOutlineMask's `depthWrite:false`).
    /// This path switches PSO per primitive according to DoubleSided
    /// and adds OnBeforeDraw (b3 bone UBO) plus per-primitive draws.
    /// Mirrors DX DrawOutlineMask 1:1.
    /// </summary>
    public virtual void DrawOutlineMask()
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        _drawList.Clear();
        CollectPrimitives(_drawList);
        if (_drawList.Count == 0)
            return;

        // Write outline color per group through the FS push constant:
        // for multi-color cases in the same frame, use the group color fixed during Update
        var cmd = Device.GraphicsCommandBuffer;
        Pipeline.SetOutlineMaskColor(cmd, _outline2DColor);

        for (int i = 0; i < _drawList.Count; i++)
        {
            var p = _drawList[i];
            if (p.IsTransparent)
                continue;
            Pipeline.SetPipeline(cmd, PipelineMode.Opaque,
                p.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, depthWrite: false);
            OnBeforeDraw();
            DrawPrimitive(cmd, p);
        }
    }
}
