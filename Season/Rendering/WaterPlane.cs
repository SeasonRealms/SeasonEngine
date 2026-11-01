// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// P2 水面的数据描述。一个水面就是一个轴对齐的水平矩形，中心在 <see cref="Position"/>、边长见 <see cref="Size"/>，
/// 法线恒为 +Y。几何由后端按 SV_VertexID 现算（6 顶点两个三角形），因此这里不携带任何网格数据。
///
/// 与 <see cref="DecalVolume"/> 的定位相同：作者层只写数据，渲染层每帧按需收集。区别在于水面没有体积，
/// 所以不需要局部盒坐标，着色器直接用世界坐标算 UV 与法线扰动。
/// </summary>
public sealed class WaterPlane
{
    internal SceneWaters? Owner;

    public bool Enabled { get; set; } = true;

    /// <summary>水面中心的世界坐标（矩形在 XZ 平面上，Y 即水面高度）。</summary>
    public Vector3 Position { get; set; }

    /// <summary>XZ 方向的世界尺寸（米）。</summary>
    public Vector2 Size { get; set; } = new(8f, 8f);

    /// <summary>水体本身的透射色：厚度趋近无穷时水底反射回来的颜色。</summary>
    public Vector4 Tint { get; set; } = new(0.055f, 0.20f, 0.235f, 1f);

    /// <summary>法线平整处的基础不透明度。Fresnel 会在掠射角把它推到接近 1。</summary>
    public float Opacity { get; set; } = 0.32f;

    /// <summary>吸收系数（1/米）。按 e^(-absorption * 厚度) 衰减，颜色取 <see cref="Tint"/> 的倒数光谱。</summary>
    public float Absorption { get; set; } = 2.2f;

    /// <summary>屏幕空间折射偏移的最大像素比例；0 关闭折射，直接采样正下方的水底。</summary>
    public float RefractionStrength { get; set; } = 0.035f;

    /// <summary>
    /// 法线扰动的幅度倍率。1 表示直接采用着色器里按真实小风浪标定出的梯度（约 3~4° 倾角），
    /// 0 是完全平整的水面（只剩 Fresnel 与雨涟漪），大于 1 用于夸张的「沸腾」水面。
    /// 注意这里的 1 对应的是梯度量纲，不是旧版的任意归一化值。
    /// </summary>
    public float NormalStrength { get; set; } = 1f;

    /// <summary>法线贴图的 UV 缩放：一个单位贴图在水面上重复的世界米数。</summary>
    public float NormalTiling { get; set; } = 0.12f;

    /// <summary>两张法线的反向滚动速度（UV/秒）。第二层用 1.37 倍频率和 0.8 倍速度，避免拍频后静止。</summary>
    public float ScrollSpeed { get; set; } = 0.035f;

    /// <summary>雨涟漪的幅度。实际幅度再乘以本帧是否有降雨（0/1），晴天完全不出现。</summary>
    public float RippleStrength { get; set; } = 0.9f;

    /// <summary>Fresnel 的正面反射率。水在 20°C 的 IOR 约 1.333，法向入射约 0.02。</summary>
    public float FresnelF0 { get; set; } = 0.02f;

    /// <summary>反射的粗糙度：0 = 镜面天空，越大越接近模糊的亮斑。</summary>
    public float Roughness { get; set; } = 0.06f;

    // ---- P4：Gerstner 位移与分块实例化 ----

    /// <summary>
    /// 风速（m/s）。0 关闭 Gerstner 顶点位移，退回 P3 的「纯像素端法线扰动」—— 水面依然平整，
    /// 只是没有任何几何起伏。这个值同时决定波长谱与相速度：波长按完全发展海的
    /// λ_peak = 2πU²/g 取主波，再按几何级数排出四个分量；相速度由深水色散 c = sqrt(g/k) 自动得出。
    /// </summary>
    public float WindSpeed { get; set; }

    /// <summary>风向（弧度，世界 XZ 平面内，0 = +X，π/2 = +Z）。四个波分量绕它展开 ±30°。</summary>
    public float WindDirection { get; set; }

    /// <summary>
    /// 峰谷总高（米）。四个波按权重分配振幅，和值为它的一半（因为 sin 的峰值是 1 而峰谷是 2A）。
    /// 这个值只作用于顶点位移；像素端的高频细节波不受它影响。
    /// </summary>
    public float SwellHeight { get; set; } = 1f;

    /// <summary>
    /// 水平位移系数。1 是标准 Gerstner（波峰变尖、波谷变平）；0 退化成纯垂直正弦（圆滑的浪）；
    /// 超过 1 会自交，只在需要夸张的「刀锋浪」时才用。
    /// </summary>
    public float Choppiness { get; set; } = 1f;

    /// <summary>
    /// 相速度倍率。1 = 物理相速度 sqrt(g/k)；调大让浪跑得更快，调小则更沉。
    /// 波的空间频率不受影响，所以它是纯「时间」参数，不会改变画面的波长结构。
    /// </summary>
    public float WaveSpeed { get; set; } = 1f;

    /// <summary>
    /// 分块实例化的一块 tile 的最大边长（米）。0 表示不分块：整块矩形只用 6 个顶点（P3 行为），
    /// 适合池塘这类小水面。海面必须分块，否则 1400m 的矩形只有 4 个角有顶点，Gerstner 无处可算。
    /// </summary>
    public float TileSize { get; set; }

    /// <summary>
    /// 每块 tile 的细分段数（tile 内是 subdiv×subdiv 个四边形）。0 与 <see cref="TileSize"/> 为 0 等价。
    /// 顶点数是 tilesX×tilesZ×subdiv²×6，所以它和 TileSize 一起决定顶点预算。
    /// </summary>
    public int TileSubdiv { get; set; }

    /// <summary>可选的法线贴图名。空表示用程序化双正弦波，不依赖任何资源。</summary>
    public string? NormalTexture { get; set; }

    /// <summary>同 <see cref="DecalVolume.SortOrder"/>：数值小的先画，稳定排序保证同值时按注册顺序。</summary>
    public float SortOrder { get; set; }

    /// <summary>模型→世界矩阵：把 [-0.5,0.5]² 的单位平面放大到 <see cref="Size"/> 并平移到 <see cref="Position"/>。</summary>
    public Matrix4x4 WorldMatrix => Matrix4x4.CreateScale(Size.X, 1f, Size.Y)
        * Matrix4x4.CreateTranslation(Position);

    /// <summary>着色器侧的世界空间 UV 原点，等于矩形的最小角；用于让两张法线不跟着矩形尺寸拉伸。</summary>
    public Vector2 UvOrigin => new(Position.X - Size.X * 0.5f, Position.Z - Size.Y * 0.5f);
}

/// <summary>
/// P2 场景水面注册表，与 <see cref="SceneDecals"/> 同构：作者层只管增删，渲染层每帧按需收集。
///
/// 上限按逐水面一次 6 顶点小 draw 计，不是显存上限。水面的实际绘制开销主要来自折射采样，
/// 与水面数量无关（每个水面都要按屏幕重新采样一次场景色），所以这个数字应当保守。
/// </summary>
public sealed class SceneWaters
{
    public const int MaxActiveWaters = 32;

    readonly List<WaterPlane> _waters = new();
    readonly ReadOnlyCollection<WaterPlane> _view;

    public SceneWaters() => _view = _waters.AsReadOnly();

    public IReadOnlyList<WaterPlane> Waters => _view;

    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _waters.Count; i++) if (_waters[i].Enabled) count++;
            return count;
        }
    }

    internal void EnsureActiveCapacity()
    {
        if (ActiveCount >= MaxActiveWaters)
            throw new InvalidOperationException($"每个场景最多支持 {MaxActiveWaters} 个活动水面。");
    }

    public void Add(WaterPlane water)
    {
        ArgumentNullException.ThrowIfNull(water);
        if (ReferenceEquals(water.Owner, this)) return;
        if (water.Owner != null)
            throw new InvalidOperationException("同一水面不能同时注册到多个场景。");
        if (water.Enabled) EnsureActiveCapacity();
        _waters.Add(water);
        water.Owner = this;
    }

    public bool Remove(WaterPlane water)
    {
        if (!_waters.Remove(water)) return false;
        water.Owner = null;
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < _waters.Count; i++) _waters[i].Owner = null;
        _waters.Clear();
    }
}
