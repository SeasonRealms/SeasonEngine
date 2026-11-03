// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Season.Rendering.Effects;

namespace Engine.Panels;

/// <summary>
/// 瀑布潭：落水口下方的水面（<see cref="Season.Rendering.WaterPlane"/>）+ 潭底两张贴花 +
/// 一圈石块潭沿（复用 Rocks.glb 的实例化石块）+ 三组 GPU 粒子（水花 Splash / 雾 Mist / 白沫 Foam）。
/// 除水汽之外的全部锚点每帧从瀑布的落点控制点现算，
/// 所以拖动瀑布（Place 改变摆放矩阵）时潭与特效会跟着走，不需要两处维护坐标。
///
/// 落点锚定与瀑布同源：取 <see cref="WaterfallSheet.Points"/> 的最后一个控制点（v=1 落点），
/// 乘 <see cref="WaterfallSheet.Transform"/> 进世界。半宽用「变换两个端点再取距离的一半」反推，
/// 于是 Place 的各向异性缩放（15×14×13）与偏航都自动吸进来，不用镜像 Place 的公式。
///
/// 为什么伸进山体的矩形不用裁：水面 Pass 的像素着着色器有软件深度测试
/// （input.position.z 大于 SceneDepth 就 discard，见 WaterShaders），潭矩形朝山里的部分深度上在岩体后面，
/// 自然被剔掉；剩下的问题是矩形的四条直边 —— 前/侧两条由石块潭沿与白沫粒子压住缝，
/// 后边整个沉在岩体里不存在这条边。石头沿「落点椭圆的世界轴对齐包围盒」的边线摆，
/// 因为水面本来就是那个轴对齐矩形，石头贴着矩形边走才能压住直缝，贴椭圆边反而会露缝。
///
/// 粒子三组都走 ParticleSystemEffect 的矩形出生分支（SpawnRect，行 16..19 契约），
/// 注册与驱动方式逐字照抄 Campfire：ExternalTime + Advance 是唯一时钟，
/// RegisterCompute 拒绝时 Dispose 留 null，Update 里逐帧 Advance 并按瀑布 Enabled 门控。
/// 落地消亡用 DieOnFloor + FloorY=潭面：水花弧线落穿水面那一刻整帧起不再发射，就是「入水即灭」。
///
/// 水汽是唯一的例外：它的出生矩形不再从落点推导，而是来自可拾取的隐形盒控件 <see cref="Season.Controls.MistVolume"/>，
/// 与 <see cref="Season.Controls.Waterfall"/> 同一套路 —— 盒心/偏航/长宽深就是出生面与雾柱高度，
/// 雾的参数于是变成一个能在属性面板里拖、转、缩的对象（改完下一帧即生效）。
/// 白沫也是同一个盒子驱动的：它就是画面里那条最白的带子（盒子 Width = 带长，Depth = 带厚），
/// 所以盒子一动，雾与白沫一起动；水花仍绑在落点上。
/// 首帧只按潭面摆一套默认值；之后盒子平面位置跟着潭心走，直到用户在面板里动过 Pos，才永久脱钩交给用户。
///
/// 三组粒子都不设近景可见距离（ViewDistance = 0，即无限远）：那是一个以相机为圆心、超过就整簇淡出的固定半径，
/// 曾经的 50 / 55 m 只够站在潭边看。从天空俯视整座岛时相机到潭的距离在百米以上，固定半径会把水花、白沫与
/// 水汽一起抹成 0，画面里只剩一挂没有落点的水幕 —— 那不是「远处看不清」，而是地标凭空消失。
/// 远处的粒子和别处的几何一样交给视锥剔除与寿命淡出，不需要第二道以相机为心的门。
///
/// 粒子不吃场景光照（Particles 的 Shade 只算 Color.rgb * alpha，Color 就是 albedo）：
/// 所以 Update 每帧按太阳高度把作者色缩放一份写回去（见 <see cref="ApplyParticlesLighting"/>），
/// 水雾白天夜晚才有明暗差。瀑布水幕那边不需要这个补丁：WaterfallEffect 已经把
/// FrameSchedule.LightingSnapshot 的太阳与环景光包进 frame buffer（rows 6..8），片元里乘 ambient/direct。
///
/// 石块不自造盒子：几何与四张贴图来自 Rocks 面板后台抽出的 Assets/Rocks.glb 小变体，
/// 本面板只往那几张 InstancedMesh3D 追加实例、每帧写矩阵（拖动瀑布时石圈跟着走），
/// 于是潭沿与东岸共用一份几何与 GPU instancing，也顺带继承了 Rocks 的拾取与碰撞注册。
/// </summary>
internal class WaterfallPool : Panel
{
    /// <summary>潭水面高度。瀑布落点被 tunedSize 压到草地以下约 0.2 m，水膜盖在 y=0 草地上方 5 cm。</summary>
    const float WaterY = 0.05f;

    /// <summary>雾与场景雾同源的风（App.MistWind 同值），潭雾也朝这个方向漂，画面里风场才一致。</summary>
    static readonly Vector3 MistWind = new(0.30f, 0f, 0.10f);

    /// <summary>正午直射时的潭底焦散强度设计值（比 Pond 略低：潭更深、头顶还有水幕挡光）。</summary>
    const float CausticsIntensity = 0.38f;

    // 作者色（正午满亮时的设计值，任何时刻都不再改）：粒子着色器把 Color 直接当 albedo，
    // 完全不参与场景光照（见 Particles 的 Shade，只有 Color.rgb * alpha），所以三个系统每帧要按太阳高度
    // 缩放一份写回去（见 ApplyParticlesLighting），否则白天夜晚的观感完全一样。
    // 作者色供 River 的出海口水花与白沫同源复用（internal）：两处是同一类撞击效果，颜色只该有一份设计值。
    internal static readonly Vector4 SplashColor = new(0.82f, 0.93f, 0.98f, 0.75f);
    internal static readonly Vector3 SplashMid = new(0.90f, 0.97f, 1.00f);
    internal static readonly Vector3 SplashEnd = new(0.50f, 0.62f, 0.70f);

    // 雾的 alpha 取 0.07：0.05 太淡，Haze 几乎看不出来，Height 旋钮也就失去了意义。
    static readonly Vector4 MistColor = new(0.92f, 0.96f, 1.00f, 0.07f);
    static readonly Vector3 MistMid = new(0.95f, 0.97f, 1.00f);
    static readonly Vector3 MistEnd = new(1.00f, 1.00f, 1.00f);

    // 白沫 alpha 从 0.42 提到 0.52：这条带子是画面里最白的一笔，压得太透会露出后面的水与石头，
    // 看起来“虚而不实”。
    internal static readonly Vector4 FoamColor = new(0.95f, 0.98f, 1.00f, 0.52f);
    internal static readonly Vector3 FoamMid = new(0.92f, 0.96f, 0.99f);
    internal static readonly Vector3 FoamEnd = new(0.75f, 0.82f, 0.88f);

    /// <summary>夜间亮度下限：月光下水雾仍要看得出轮廓，而不是直接黑成剪影。</summary>
    const float NightParticleLight = 0.32f;

    /// <summary>被锚定的瀑布控件。控件那一侧（Place/代理网格）由 App 的 waterfalls 负责，这里只读形状与就绪状态。</summary>
    readonly Season.Controls.Waterfall waterfall;

    /// <summary>瀑布作者数据，即 waterfall.Sheet；形状与落点的唯一数据源。</summary>
    readonly Season.Rendering.WaterfallSheet sheet;

    internal readonly Season.Rendering.WaterPlane Water = new()
    {
        // 山潭比海水潭更暗更浑：本色压深、吸收加大，掠射角靠 Fresnel 自己提亮。
        Tint = new Vector4(0.045f, 0.16f, 0.20f, 1f),
        Opacity = 0.45f,
        Absorption = 2.6f,
        NormalStrength = 0.7f,
        ScrollSpeed = 0.02f,
        // 平静潭水：WindSpeed 保持 0，Gerstner 顶点位移整条关闭，只剩像素侧法线扰动。
        RippleStrength = 0.4f,
        Roughness = 0.05f,
    };

    Season.Rendering.DecalVolume silt;
    Season.Rendering.DecalVolume caustics;

    // 潭沿石块：不自己造盒子，改贴 Assets/Rocks.glb 的实例——几何/贴图由 Rocks 面板在后台一次性抽出
    // （15 个变体各一张 InstancedMesh3D），这里只往那几张模板里追加实例，于是同一份几何走 GPU instancing、
    // 同一套四贴图 PBR，还顺带进了 Rocks 的拾取与碰撞表。参数存在下面这张表里。
    readonly RimRock[] rimLayout;
    readonly MeshInstanceTransform?[] rimInstances;   // 注册（模板就绪）前为 null
    readonly InstancedMesh3D?[] rimFields;            // 每块石所属的变体模板，用于取模板局部尺寸
    bool rimRegistered;

    // Null when the backend declined compute support; the update loop then has nothing to tick.
    ParticleSystemEffect? splash;
    ParticleSystemEffect? mist;
    ParticleSystemEffect? foam;

    /// <summary>
    /// 水汽的可摆放体积：一个不可见的盒控件（与 Waterfall 同套路）。盒心与朝向、长宽高就是出生矩形，
    /// Height 决定粒子在寿命内爬多高 —— 于是「雾的参数」就是这一个可以在面板里拖、转、缩的盒子。
    /// 它作为普通 Mesh3D 登进 picker.Targets，因此有三角级拾取与 Bounds/Wireframe/Outline 三种高亮；
    /// 控件自己的默认高亮改成了 Wireframe（见 MistVolume 构造函数）：Bounds 是世界轴对齐包围盒，
    /// 转这个 8.8 × 2.4 m 的长盒子只会把 AABB 撑胖（ 45° 时约 8 × 8 m），“旋转”看上去像“放大”。
    /// </summary>
    internal readonly Season.Controls.MistVolume Mist = new("WaterfallMist");

    /// <summary>水汽盒是否已经摆过：第一帧（瀑布 Load 完）按潭面几何给一套默认值。</summary>
    bool mistPlaced;

    /// <summary>用户是否已在面板里改过水汽盒的位置：一旦改过就永久脱钩，不再被每帧的跟随写回覆盖。</summary>
    bool mistDetached;

    /// <summary>上一次写进水汽盒的中心点；下一帧与盒子的 Pos 比较，不等即说明用户动过它。</summary>
    Vector3 lastMistCenter;

    /// <summary>Only the systems that actually registered; a backend without compute leaves this empty.</summary>
    readonly List<ParticleSystemEffect> systems = new();

    /// <summary>单块潭沿石的作者参数：极角（0=正前，即 outward 方向）、世界高度（米）、偏航、外推距离、下沉量。</summary>
    readonly record struct RimRock(float AngleDegrees, float Height, float YawDegrees, float PushOut, float Sink);

    /// <summary>LeftPull：左半圈石块的横向右移量（米）。潭矩形左侧被岩壁吃掉，原样贴边摆会整块埋进山体，所以左半圈再往右推一点。</summary>
    const float LeftPull = 0.9f;

    internal WaterfallPool(Season.Controls.Waterfall waterfall)
    {
        this.waterfall = waterfall;
        this.sheet = waterfall.Sheet;

        // 石块布局：前向 ±114° 每 12° 一块共 20 块，块高与偏航用固定种子摇（与 Rocks 面板同一手法：布局可复现，
        // 改种子就整体换形）。块高直接用世界米数，再按模板包围盒反算缩放，等于不管资源里的石头多大都不会摆小。
        var rng = new Random(0x5A11BEEF);
        rimLayout = new RimRock[20];
        for (int i = 0; i < rimLayout.Length; i++)
        {
            float angle = -114f + i * 12f;
            // 中间几块矮一点，别挡住看水幕落点的视线；两侧的大石头负责盖角、把一圈缺口堵上。
            bool center = MathF.Abs(angle) < 40f;
            float height = (center ? 0.95f : 1.45f) + (float)rng.NextDouble() * (center ? 0.35f : 0.60f);
            float yaw = (float)rng.NextDouble() * 360f;
            rimLayout[i] = new RimRock(angle, height, yaw, 0.30f, 0.35f);
        }

        rimInstances = new MeshInstanceTransform[rimLayout.Length];
        rimFields = new InstancedMesh3D[rimLayout.Length];

        // 水汽盒：控件本身不可见，只提供矩阵与高亮；App 把它加进 picker.Targets（三角级拾取 + 属性面板）。
        AddControl(Mist);

        // 水面与贴花都是「数据注册」而不是控件：与 Pond/Sea 同路径，由 App 的注册表每帧被采集。
        App.Instance.Waters.Add(Water);

        // 潭底淤泥：Modulate 把草地压成深褐绿。投影盒以 y=0 草面为中心上下留余量（与 Pond 贴花同规矩：
        // 盒面恰好落在表面上会因深度重建误差 + TAA 抖动逐行翻转，表现为闪烁条纹）。
        silt = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Modulate,
            Texture = "Assets/DecalWetGrass.png",
            Position = new Vector3(0f, 0f, 0f),
            Size = new Vector3(10f, 0.3f, 10f),
            Tint = new Vector3(0.42f, 0.50f, 0.44f),
            Opacity = 0.9f,
            AngleFade = 0.15f,
            EdgeFade = 0.25f,
            AffectRoughness = true,
            Roughness = 0.15f,
            SortOrder = 0,
        };
        App.Instance.Decals.Add(silt);
        App.Instance.RegisterNamedTexture(silt.Texture);

        // 潭底焦散：Additive，压在 0.05 的水面之下。盒全高只给 0.08：草面在 0，顶面 0.04 卡在潭面以下，
        // 焦散不会浮到水面上画在空气里，也不会顺手把石块侧面下半截照成发光（与 Pond 同一约束：盒顶必须没入水下）。
        // 亮度与 Pond 焦散同源：每帧按阳光高度重算，夜里场景变暗后它才不会因对比度反而显得更亮。
        caustics = new Season.Rendering.DecalVolume
        {
            Mode = Season.Rendering.DecalMode.Additive,
            Texture = "Assets/DecalCaustics.png",
            Position = new Vector3(0f, 0f, 0f),
            Size = new Vector3(6f, 0.08f, 6f),
            Tint = new Vector3(0.80f, 0.95f, 1.00f),
            Opacity = 0.5f,
            AngleFade = 0.1f,
            EdgeFade = 0.3f,
            Intensity = CausticsIntensity,
            SortOrder = 1,
        };
        App.Instance.Decals.Add(caustics);
        App.Instance.RegisterNamedTexture(caustics.Texture);

        splash = Register(CreateSplash());
        mist = Register(CreateMist());
        foam = Register(CreateFoam());
    }

    /// <summary>
    /// 水花：从落点横幅整排斜向上抛出，锥形抖动 0.5，重力 -9.8，寿命 0.9 s，
    /// DieOnFloor 让落穿水面的那一瞬整帧起不再发射（入水即灭）。
    /// SizeEnd 需要 CurveMode 才生效（顶点端的 size lerp 在 curve.w 门内），所以白青色做成
    /// 出生近白、消亡转淡青的两段渐变；ToonEdge 与 CurveMode 无关，常开有效。
    /// </summary>
    ParticleSystemEffect CreateSplash() => new(1536)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110001,
        Density = 0.6f,
        SpawnRect = true,
        Lifetime = 0.9f,
        Acceleration = new Vector3(0f, -9.8f, 0f),
        ConeJitter = 0.42f,
        SpeedJitter = 0.20f,
        DieOnFloor = true,
        Stretch = 0.04f,
        ParticleSize = 0.05f,
        SizeEnd = 0.02f,
        Color = SplashColor,
        ColorMid = SplashMid,
        ColorEnd = SplashEnd,
        FadeIn = 0.05f,
        FadeOut = 0.30f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.3f,
        Turbulence = 0.15f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// 雾：出生矩形与身高完全由 <see cref="Mist"/>（可拾取的隐形盒）决定 —— 粒子从盒底面长出来，
    /// 上升初速按盒高反解，于是把盒子拉高就是把雾拉高。加速度向上 0.15、随场景风向漂移，寿命 3.2 s。
    /// 一簇淡雾：alpha 0.05 的低覆盖软雾团，0.45 起幅、1.25 终幅靠 CurveMode 的 SizeEnd 插值撑开，
    /// 噪声 0.6 让每团边缘不规则。密度和体量都压得小，雾只做水幕脚下的湿气，不能盖住瀑布本体。
    /// </summary>
    ParticleSystemEffect CreateMist() => new(256)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110002,
        Density = 0.40f,
        SpawnRect = true,
        Lifetime = 3.2f,
        Acceleration = new Vector3(0f, 0.15f, 0f),
        WindVelocity = MistWind,
        ConeJitter = 0.25f,
        ParticleSize = 0.45f,
        SizeEnd = 1.25f,
        Color = MistColor,
        ColorMid = MistMid,
        ColorEnd = MistEnd,
        FadeIn = 0.20f,
        FadeOut = 0.55f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.6f,
        Turbulence = 0.15f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// 白沫：落点前沿一条浅带出生，水平向外 0.8、上浮 0.3，寿命 1.6 s。
    /// 卡通云团：白色 alpha 0.52，尺寸 0.36→0.92，ToonEdge 0.30 把径向衰减压成清晰圆边，
    /// 一团团像挤出来的肥皂泡而不是灰烟。
    ///
    /// 密度：Density 是 GPU 侧的存活配额（容量 × Density），所以“更实”直接抬这里，不增加 draw call。
    /// 768 × 0.80 ≈ 610 个粒子铺在盒子底面（默认 8.8 × 2.4 m ≈ 21 m²）上，间距约 0.19 m，
    /// 而粒子自身 0.36 ~ 0.92 m，于是互相叠压成一条连续的白带；原来的 512 × 0.50 ≈ 256 个间距 0.29 m，
    /// 叠压不足，看到的就是一颗颗分开的白点。
    /// NoiseAmount 同时从 0.5 降到 0.35：噪声越大边缘越碎，白带会重新散回“点云”，与“更实”的诉求相反。
    /// </summary>
    ParticleSystemEffect CreateFoam() => new(768)
    {
        ExternalTime = true,
        CameraWindow = false,
        Seed = 0x5A110003,
        Density = 0.80f,
        SpawnRect = true,
        Lifetime = 1.6f,
        Acceleration = new Vector3(0f, 0.08f, 0f),
        SpeedJitter = 0.3f,
        ParticleSize = 0.36f,
        SizeEnd = 0.92f,
        Color = FoamColor,
        ColorMid = FoamMid,
        ColorEnd = FoamEnd,
        FadeIn = 0.08f,
        FadeOut = 0.32f,
        Additive = false,
        CurveMode = true,
        NoiseAmount = 0.35f,
        Turbulence = 0.25f,
        ToonEdge = 0.30f,
        ViewDistance = 0f,
    };

    /// <summary>
    /// Registers one particle system and returns it, or disposes it and returns null when the backend
    /// declines. Same contract as Campfire: RegisterCompute reports a declined Initialize as false and
    /// Initialize already tears its own resources down.
    /// </summary>
    ParticleSystemEffect? Register(ParticleSystemEffect effect)
    {
        if (Season.Rendering.FrameSchedule.RegisterCompute(Season.Basic.Graphics.Instance, effect))
        {
            systems.Add(effect);
            return effect;
        }

        effect.Dispose();
        return null;
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (sheet.Points.Count < 2)
            return result;

        // 瀑布控件还没 Load 完时 Transform 是单位矩阵，落点就是作者坐标（与最终位置差约 2 m）；
        // Load 完成那一帧起下面所有锚点自动对齐，不需要等门控或补一次初始化。
        var m = sheet.Transform;
        var last = sheet.Points[^1];
        var foot = Vector3.Transform(last.Position, m);

        // outward / lateral 必须在世界坐标里各自算：Place 是逐轴缩放（15×14×13），
        // 各向异性会把作者空间里互相垂直的两个基向量扭成斜交（这里实际偏了约 20°），
        // 所以不能拿 outward 转 90′ 去推 lateral，否则出生矩形与真实水幕横幅斜着相交，
        // 潭面也会被撑大一个量级。两者都各自过一遍变换再归一化才是真实几何。
        var rawOut = last.Outward;
        float rawLen = MathF.Sqrt(rawOut.X * rawOut.X + rawOut.Z * rawOut.Z);
        var outLocal = rawLen > 1e-6f
            ? new Vector3(rawOut.X / rawLen, 0f, rawOut.Z / rawLen)
            : new Vector3(0f, 0f, 1f);
        var latLocal = new Vector3(outLocal.Z, 0f, -outLocal.X);
        var outward = Horizontal(Vector3.TransformNormal(outLocal, m), new Vector3(0f, 0f, 1f));
        var lateral = Horizontal(Vector3.TransformNormal(latLocal, m), new Vector3(1f, 0f, 0f));

        // 横幅半宽的世界值：变换作者坐标里落点两侧端点再取半距，逐轴缩放全部吸进来。
        float hw = MathF.Max(last.HalfWidth, 0.5f);
        float halfWidthWorld = 0.5f * Vector3.Distance(
            Vector3.Transform(last.Position + latLocal * hw, m),
            Vector3.Transform(last.Position - latLocal * hw, m));

        // 潭面：横向 = 横幅半宽 + 白沫余量；纵向向前（水花落点）拿 1.15×半深，
        // 向后只凿进崖面 1 m —— 刚好把矩形后边埋进岩体里交给软件深度测试裁掉，不多占。
        float a = halfWidthWorld + 0.6f;
        float b = MathF.Max(2.5f, a * 0.55f);
        const float back = 1.0f;
        float front = b * 1.15f;
        var center = foot + outward * ((front - back) * 0.5f);

        // 斜交基张成的平行四边形的精确轴对齐包围盒：半宽 = 两边分别在轴上的投影长度相加。
        // （不能用 sqrt(u²+v²)：那条式子只对正交基成立。）
        float hx = a * MathF.Abs(lateral.X) + back * MathF.Abs(outward.X);
        float hz = a * MathF.Abs(lateral.Z) + back * MathF.Abs(outward.Z);

        Water.Position = new Vector3(center.X, WaterY, center.Z);
        Water.Size = new Vector2(2f * hx + 0.02f, 2f * hz + 0.02f);

        // 贴花盒跟着潭面走：淤泥盖满潭底，焦散只铺前面一半（水幕落点正前方最亮，朝山那侧埋着看不见）。
        silt.Position = new Vector3(center.X, 0f, center.Z);
        silt.Size = new Vector3(2f * hx - 1.2f, 0.3f, 2f * hz - 1.2f);
        caustics.Position = new Vector3(center.X + outward.X * front * 0.35f, 0f, center.Z + outward.Z * front * 0.35f);
        caustics.Size = new Vector3(
            MathF.Max(2f, 2f * hx * 0.55f), 0.08f,
            MathF.Max(2f, 2f * hz * 0.55f));
        caustics.Intensity = CausticsIntensity * App.SunlightFactor(App.Instance.DayPhase);

        // 水汽盒：首帧（瀑布 Load 完）按潭面给一套默认值，之后平面位置每帧跟随潭心写回；
        // 但盒子就绪后如果发现 Pos 与我们上一帧写入的值不一致，就说明用户在面板里动过它，
        // 从此永久脱钩 —— 于是「拖动瀑布时雾跟着走」与「雾可自由摆放」同时成立，且用户改动不会被覆盖。
        if (!mistPlaced)
        {
            if (waterfall.Ready)
            {
                const float mistHeight = 2.6f;
                // 默认只在落水口脚边铺一条带子（厚 2.4 m），不盖满整个水面：盒子就是那条白色水雾带本身，
                // 想铺满潭面就把 Depth 调大，想只留一道薄纱就把 Depth 调到 0.5。
                const float mistDepth = 2.4f;
                float yawDegrees = MathF.Atan2(-lateral.Z, lateral.X) * 180f / MathF.PI;
                PlaceMist(
                    new Vector3(center.X, WaterY + mistHeight * 0.5f, center.Z),
                    yawDegrees,
                    new Vector3(2f * a + 0.6f, mistHeight, mistDepth));
            }
        }
        else if (!mistDetached && Mist.Ready)
        {
            if (Mist.Center != lastMistCenter)
            {
                mistDetached = true;
            }
            else
            {
                // 只跟随平面位置：竖直方向沿用户给的 Height 把底面留在潭面上，偏航与长宽高全归用户。
                float mistHeightWorld = Mist.WorldHeight > 0f ? Mist.WorldHeight : 2.6f;
                lastMistCenter = new Vector3(center.X, WaterY + mistHeightWorld * 0.5f, center.Z);
                Mist.PosX = lastMistCenter.X;
                Mist.PosY = lastMistCenter.Y;
                Mist.PosZ = lastMistCenter.Z;
            }
        }

        // 石块沿轴对齐矩形的可见边线摆：从中心按极角发一条射线，求出它与矩形边的交点参数，
        // 再沿同方向外推一点压住水陆缝。水面是轴对齐矩形，所以石头贴矩形边而不是贴横幅。
        RegisterRimInstances();
        for (int i = 0; i < rimLayout.Length; i++)
        {
            var instance = rimInstances[i];
            if (instance == null)
                continue;   // 后台抽取还没给出模板：这一帧先不摆石头，就绪后自动补上。

            var layout = rimLayout[i];
            float rad = layout.AngleDegrees * MathF.PI / 180f;
            float side = MathF.Sin(rad);                    // -1 = 屏幕左（-lateral），+1 = 屏幕右（+lateral）
            var dir = lateral * side + outward * MathF.Cos(rad);
            float dirLen = dir.Length();
            if (dirLen < 1e-5f) dirLen = 1f;
            dir /= dirLen;
            float tX = MathF.Abs(dir.X) > 1e-4f ? hx / MathF.Abs(dir.X) : float.MaxValue;
            float tZ = MathF.Abs(dir.Z) > 1e-4f ? hz / MathF.Abs(dir.Z) : float.MaxValue;
            var edge = center + dir * MathF.Min(tX, tZ);

            // 左半圈额外往 +lateral（屏幕右）推一点：左侧贴矩形边摆的话整块会埋进岩壁，
            // 右半圈不推，于是“左边右移、右边不动”。
            float pull = side < 0f ? -side * LeftPull : 0f;

            instance.PosX = edge.X + dir.X * layout.PushOut + lateral.X * pull;
            instance.PosZ = edge.Z + dir.Z * layout.PushOut + lateral.Z * pull;
            instance.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, layout.YawDegrees * MathF.PI / 180f);
            instance.Enable = sheet.Enabled;

            // 以世界高度定标（而不是固定缩放）：不管 Rocks.glb 里这一档石头原本多大，摆出来都是 layout.Height 高；
            // 宽度/深度按模板长宽比等比例跟，锚点是包围盒中心，所以底边压到 -Sink 时中心在半高减 Sink。
            var local = rimFields[i]!.TemplateLocalSize;
            if (local == Vector3.Zero)
                continue;
            float scale = local.Y > 1e-4f ? layout.Height / local.Y : 1f;
            instance.Width = local.X * scale;
            instance.Height = local.Y * scale;
            instance.Depth = local.Z * scale;
            instance.PosY = instance.Height * 0.5f - layout.Sink;
        }

        // 粒子锚点每帧跟随：出生矩形都横在 lateral 轴上，WorldOrigin 直接落进世界坐标。
        // boxReady 同时是两个系统的开关门控（见下面 systems 循环）：雾与白沫的出生矩形都来自 MistVolume，
        // 盒子没摆过就没有可用的出生面，宁可整组关掉，也不能回到“原点爆一团”或“相机脚下冒雾”的旧行为。
        // 注意只看 Placed、不看 Ready：Place 已经把 Pos/Width/Height/Depth 写进字段（与包围盒无关），
        // 所以即使盒子的 GPU 网格还在加载队列里，粒子也能立即按它发射 —— 参数修改不需要等加载。
        bool boxPlaced = Mist.Placed;
        var axis2 = new Vector2(lateral.X, lateral.Z);
        if (splash != null)
        {
            splash.WorldOrigin = new Vector3(foot.X + outward.X * 0.25f, WaterY + 0.08f, foot.Z + outward.Z * 0.25f);
            splash.SpawnAxis = axis2;
            splash.SpawnHalfLength = a;
            splash.SpawnHalfDepth = 0.6f;
            splash.InitialVelocity = new Vector3(outward.X * 1.5f, 3.5f, outward.Z * 1.5f);
            splash.FloorY = WaterY;
        }

        if (boxPlaced)
        {
            // 雾与白沫完全跟随 MistVolume：盒心（xz）与底面就是出生平面，盒的 +X 轴就是横幅方向，
            // Width/Depth 直接就是出生矩形的长与深 —— 于是“可以叫白色的那团东西”的位置、大小、朝向
            // 全都能在属性面板里实时改，下一帧就作用到粒子上（compact 每帧按盒参数重建所有粒子的位置）。
            var boxAxis = Mist.SpawnAxis;
            var boxAxis2 = new Vector2(boxAxis.X, boxAxis.Z);
            var boxOrigin = new Vector3(Mist.PosX, Mist.BottomY, Mist.PosZ);

            if (mist != null)
            {
                // 雾：从盒底面长出来，Height 反解上升初速，让雾在寿命末爬到盒顶。
                mist.WorldOrigin = boxOrigin;
                mist.SpawnAxis = boxAxis2;
                mist.SpawnHalfLength = Mist.HalfLength;
                mist.SpawnHalfDepth = Mist.HalfDepth;
                mist.InitialVelocity = new Vector3(
                    outward.X * 0.25f,
                    RiseSpeed(Mist.WorldHeight, mist.Lifetime, mist.Acceleration.Y),
                    outward.Z * 0.25f);
            }

            if (foam != null)
            {
                // 白沫：就是画面里那条最白的雾带。铺满盒底面，所以 Depth 就是它的厚度（“厚度不够”直接调 Depth），
                // Width 就是它的长度；只向上浮一点点，不跟着盒子爬高。
                foam.WorldOrigin = new Vector3(boxOrigin.X, boxOrigin.Y + 0.02f, boxOrigin.Z);
                foam.SpawnAxis = boxAxis2;
                foam.SpawnHalfLength = Mist.HalfLength;
                foam.SpawnHalfDepth = Mist.HalfDepth;
                foam.InitialVelocity = new Vector3(outward.X * 0.8f, 0.3f, outward.Z * 0.8f);
            }
        }

        // 粒子不吃场景光照（PS 只算 Color.rgb * alpha，见 Particles 的 Shade），所以每帧按太阳高度
        // 把作者色缩放一份写回去：正午满亮、夜里压到 32%，与潭底焦散共用同一条太阳曲线，
        // 画面上水雾与焦散同明同暗。只乘 RGB，alpha（覆盖度）不随光影变，雾的体量才是稳定的。
        float light = ParticleLighting(App.Instance.DayPhase);
        if (splash != null) ApplyParticlesLighting(splash, SplashColor, SplashMid, SplashEnd, light);
        if (mist != null) ApplyParticlesLighting(mist, MistColor, MistMid, MistEnd, light);
        if (foam != null) ApplyParticlesLighting(foam, FoamColor, FoamMid, FoamEnd, light);

        // 水汽盒自身也是一个控件：Panel.Update 不递归，必须显式驱动一次，
        // 否则它的世界矩阵不会上传，选中了也画不出高亮框。
        if (Mist.Update(time))
            result = true;

        // 与 Campfire 同款驱动：帧 delta 夹在 [0, 0.1]，长卡顿不会把整个相位场瞬移过一整个回卷。
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;
        bool on = sheet.Enabled;
        for (int i = 0; i < systems.Count; i++)
        {
            var system = systems[i];
            // 雾与白沫的出生矩形都来自 MistVolume：盒子未就绪（还没 Load 完）那一帧起整组先关，
            // 否则它们会退回“没有 WorldOrigin 就跟相机”的旧行为（世界原点或镜头脚下爆一团）。
            bool boxDriven = system == mist || system == foam;
            bool systemOn = on && (boxPlaced || !boxDriven);
            system.Enabled = systemOn;
            if (systemOn)
                system.Advance(delta);
        }

        return result;
    }

    /// <summary>
    /// 初始化入口：在 App 里显式给水汽盒摆位，语义与 <see cref="Season.Controls.MistVolume.Place"/> 一致
    /// （<paramref name="center"/> 就是面板里的 Pos，<paramref name="size"/> 就是 Width/Height/Depth）。
    /// 不调用时本面板会在瀑布 Load 完的那一帧按潭面自动摆一套默认值。
    /// </summary>
    internal void PlaceMist(Vector3 center, float yawDegrees = 0f, Vector3? size = null)
    {
        Mist.Place(center, yawDegrees, size);
        lastMistCenter = center;
        mistDetached = false;
        mistPlaced = true;
    }

    /// <summary>
    /// 把 20 块潭沿石追加到 Rocks 面板的实例集：那是 Assets/Rocks.glb 抽出的小块变体，两边共用一份几何
    /// 与四张贴图，石的矩阵仍由本面板每帧写（拖动瀑布时石圈跟着走）。抽取是后台任务，
    /// <see cref="Rocks.rockFields"/> 完成前为 null，所以这里每帧试一次，成功后置位不再重试。
    /// </summary>
    void RegisterRimInstances()
    {
        if (rimRegistered)
            return;

        var rocks = App.Instance.rocks;
        if (rocks == null || rocks.rockFields == null || rocks.rockFields.Length == 0)
            return;   // 后台抽取未完成或失败：这一帧先不摆石头。

        var fields = rocks.rockFields;
        for (int i = 0; i < rimInstances.Length; i++)
        {
            // Rocks 的 Mesh.LogicalIndex 顺序是 5 大 + 5 中 + 5 小，潭沿取最小的一档；
            // 夹到现有变体数上，免得资源版本更少时越界。
            var field = fields[Math.Min(fields.Length - 1, 10 + i % 5)];
            var instance = new MeshInstanceTransform
            {
                Name = $"WaterfallPoolRock{i}",
                Enable = false,   // 等 Update 里按瀑布 Enabled 打开。
            };
            field.Instances.Add(instance);
            rimInstances[i] = instance;
            rimFields[i] = field;
        }

        rimRegistered = true;
    }

    /// <summary>
    /// 让水汽在寿命内爬到盒子顶附近：位移 = v·T + ½·a·T²，反解 v（再夹到实用区间防呆）。
    /// 于是 Height 就是“雾柱多高”，不必再单独调初速。
    /// </summary>
    internal static float RiseSpeed(float height, float lifetime, float accelY)
    {
        float t = MathF.Max(lifetime, 0.1f);
        return Math.Clamp((height - 0.5f * accelY * t * t) / t, 0.05f, 3f);
    }

    /// <summary>
    /// 粒子的“光照”：粒子是自发光色，不采样场景光照，所以只能自己把作者色按太阳高度缩放。
    /// 与潭底焦散共用 App.SunlightFactor 这条太阳曲线：正午 => 1.10，夜里 => NightParticleLight。
    /// 用下限而不是归零，是因为全黑的水雾看起来像掉帧的黑块，而不是月光下的白雾。
    /// </summary>
    internal static float ParticleLighting(float dayPhase)
    {
        float sun = App.SunlightFactor(dayPhase);
        return NightParticleLight + (1.10f - NightParticleLight) * sun;
    }

    /// <summary>把作者色乘上光照写回系统；只乘 RGB，alpha 不变（覆盖度不能跟着日光抖动）。</summary>
    internal static void ApplyParticlesLighting(ParticleSystemEffect system, Vector4 color, Vector3 mid, Vector3 end, float light)
    {
        system.Color = new Vector4(color.X * light, color.Y * light, color.Z * light, color.W);
        system.ColorMid = mid * light;
        system.ColorEnd = end * light;
    }

    /// <summary>水平归一化：各向异性缩放后的基向量先去掉竖直分量，退化时用兜底方向。</summary>
    static Vector3 Horizontal(Vector3 v, Vector3 fallback)
    {
        v.Y = 0f;
        float length = v.Length();
        return length > 1e-6f ? v / length : fallback;
    }
}
