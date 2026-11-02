// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Authored fog setups the app can switch between. This is a preset name rather than a bag of numbers for the same
/// reason RenderQuality keeps tiers rather than raw factors: the numbers themselves belong to whoever builds the medium,
/// and only the choice of setup is a world parameter worth persisting. A preset that a given app does not implement is
/// simply never selected, so the enum stays a naming scheme rather than a capability declaration.
/// </summary>
public enum FogPreset
{
    /// <summary>Low morning mist hugging the ground.</summary>
    Morning,

    /// <summary>Low sun through a colonnade, for visible light shafts.</summary>
    SunShafts,

    /// <summary>Night haze around the street lamp and the room lights.</summary>
    NightLamps,
}

/// <summary>
/// 海面呈现方式。这是世界参数而不是渲染质量档位：三种方式都走既有的、四端都已实现的渲染通道，
/// 差别只在用哪一份几何与材质、以及顶点阶段要不要多算几道正弦，所以它可以随时切换，
/// 也不存在"某端不支持"的情形。
///
/// <see cref="Static"/> 是 P3 之前的海面做法取回来：1400m 烘焙起伏网格本身就是可见表面，
/// 靠低粗糙度与噪声法线让太阳和月亮在水面上拉出高光带。它没有折射，也没有屏幕空间反射，
/// 但每帧的开销只有一次矩阵写入。
/// <see cref="Swell"/> 在同一个网格上叠一层顶点涌浪：波形是三道绕岛的径向正弦，没有 Gerstner 的尖峰
/// 也没有高频细纹，所以轮廓保持圆润的卡通感；太阳反光、CSM 阴影接收与 GI 全部沿用，
/// 因为走的还是同一个 PBR 着色器。位移只在相机附近的一个圆内发生，圆外回到静态的烘焙起伏。
/// <see cref="Dynamic"/> 是 P3/P4 的水面：同一张网格退到水下当海底提供水深，
/// 可见表面由 SceneWater Pass 的 Gerstner 水面绘制，菲涅尔、折射、Hi-Z 屏幕空间反射与深度吸收都在着色器里。
/// 池塘不受这个开关影响，始终走动态水面。
/// </summary>
public enum SeaMode
{
    /// <summary>烘焙起伏网格直接作为可见海面，默认。</summary>
    Static,

    /// <summary>SceneWater Pass 的 Gerstner 水面。</summary>
    Dynamic,

    /// <summary>
    /// 烘焙网格 + 顶点涌浪。追加在枚举末尾而不是插在中间：Settings.json 把枚举存成数字，
    /// 中间插入会把已保存的 Dynamic 读成新值。面板上的排列顺序不受这里影响。
    /// </summary>
    Swell,
}

/// <summary>
/// World-simulation settings: the parameters that say how the world itself behaves over time, as opposed to how it
/// is rendered. Persisted in Settings.json next to <see cref="RenderQuality"/> and editable while the frame loop runs.
///
/// This is a type of its own rather than more fields on RenderQuality because the two answer different questions.
/// RenderQuality is a tier: most of it is read once and locked before graphics initialization, and a backend is
/// allowed to ignore a value it cannot support. Nothing here is a tier - these are simulation inputs that every
/// backend reads identically, that are meant to change at runtime, and that have no notion of being unsupported.
/// Filing a clock rate under a tier object would invite it to be baked at startup along with the values that
/// legitimately are, and would make the tier's "must not change after the frame loop starts" rule a lie.
///
/// The shape deliberately matches RenderQuality so the two are read and persisted the same way: static Default*
/// fields are the default sources an app may override in its constructor, instance properties are what round-trips
/// through Settings.json, and <see cref="Current"/> is the single runtime read entry point. New world parameters
/// belong here rather than in another new type.
/// </summary>
public class WorldSettings
{
    // -- Default-value sources (static Default* fields; apps may override them in the constructor, and BaseApp.Init() snapshots them into Settings.World). --

    /// <summary>Default value for DayNightSpeed (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultDayNightSpeed = 0.01f;

    /// <summary>Default value for StartHour (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultStartHour = DayNightCycle.SunriseHour;

    /// <summary>
    /// Day-night phase increment per second. Phase counts whole day cycles, so the 0.005 default is an about
    /// 100-second day, and 0 holds the sky still - which is the point of exposing it at all, since a frozen sky is
    /// what makes the lighting comparable between two captures.
    ///
    /// Freezing is not a special case: the phase is integrated from this rate rather than computed as elapsed time
    /// times it, so changing the rate only affects where the phase goes next and stopping leaves it where it was.
    ///
    /// Assigning this property is honoured but arrives as a step in the rate. The owner of the integration is
    /// expected to ease toward it instead; in this engine that owner is the app's CelestialLighting, whose
    /// SetDayNightSpeed is the intended entry point and also the one that persists the change.
    /// </summary>
    public float DayNightSpeed { get; set; } = DefaultDayNightSpeed;

    /// <summary>
    /// Clock hour the world starts the day at, in 0~24, where 0 and 24 are the same instant. The default is
    /// <see cref="DayNightCycle.SunriseHour"/>, which is the hour phase 0 already stood for, so leaving it alone reproduces
    /// the sunrise start the sample had before this existed.
    ///
    /// This is a time of day, not a phase: the phase convention counts whole day cycles and its integer part drives the
    /// lunar cycle, so an hour is the part of it a person can actually name. <see cref="DayNightCycle.PhaseFromHour"/> is the
    /// conversion, and it deliberately returns the fractional part alone.
    ///
    /// Unlike <see cref="DayNightSpeed"/> this one is not integrated from - it seeds the clock rather than driving it, so
    /// once the day is running the value here is history and no longer says what time it is. Changing it therefore has to
    /// move the clock explicitly, which is a jump rather than an ease: setting the time is a discontinuity by definition,
    /// where changing the rate is not. CelestialLighting.SetStartHour is the entry point that does both that and the save.
    /// </summary>
    public float StartHour { get; set; } = DefaultStartHour;

    /// <summary>Default value for FogPreset (overrideable in the app constructor and captured by Init()).</summary>
    public static FogPreset DefaultFogPreset = FogPreset.Morning;

    /// <summary>Default value for FogEnabled (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultFogEnabled = true;

    /// <summary>Default value for FogDensityScale (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultFogDensityScale = 1f;

    /// <summary>Default value for FogWeatherLink (overrideable in the app constructor and captured by Init()).</summary>
    public static bool DefaultFogWeatherLink = true;

    /// <summary>
    /// Which authored fog setup the world is running. The preset also names a time of day and a camera in the sample
    /// app, but only the name is persisted: the numbers behind it live next to the code that builds the medium, so
    /// retuning one is not a settings migration, and a world saved under a preset keeps meaning the same setup.
    ///
    /// Changing it is a step rather than an ease. Unlike the day-night rate there is no continuous quantity underneath
    /// to integrate towards: the medium is replaced outright, so the volumetric history has to be dropped instead - see
    /// SceneFog.ResetHistory. The owner of that reset is the panel that writes this property.
    /// </summary>
    public FogPreset FogPreset { get; set; } = DefaultFogPreset;

    /// <summary>
    /// Whether the scene medium participates at all. Separate from <see cref="RenderQuality.VolumetricFog"/> on purpose:
    /// that one is a tier and decides whether the pass is built, which cannot change once the frame loop has started,
    /// while this one is a switch every backend reads identically each frame and that has no notion of being
    /// unsupported. Turning it off leaves the pass registered and costs nothing extra.
    /// </summary>
    public bool FogEnabled { get; set; } = DefaultFogEnabled;

    /// <summary>
    /// Multiplier on the preset's own extinction. This is the one fog number that is a world parameter rather than a
    /// rendering tier, because it is the quantity a person actually reasons about - "thicker than yesterday" - and it
    /// has the same meaning under every preset.
    /// </summary>
    public float FogDensityScale { get; set; } = DefaultFogDensityScale;

    /// <summary>
    /// Whether the medium follows the weather cycle. On, the extinction is scaled by how much cloud cover the current
    /// weather segment carries, so the same preset reads thin on a clear day and thick under an overcast; off, the
    /// preset runs at its authored density regardless of the sky.
    /// </summary>
    public bool FogWeatherLink { get; set; } = DefaultFogWeatherLink;

    /// <summary>Default value for SeaMode (overrideable in the app constructor and captured by Init()).</summary>
    public static SeaMode DefaultSeaMode = SeaMode.Static;

    /// <summary>
    /// 海面用哪一种呈现方式，默认 <see cref="SeaMode.Static"/>。
    ///
    /// 切换是一次结构性替换而不是渐变：静态与涌浪共用同一张网格（涌浪只是多算一段顶点位移），
    /// 动态方式则把网格压到水下当天底并换成 SceneWater 的可见表面，三条路径的可见表面没有共同的中间态可插值。
    /// 每帧只读一次这个属性，所以改动在下一帧生效，不需要重建任何管线 —— 三种方式用的都是已经常驻的通道
    /// （Mesh3D 的 PBR 与 SceneWater Pass）。
    /// </summary>
    public SeaMode SeaMode { get; set; } = DefaultSeaMode;

    /// <summary>Default value for SeaWaveHeight (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultSeaWaveHeight = 1f;

    /// <summary>
    /// 涌浪的峰谷高度（米），只在 <see cref="SeaMode.Swell"/> 下有意义。
    ///
    /// 这个量是峰到谷的距离，与 <see cref="WaterPlane.SwellHeight"/> 同口径，着色器里取一半当振幅。
    /// 海面网格的烘焙起伏本身已有约 ±4.5m 的开阔海涌浪，这一层是叠在它之上的行波，
    /// 所以 1m 已经足够让水面"活"起来。上限不是为了护住水线——近岸掩码在离岸 6m 处就把振幅压到 0，
    /// 波峰爬不上沙滩——而是因为再大就压过了烘焙起伏本身，读起来像风暴而不是涌浪。
    /// </summary>
    public float SeaWaveHeight { get; set; } = DefaultSeaWaveHeight;

    /// <summary>Default value for SeaWaveSpeed (overrideable in the app constructor and captured by Init()).</summary>
    public static float DefaultSeaWaveSpeed = 9f;

    /// <summary>
    /// 涌浪主分量的相速度（米/秒），只在 <see cref="SeaMode.Swell"/> 下有意义。
    ///
    /// 这是"浪峰每秒走过多少米"，不是相位角速度：相位由它积分出来，所以改这个值只影响之后的推进速度，
    /// 不会像"历经时间乘速率"那样让整个波形瞬移。另外两道分量的角频率是它的 2 倍与 3 倍，
    /// 整数倍是为了让相位取模 2π 时完全无缝；它们的波长更短，相速度因此略快，正好符合深水色散的方向。
    /// </summary>
    public float SeaWaveSpeed { get; set; } = DefaultSeaWaveSpeed;

    /// <summary>空间天气配置。旧设置缺失此项时为空，由应用补齐场景默认区域。</summary>
    public WeatherRegionSettings[] WeatherRegions { get; set; } = [];

    /// <summary>区域模式不再让背景天空自动进入全局风暴；展示模式保留原循环。</summary>
    public WeatherMode WeatherMode { get; set; } = WeatherMode.Regional;

    static WorldSettings? _currentFallback;

    /// <summary>Runtime access entry point. Consumers should always read this property instead of the static Default* fields.</summary>
    public static WorldSettings Current
    {
        get
        {
            var world = DeviceServices.BaseApp?.Settings?.World;
            if (world != null)
                return world;
            return _currentFallback ??= new WorldSettings();
        }
    }
}
