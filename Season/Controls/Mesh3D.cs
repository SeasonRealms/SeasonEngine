// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Controls;

/// <summary>
/// Surface blend modes for Mesh3D, aligned with the three glTF alphaMode variants.
/// - Opaque: fully opaque, using the Opaque PSO, writing depth, and ignoring texture alpha.
/// - Mask: binary cutout, using the Opaque PSO plus shader clip(alpha - AlphaCutoff), and writing depth.
///         Suitable for assets such as sun.png, leaves, or fences that use alpha cutouts without blending.
/// - Blend: translucent, using the Transparent PSO, not writing depth, and performing true alpha blending.
/// </summary>
public enum SurfaceBlendMode
{
    Opaque,
    Mask,
    Blend,
}

/// <summary>
/// PBR texture slots for Surface. Values map one-to-one to each backend's internal TextureSlot
/// (BaseColor = 0 through Emissive = 4), so they can be mapped directly by value.
/// </summary>
public enum SurfaceTextureSlot
{
    BaseColor = 0,
    Normal = 1,
    MetallicRoughness = 2,
    Occlusion = 3,
    Emissive = 4,
}

/// <summary>
/// A triangle set sharing one group of material parameters.
/// Vertices and indices may form arbitrary shapes without requiring coplanarity, and UVs are per-vertex.
/// It uses the unlit path by default; setting Surface.Unlit to false enables the full PBR material inputs.
/// When BaseColorTexturePath is empty, pure-color mode is used. Other missing texture paths fall back to their default values.
/// Each slot can also provide in-memory pixels directly through the corresponding TextureOverride (INativeImageDecoder).
/// During Load, the backend consumes them once and uploads them straight to the GPU with no file I/O,
/// using the same path as embedded glTF images.
/// </summary>
public class Surface
{
    /// <summary>Vertex array. TexCoord/Normal/Tangent are provided by the caller; Joints/Weights should be set to 0.</summary>
    public Vertex[] Vertices { get; set; }

    /// <summary>Triangle-list indices. Follows the LH + FrontCounterClockwise = 0 convention, so clockwise is front-facing when viewed from outside.</summary>
    public ushort[] Indices { get; set; }

    /// <summary>Relative texture path under `Raw/`. Null or empty uses pure-color mode.</summary>
    public string BaseColorTexturePath { get; set; }

    /// <summary>Relative normal-map path under `Raw/`. Null or empty disables the normal map.</summary>
    public string NormalTexturePath { get; set; }

    /// <summary>Relative metallic-roughness texture path under `Raw/`. Null or empty uses MetallicFactor and RoughnessFactor.</summary>
    public string MetallicRoughnessTexturePath { get; set; }

    /// <summary>Relative AO texture path under `Raw/`. Null or empty uses the default AO = 1.</summary>
    public string OcclusionTexturePath { get; set; }

    /// <summary>Relative emissive texture path under `Raw/`. Null or empty uses EmissiveFactor.</summary>
    public string EmissiveTexturePath { get; set; }

    /// <summary>
    /// BaseColor texture override source, used to replace the Surface texture at runtime.
    /// Supports either a file path or pixel data through implicit conversion.
    /// </summary>
    public TextureUpdateSource TextureOverride { get; set; }

    /// <summary>Normal texture override source, used to replace the Surface texture at runtime.</summary>
    public TextureUpdateSource NormalTextureOverride { get; set; }

    /// <summary>MetallicRoughness texture override source, used to replace the Surface texture at runtime.</summary>
    public TextureUpdateSource MetallicRoughnessTextureOverride { get; set; }

    /// <summary>Occlusion texture override source, used to replace the Surface texture at runtime.</summary>
    public TextureUpdateSource OcclusionTextureOverride { get; set; }

    /// <summary>Emissive texture override source, used to replace the Surface texture at runtime.</summary>
    public TextureUpdateSource EmissiveTextureOverride { get; set; }

    /// <summary>Base color. In pure-color mode this is the output color; with a texture it is multiplied by the texture. The W channel is multiplied by Surface.Alpha and Mesh3D.Alpha.</summary>
    public Vector4 BaseColor { get; set; } = Vector4.One;

    /// <summary>Metallic factor. Used directly as the material metallic value when no MR texture is provided; otherwise multiplied by the texture's B channel.</summary>
    public float MetallicFactor { get; set; } = 0f;

    /// <summary>Roughness factor. Used directly as the material roughness value when no MR texture is provided; otherwise multiplied by the texture's G channel.</summary>
    public float RoughnessFactor { get; set; } = 0.5f;

    /// <summary>Emissive color. Used directly when no emissive texture is provided; otherwise acts as the base intensity.</summary>
    public Vector4 EmissiveFactor { get; set; } = Vector4.Zero;

    /// <summary>
    /// Surface-level opacity multiplied into the final BaseColor.W.
    /// Final alpha = BaseColor.W x Surface.Alpha x Mesh3D.Alpha x texture alpha.
    /// Note: the current implementation bakes this into PrimitiveData.OriginalBaseColorAlpha during Load,
    /// so changing Surface.Alpha dynamically at runtime has no effect.
    /// </summary>
    public float Alpha { get; set; } = 1f;

    /// <summary>Surface-level blend mode.</summary>
    public SurfaceBlendMode Mode { get; set; } = SurfaceBlendMode.Opaque;

    /// <summary>
    /// Binary cutout threshold used by MASK mode: pixels with texture alpha &lt; AlphaCutoff are discarded.
    /// Only applies when Mode = Mask; at runtime it scales proportionally with Mesh3D.Alpha
    /// (see DXPrimitiveGroup.SyncAlpha). Defaults to 0.5, matching the glTF spec.
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    /// <summary>Whether to render both sides. When true, back-face culling is disabled; Blend mode uses the dual-pass transparency approximation.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>
    /// Whether to use the unlit path. Defaults to true to preserve legacy Mesh3D behavior
    /// and fits skyboxes, billboards, pure textured quads, and similar cases.
    /// Set it to false to enable PBR lighting and consume normal/MR/AO/emissive inputs.
    /// </summary>
    public bool Unlit { get; set; } = true;

    /// <summary>
    /// Whether to use the procedural-sky path (2-5). When true, the main shader uses renderMode = 3,
    /// ignores vertex UVs, and instead reconstructs Sky-View LUT UVs from the world view direction
    /// before sampling the BaseColor texture. It is therefore mutually exclusive with Unlit and takes priority over it.
    /// Usage: set <c>BaseColorTexturePath = FrameSchedule.SkyViewTexture</c> and enable this flag;
    /// all six faces share the same LUT. When SkyViewTexture is null because the procedural profile is inactive,
    /// keep this false so it falls back to the static cube texture.
    /// </summary>
    public bool ProceduralSky { get; set; }

    /// <summary>
    /// Returns the effective texture source for the specified slot: first the matching TextureOverride
    /// (pixel data or path), then the slot path wrapped as TextureUpdateSource. If both are empty, returns default.
    /// During Load the backend resolves textures from this: the Image branch uploads directly to the GPU without
    /// writing to disk, while the Path branch uses the regular loading path.
    /// </summary>
    public TextureUpdateSource GetTextureSource(SurfaceTextureSlot slot) => slot switch
    {
        SurfaceTextureSlot.BaseColor => TextureOverride.HasValue ? TextureOverride : PathToSource(BaseColorTexturePath),
        SurfaceTextureSlot.Normal => NormalTextureOverride.HasValue ? NormalTextureOverride : PathToSource(NormalTexturePath),
        SurfaceTextureSlot.MetallicRoughness => MetallicRoughnessTextureOverride.HasValue ? MetallicRoughnessTextureOverride : PathToSource(MetallicRoughnessTexturePath),
        SurfaceTextureSlot.Occlusion => OcclusionTextureOverride.HasValue ? OcclusionTextureOverride : PathToSource(OcclusionTexturePath),
        SurfaceTextureSlot.Emissive => EmissiveTextureOverride.HasValue ? EmissiveTextureOverride : PathToSource(EmissiveTexturePath),
        _ => default,
    };

    /// <summary>
    /// The authored path of one slot, ignoring any TextureOverride. This is deliberately not
    /// <see cref="GetTextureSource"/>: the backends need it while <em>releasing</em> a mesh's texture references, and by
    /// then Load has cleared every Override under the single-consumption contract, so GetTextureSource can no longer
    /// report which branch actually ran. Reading the path alone gives release the same answer the path branch of
    /// EnsureSurfaceTexture saw at Load time, independent of override state.
    /// </summary>
    public string? GetTexturePath(SurfaceTextureSlot slot) => slot switch
    {
        SurfaceTextureSlot.BaseColor => BaseColorTexturePath,
        SurfaceTextureSlot.Normal => NormalTexturePath,
        SurfaceTextureSlot.MetallicRoughness => MetallicRoughnessTexturePath,
        SurfaceTextureSlot.Occlusion => OcclusionTexturePath,
        SurfaceTextureSlot.Emissive => EmissiveTexturePath,
        _ => null,
    };

    /// <summary>Whether the specified slot has an effective texture source, either a path or pixels. Drives the Use*Map material flags under the "declared means enabled" rule.</summary>
    public bool HasTexture(SurfaceTextureSlot slot) => GetTextureSource(slot).HasValue;

    /// <summary>Clears the TextureOverride for the specified slot. Called by the backend after Load has consumed it to preserve the one-shot consumption contract.</summary>
    public void ClearTextureOverride(SurfaceTextureSlot slot)
    {
        switch (slot)
        {
            case SurfaceTextureSlot.BaseColor: TextureOverride = default; break;
            case SurfaceTextureSlot.Normal: NormalTextureOverride = default; break;
            case SurfaceTextureSlot.MetallicRoughness: MetallicRoughnessTextureOverride = default; break;
            case SurfaceTextureSlot.Occlusion: OcclusionTextureOverride = default; break;
            case SurfaceTextureSlot.Emissive: EmissiveTextureOverride = default; break;
        }
    }

    static TextureUpdateSource PathToSource(string? path)
        => string.IsNullOrEmpty(path) ? default : new TextureUpdateSource { Path = path };
}

/// <summary>
/// Caller-assembled 3D mesh control composed of multiple Surfaces.
/// v1 has no skinning or animation. World transforms follow the unified positioning model:
/// (PosX, PosY, PosZ) is the anchor at the geometric center of the bounding box,
/// Width/Height/Depth are per-axis scales, and Rotation is a quaternion whose pivot is the anchor,
/// see <see cref="Mesh3DBase"/>. When position needs to be expressed relative to the mesh-local origin,
/// convert with <see cref="Mesh3DBase.AnchorWorldOffset"/>.
/// For the shared framework of bounds culling, shadow gating, GI proxies, and Draw gating,
/// see <see cref="Mesh3DBase"/>.
/// </summary>
public class Mesh3D : Mesh3DBase
{
    public Mesh3D()
    {
        // Name is normalized back to Control.Name. Platform dictionaries use (Name, ID) as the key.
        // The default value is only for logs and cache keys, so it may be omitted.
        Name = "Mesh3D";
    }

    /// <summary>All Surfaces that make up this Mesh3D.</summary>
    public List<Surface> Surfaces { get; } = new List<Surface>();

    /// <summary>World rotation, defaulting to the identity quaternion. This is the rotation input in the unified positioning model, with the anchor as pivot.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>Rotation injection point: quaternion rotation with the anchor as pivot, see <see cref="Mesh3DBase.BuildWorldMatrix"/>.</summary>
    protected override Matrix4x4 GetRotationMatrix() => Matrix4x4.CreateFromQuaternion(Rotation);

    /// <summary>
    /// Mesh-level color multiplier: xyz multiplies each Surface.BaseColor.rgb component-wise.
    /// W is unaffected, and opacity is still controlled by the Alpha chain.
    /// Vector4.One means no multiplier. Runtime changes take effect on the next Update.
    /// Backends only write the material buffer when this changes, so steady-state cost is zero.
    /// Typical use: adjust brightness or color temperature of unlit meshes such as skyboxes across a day-night cycle,
    /// see Season.Rendering.DayNightCycle.SkyTint.
    /// </summary>
    public Vector4 ColorTint { get; set; } = Vector4.One;

    /// <summary>
    /// 2-2 extension clause: whether this mesh is exempt from GTAO ambient occlusion.
    /// Default false means normal participation. When true, this mesh does not write depth in the Scene pass
    /// (Opaque/Fade use the NoDepth PSO variant on all backends). SceneDepth then stays at the clear value 1.0,
    /// so the empty-sky branch of the GTAO kernel (d >= 1) writes ao = 1 directly and the composite stage applies no darkening there.
    /// Typical use: skyboxes. A real skybox cube with depth &lt; far can be misidentified as occluded by horizon search at face seams,
    /// producing dark cracks along edges. Exempting it also removes AO dark halos where the sky meets scene geometry.
    /// By design this only fits background meshes that are drawn first and never have geometry behind them.
    /// Applying it to normal geometry removes depth occlusion, allowing later objects to show through, and also removes AO.
    /// It is orthogonal to CastShadows = false under 1-5 clause 7; skyboxes typically set both.
    /// It can be toggled at runtime, with each backend syncing it during Update to PrimitiveData or the JS route bit.
    /// </summary>
    public bool ExcludeFromAo { get; set; }

    /// <summary>
    /// 主涌浪分量的波数（2π/90m）。着色器里三道分量的波长是 90m/55m/34m，这里只复现第一道：
    /// 相位是三道共用的一个标量，另外两道的角频率取它的 2 倍与 3 倍，
    /// 所以 CPU 侧只需要知道主分量的波数就能把 <see cref="WaveSpeed"/> 换算成相位推进量。
    /// 两侧各写一份常量是刻意的，这个值不是给外部调的，多一条序列化字段没有好处；
    /// 改波长时要同时改这里和四个后端的 <c>SwellEvaluate</c>。
    /// </summary>
    const float SwellWaveNumber0 = 0.06981317f;     // 2 * PI / 90

    const float TwoPi = 6.2831853f;

    /// <summary>
    /// 顶点涌浪的峰谷高度（米），0 表示完全关闭。默认关闭，因此所有既有网格的行为一字不变。
    ///
    /// 这是叠在网格自身烘焙起伏之上的一层行波，不是替代：起伏仍然决定海面的整体形状与近岸衰减，
    /// 涌浪只让它在原地上下动起来。位移发生在顶点着色器里，法线也随解析梯度重建，
    /// 所以太阳高光、CSM 阴影接收与 GI 都跟着一起动，不需要额外的通道。
    /// </summary>
    public float WaveAmplitude { get; set; }

    /// <summary>
    /// 主分量相速度（米/秒）。相位由它积分出来，所以改这个值只影响之后的推进速度，
    /// 不会像"历经时间乘速率"那样让整个波形瞬移。
    ///
    /// 浪向是固定的：相位在着色器里是加号，波峰半径 r 满足 k*r + 相位 = 常数，
    /// 相位涨一点波峰半径就小一点，所以浪永远朝岛心收拢，这个速度也就是径向靠岸速度。
    /// </summary>
    public float WaveSpeed { get; set; } = 9f;

    /// <summary>
    /// 涌浪半径（米）：离相机超过这个距离的顶点回到烘焙起伏。0 表示不做距离衰减。
    ///
    /// 这是这个特性里最大的一笔性能账：1400m 的网格有 65025 个顶点，而 1m 的浪高在 150m 外不到一个像素，
    /// 真正需要位移的只有相机附近那一圈。远处的海面本来就看不出一米级别的起伏，但它的太阳反光带是看得见的，
    /// 所以那里不能剔除、也不能变黑，只能退回静态姿态——正好就是烘焙好的那份几何与法线。
    ///
    /// 门控量的是到相机的三维距离，不是水平距离，这一个判据同时覆盖三种处境：站在岛上时高度差只有几米，
    /// 与水平距离等价；坐船时相机贴着水面，同样等价；飞到高空时高度差就是离地高度，正下方的水面早已超出半径，
    /// 整片海自动停止位移。所以这里不需要额外的"相机太高就归零"规则，也不该由面板去猜多高才算看不出。
    /// </summary>
    public float WaveFadeRadius { get; set; } = 260f;

    float _wavePhase;
    float _wavePhasePrev;
    float _waveLastTime;
    bool _waveClockSeeded;

    /// <summary>
    /// 交给后端的一个 float4：(半峰谷振幅, 当前相位, 上一帧相位, 衰减半径)。
    /// 缓冲区里的相位不取模也能算对，取模只是为了长时间运行时不丢 float 的有效位。
    /// </summary>
    internal Vector4 WaveParams => new(WaveAmplitude * 0.5f, _wavePhase, _wavePhasePrev, WaveFadeRadius);

    /// <summary>
    /// 相位积分。用增量而不是 elapsedTime × speed：后者在改速度的那一刻会把整个波形瞬移一段，
    /// 而增量只改变之后走多快。取模 2π 不产生接缝，因为三道分量的角频率是 1:2:3 的整数比。
    /// </summary>
    void AdvanceWavePhase(float time)
    {
        if (!_waveClockSeeded)
        {
            _waveClockSeeded = true;
            _waveLastTime = time;
            return;
        }

        float dt = time - _waveLastTime;
        _waveLastTime = time;

        // 场景时间被重定基准（切场景）或这一帧卡了很久时，dt 会是负数或一大段空白；
        // 按 0 处理让波形停在原地，而不是突然跳半圈。
        if (dt <= 0f || dt > 0.25f)
            return;

        _wavePhasePrev = _wavePhase;
        _wavePhase = WrapPhase(_wavePhase + dt * WaveSpeed * SwellWaveNumber0);
    }

    static float WrapPhase(float phase) => phase - TwoPi * MathF.Floor(phase / TwoPi);

    /// <summary>Transparent-sort reference point: the unified positioning model uses the anchor's world position.</summary>
    public override Vector3 TransparentSortPosition => new Vector3(PosX, PosY, PosZ);

    public override bool EnableTransparentSort => Alpha < 1f || Surfaces.Any(s => s.Mode == SurfaceBlendMode.Blend);

    protected override bool HasContent => Surfaces.Count > 0;

    public override async Task<bool> Load()
    {
        // 1-3: Aggregate the control-level local bounding box once during loading, per contract clause 2 and shared across all four backends.
        var bounds = default(Bounds3D);
        bool first = true;
        foreach (var surface in Surfaces)
        {
            if (surface.Vertices == null || surface.Vertices.Length == 0)
                continue;

            var surfaceBounds = Bounds3D.FromVertices(surface.Vertices);
            bounds = first ? surfaceBounds : Bounds3D.Union(bounds, surfaceBounds);
            first = false;
        }
        LocalBounds = bounds;
        // Unified positioning model: Mesh3D has no animation expansion, so the raw box is the aggregated box itself.
        // The setter triggers OnBoundsEstablished to settle default dimensions.
        LocalBoundsRaw = bounds;

        await Graphics.Instance.LoadMesh3D(this);

        return true;
    }

    public bool Update(float time, float? alpha = null)
    {
        var result = base.Update(time, alpha: alpha);

        if (alpha is null || Alpha == alpha)
        {

        }
        else
        {
            Alpha = alpha.Value;
        }

        // Phase advances even while the control is disabled: it is two float adds, and a hidden sea that keeps
        // its clock running is what lets it reappear mid-wave instead of snapping back to phase 0.
        if (WaveAmplitude > 0f)
            AdvanceWavePhase(time);

        if (Ready && HasContent && Enable)
        {
            Graphics.Instance.UpdateMesh3D(this, time);
        }

        return result;
    }

    /// <summary>
    /// Surface-accurate picking for v2: broad-phase LocalBounds culling followed by per-Surface ray-triangle tests.
    /// Only actual triangle surfaces can be hit, so empty space inside the bounding box is no longer selected by mistake.
    /// Overlapping objects resolve by nearest surface distance, meaning the one closer to the screen wins.
    /// When no Surface data exists, the base class falls back to OBB picking.
    /// </summary>
    public override bool TryPickSurface(Vector3 rayOrigin, Vector3 rayDirection, out float distance)
    {
        var world = BuildWorldMatrix();
        if (!TryPickBroadPhase(rayOrigin, rayDirection, world))
        {
            distance = float.MaxValue;
            return false;
        }

        bool hasData = false;
        bool hit = false;
        float bestDistance = float.MaxValue;

        foreach (var surface in Surfaces)
        {
            if (surface.Vertices == null || surface.Vertices.Length < 3
                || surface.Indices == null || surface.Indices.Length < 3)
                continue;

            hasData = true;
            if (Picking.RayIntersectsTriangles(rayOrigin, rayDirection, world, surface.Vertices, surface.Indices, out var d)
                && d < bestDistance)
            {
                bestDistance = d;
                hit = true;
            }
        }

        // No triangle data, such as an empty Surface set, falls back to OBB.
        // If data exists and broad phase passes but narrow phase misses, that is a real miss.
        if (!hasData)
            return base.TryPickSurface(rayOrigin, rayDirection, out distance);

        if (!hit)
        {
            distance = float.MaxValue;
            return false;
        }

        distance = bestDistance;
        return true;
    }

    public override bool Draw()
    {
        var result = false;

        if (base.Draw())
        {
            Graphics.Instance.DrawMesh3D(this);

            result = true;
        }

        return result;
    }

    protected override void DrawShadowCore() => Graphics.Instance.DrawMesh3DShadow(this);

    public override void Dispose()
    {
        base.Dispose();
        Graphics.Instance.DisposeMesh3D(this);
    }
}
