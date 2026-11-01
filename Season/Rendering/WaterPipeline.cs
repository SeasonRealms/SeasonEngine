// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>单个水面的绘制请求：后端据此取帧缓冲里对应的槽位，并按 <see cref="Texture"/> 解析法线贴图。</summary>
/// <param name="Index">水面数据在本帧缓冲中的槽位，后端作为每 draw 常量传给着色器。</param>
/// <param name="Texture">法线贴图名；null 表示走程序化双正弦波。</param>
/// <param name="TilesX">P4 分块实例化的列数；不启用 Gerstner 或不分块时是 1。</param>
/// <param name="TilesZ">P4 分块实例化的行数；不分块时是 1。</param>
/// <param name="Subdiv">每块 tile 的细分段数；0 表示不分块，退回 6 顶点的整块矩形。</param>
public readonly record struct WaterDraw(int Index, string? Texture, int TilesX, int TilesZ, int Subdiv)
{
    /// <summary>本 draw 的顶点数：不分块是两个三角形，分块是每个 tile 的 subdiv² 个四边形。</summary>
    public int VertexCount => Subdiv > 0 ? Subdiv * Subdiv * 6 : 6;

    /// <summary>本 draw 的实例数：不分块是一次，分块是 tile 总数。</summary>
    public int InstanceCount => Subdiv > 0 ? TilesX * TilesZ : 1;
}

/// <summary>
/// 每帧交给后端的水面输入。与 <see cref="DecalInputs"/> 的关键差异是它有两份场景颜色：
/// <see cref="Color"/> 是颜色目标（写），<see cref="RefractionSource"/> 是开画前的不透明场景色快照（读）。
/// 同一资源不能既是附件又是采样源，所以折射必须靠这份快照，而不是直接读 <see cref="Color"/>。
/// </summary>
/// <param name="Color">本帧 SceneColor，只作为颜色目标；本 Pass 不挂深度附件。</param>
/// <param name="RefractionSource">开画前拷贝的场景色，与 <paramref name="Color"/> 同尺寸同格式。</param>
/// <param name="Depth">本帧 SceneDepth，只作为采样源，用于软件深度测试与厚度吸收。</param>
/// <param name="SkyView">P0 的程序化天空 Sky-View LUT 名；null 时反射退回常量天空色。</param>
/// <param name="FrameBuffer">帧头 + 水面数组的连续缓冲，布局见 <see cref="WaterMath"/>。</param>
/// <param name="Draws">按绘制次序排好的槽位表，前 <paramref name="Count"/> 项有效。</param>
public readonly record struct WaterInputs(RenderTarget Color, RenderTarget RefractionSource, RenderTarget Depth,
    string? SkyView, StorageBuffer FrameBuffer, WaterDraw[] Draws, int Count);

/// <summary>
/// P3 水面的屏幕空间反射参数：一张 water 表的全部水面共用，由共享层按本帧 Hi-Z 就绪状态填。
/// <see cref="Enabled"/> 为 false 时着色器完全不碰那四张全字塔纹理，退回纯天空反射
/// —— 后端的 Hi-Z 纹理名字可能压根析不出对象。
/// </summary>
/// <param name="Intensity">命中置信度的倍率；0 等价于关反射，1 是完全相信屏幕内命中。</param>
/// <param name="MaxDistance">射线在水面上能走多远（米）。超出这个距离的反射直接退回天空。</param>
/// <param name="Thickness">屏幕表面厚度容差（视空间米）。射线穿过薄物体时是否算命中就靠它。</param>
public readonly record struct WaterReflection(bool Enabled, float Intensity, float Near, float Far,
    float MaxDistance, float Thickness);

/// <summary>
/// P2 水面材质的后端入口。四个后端各自实现，共享层只负责剔除、排序与打包。
/// 返回 false 表示本帧放弃绘制（资源形状不符、能力缺失），调用方必须当成「没有水面」处理而不是部分绘制。
///
/// 为什么是独立管线而不是主着色器的一个材质分支：水面要采样 SceneColor 与 SceneDepth，
/// 而主着色器在透明段里拿到的这两张都是附件，同一资源不能同时是附件与采样源。独立 Pass 才能同时
/// 读（快照 + 深度）与写（场景色），并且顺带省掉深度附件 —— 遮挡改用着色器里的软件深度测试。
/// </summary>
public abstract class WaterPipeline : IDisposable
{
    public abstract bool Prepare(in WaterInputs inputs, RenderTarget destination);

    public abstract bool Draw();

    public abstract void Dispose();
}

/// <summary>
/// 水面帧缓冲的字节布局，四端与 C# 共享同一份定义；公开以便数学测试逐字段核对
/// （与 <see cref="DecalMath"/> 的约定一致）。
///
/// 全部按 <see cref="System.Numerics"/> 原始行序存放 float4：
/// <list type="bullet">
/// <item>0..3 反抖动的 VP（含后端 NDC 变换）的逆矩阵，用于从深度重建世界坐标与厚度；</item>
/// <item>4..7 反抖动的 VP（含后端 NDC 变换），顶点着色器用它把单位平面放进裁剪空间；</item>
/// <item>8 (相机世界坐标.xyz, 1/宽)；9 (宽, 高, 1/宽, 1/高)；</item>
/// <item>10 (水面数, 是否有降雨, 时间秒, 天空 LUT 是否可用)；
/// 11 (太阳方向.xyz, 太阳强度)，水面高光与天空反射的兜底色；</item>
/// <item>12 (屏幕反射是否启用, 反射强度倍率, 近平面, 远平面)，13 (最大射线距离, 厚度容差, 0, 0)；</item>
/// <item>16 起每个水面 <see cref="WaterStride"/> 个 float4：
/// 0..3 模型→世界，4 (tint.rgb, opacity)，
/// 5 (吸收, 折射强度, 法线强度, 法线平铺)，
/// 6 (滚动速度, 涟漪强度, fresnelF0, 粗糙度)，
/// 7 (是否有法线贴图, 排序键, UV 原点.x, UV 原点.z)，
/// 8..11 P4 的四个 Gerstner 波，每个一个 float4 (振幅, 波数, 方向.x, 方向.z)，
/// k &lt;= 0 的空槽在着色器里被跳过，所以风速为 0 时顶点完全不动；
/// 12 (分块列数, 分块行数, 细分段数, 水平位移系数)，细分段数为 0 表示不分块；
/// 13 (相速度倍率, 实际波数, 0, 0)。</item>
/// </list>
/// </summary>
public static class WaterMath
{
    public const int HeaderVectorCount = 16;
    public const int WaterOffset = HeaderVectorCount;

    /// <summary>
    /// 每个水面占的 float4 数。P2/P3 是 8；P4 把四个 Gerstner 波（±8..+11）与分块参数（±12..+13）
    /// 插进同一段，剩下的 +14/+15 留空。扩到 16 而不是 14，是为了让槽位地址始终是 2 的幂 ——
    /// 着色器里 <c>WaterOffset</c> 的乘法因此和旧版一样便宜，也方便以后继续往后加字段。
    /// </summary>
    public const int WaterStride = 16;
    public const int MaxWaters = SceneWaters.MaxActiveWaters;
    public const int VectorCount = WaterOffset + MaxWaters * WaterStride;
    public const int ByteCount = VectorCount * 16;

    /// <summary>Gerstner 波分量数，与帧布局里的 +8..+11 一一对应。</summary>
    public const int WaveCount = 4;

    /// <summary>
    /// 单个水面最多切成多少块 tile。它同时是 WebGPU 的编码基数：WGSL 没有 push constant，
    /// 槽位与 tile 只能挤在 <c>instance_index</c> 里（低 <see cref="TileBits"/> 位是 tile，高位是槽位），
    /// 所以这个数必须是 2 的幂。
    /// </summary>
    public const int MaxTilesPerWater = 256;

    /// <summary>由 <see cref="MaxTilesPerWater"/> 推出的低位数：WGSL 用实例号的低位取 tile，高位取槽位。</summary>
    public const int TileBits = 8;

    /// <summary>单轴 tile 数上限：16×16 正好是 <see cref="MaxTilesPerWater"/>，再密就要换方案了。</summary>
    public const int MaxTilesPerAxis = 16;

    /// <summary>
    /// 单 tile 的细分段数上限。与 <see cref="MaxTilesPerAxis"/> 合起来把单水面的顶点数封在
    /// 256×32²×6 ≈ 1.57M；实践中海面用 20，池塘不分块。
    /// </summary>
    public const int MaxSubdiv = 32;

    /// <summary>深水重力加速度（m/s²）。四端着色器里写死同一个值，只用于把波数换成角频率。</summary>
    public const float Gravity = 9.81f;

    /// <summary>把本帧的帧头与水面数组写进缓冲。调用方负责先按排序结果把水面表排好。</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, in Matrix4x4 inverseJitteredVp,
        Vector3 position, float width, float height, bool raining, float time, bool skyView,
        in WaterReflection reflection, Vector3 sunDirection, float sunIntensity, ReadOnlySpan<WaterPlane> waters)
    {
        if (data.Length < VectorCount) throw new ArgumentException("水面帧缓冲不足。", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, inverseJitteredVp);
        WriteMatrix(data, 4, jitteredVp);
        data[8] = new(position, width > 0 ? 1f / width : 0f);
        data[9] = new(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f);
        data[10] = new(waters.Length, raining ? 1 : 0, time, skyView ? 1 : 0);
        data[11] = new(sunDirection, sunIntensity);
        data[12] = new(reflection.Enabled ? 1f : 0f, reflection.Intensity, reflection.Near, reflection.Far);
        data[13] = new(reflection.MaxDistance, reflection.Thickness, 0f, 0f);
        int offset = WaterOffset;
        for (int i = 0; i < waters.Length; i++)
        {
            var water = waters[i];
            var uv = water.UvOrigin;
            WriteMatrix(data, offset, water.WorldMatrix);
            data[offset + 4] = new(water.Tint.X, water.Tint.Y, water.Tint.Z, water.Opacity);
            data[offset + 5] = new(water.Absorption, water.RefractionStrength, water.NormalStrength, water.NormalTiling);
            data[offset + 6] = new(water.ScrollSpeed, water.RippleStrength, water.FresnelF0, water.Roughness);
            data[offset + 7] = new(water.NormalTexture != null ? 1 : 0, water.SortOrder, uv.X, uv.Y);
            // P4：四个 Gerstner 波 + 分块参数。风浪关闭时 BuildWaves 把四位全写 0（着色器按 k<=0 跳过），
            // Tiling 同时把细分段数归零，后端因此会退回 6 顶点的单矩形 —— 与 P3 的平整水面逐像素一致。
            int waves = BuildWaves(data, offset + 8, water.WindSpeed, water.WindDirection,
                water.SwellHeight, WaveCount);
            var tiling = Tiling(water);
            data[offset + 12] = new(tiling.X, tiling.Z, tiling.Subdiv, water.Choppiness);
            data[offset + 13] = new(water.WaveSpeed, waves, 0f, 0f);
            offset += WaterStride;
        }
    }

    /// <summary>
    /// 本水面的 tile 网格与细分度。返回的 <c>Subdiv</c> 为 0 表示不分块，后端据此画 6 顶点的整块矩形。
    ///
    /// 风浪关闭时直接不分块：Gerstner 是唯一需要顶点密度的东西，没有它就没有理由为一个平面付
    /// 上百倍的顶点。tile 数量超限时也整块退回，不做「一半分块」—— 那样接缝会出现在画面中间。
    /// </summary>
    public static (int X, int Z, int Subdiv) Tiling(in WaterPlane water)
    {
        if (water.WindSpeed <= 0.01f || water.SwellHeight <= 0f || water.TileSize <= 0f || water.TileSubdiv <= 0)
            return (1, 1, 0);
        int x = (int)MathF.Ceiling(water.Size.X / water.TileSize);
        int z = (int)MathF.Ceiling(water.Size.Y / water.TileSize);
        x = Math.Clamp(x, 1, MaxTilesPerAxis);
        z = Math.Clamp(z, 1, MaxTilesPerAxis);
        if (x * z > MaxTilesPerWater) return (1, 1, 0);
        // 细分也钳一下：顶点数是 subdiv²×6×tiles，作者写错一个零就是几百万顶点，
        // 而着色器的顶点预算里没有任何其它东西会拦住它。
        return (x, z, Math.Clamp(water.TileSubdiv, 1, MaxSubdiv));
    }

    /// <summary>
    /// 按风速生成四个 Gerstner 分量的参数，写进 <c>water + 8 .. + 11</c>，返回实际写入的波数。
    ///
    /// 波长谱：完全发展海的主波长是 λ_peak = 2πU²/g（PM 谱峰），这里再乘 2 往长波侧推一档，
    /// 因为顶点端的几何只能承载长波 —— 短波交给像素端的高频细节，那里的密度不受网格限制。
    /// 四个分量按 1/1.55 的等比递减，权重按 1/1.45 递减，最后归一化到 <paramref name="swellHeight"/>/2：
    /// sin 的峰值是 1，所以峰谷差是振幅的两倍。
    ///
    /// 相速度不进帧缓冲：它由深水色散 ω = sqrt(g·k) 唯一确定，着色器里用同一常量现算，
    /// 两边不会因为各自插值出不同的频率。作者层的「速度」只是 <see cref="WaterPlane.WaveSpeed"/> 这个倍率。
    /// </summary>
    public static int BuildWaves(Span<Vector4> data, int offset, float windSpeed, float windDirection,
        float swellHeight, int count)
    {
        count = Math.Clamp(count, 0, WaveCount);
        if (windSpeed <= 0.01f || swellHeight <= 0f || count == 0)
        {
            data.Slice(offset, WaveCount).Clear();
            return 0;
        }

        // 主波长：PM 谱峰乘 2。下限 24m 是为了让顶点端至少有几何意义（tile 内格距通常 8~12m）。
        float longest = Math.Clamp(6.2831853f * windSpeed * windSpeed / Gravity * 2f, 24f, 320f);

        // 权重先求和再归一化：只取前 count 个分量，总振幅仍等于 swellHeight/2，不会因减波而变矮。
        Span<float> weight = stackalloc float[WaveCount];
        float weightSum = 0f;
        for (int i = 0; i < count; i++)
        {
            weight[i] = 1f / MathF.Pow(1.45f, i);
            weightSum += weight[i];
        }
        float peak = swellHeight * 0.5f;

        float wavelength = longest;
        for (int i = 0; i < count; i++)
        {
            float k = 6.2831853f / wavelength;
            float amplitude = peak * weight[i] / weightSum;
            // 方向绕主风向展开约 ±45°：完全平行的一组波峰在掠射角会读成一组规则条纹。
            float angle = windDirection + (i - (count - 1) * 0.5f) * 0.52f;
            data[offset + i] = new(amplitude, k, MathF.Cos(angle), MathF.Sin(angle));
            wavelength /= 1.55f;
        }
        return count;
    }

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);
}
