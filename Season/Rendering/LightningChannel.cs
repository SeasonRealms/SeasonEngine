// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// 一次落雷的可见通道几何：一条从云内击穿点向下到地面的折线，加上若干从主通道分叉出去的支路。
///
/// 为什么是 CPU 几何而不是 GPU：通道的形状必须在落雷那一帧确定下来，之后整道雷只随包络变亮变暗，
/// 不再改变形状。人眼对"闪电每帧重新长一遍"极其敏感——那样看起来是抖动的噪声而不是一道击穿。
/// 一次落雷大约 150 段，生成成本在一次数组遍历的量级上，而落雷间隔是 2.6 秒起，完全不必放进 GPU。
///
/// 为什么用中点位移而不是样条：中点位移的每一级只依赖上一级的端点，天然自相似，于是"主通道 / 分支 /
/// 子分支"可以共用同一个函数，只是级数、振幅和方向约束不同。样条需要先解一组控制点，反而更贵。
///
/// 几何是 3D 的：位移方向取每段的垂直平面内随机方向，所以同一条通道从任何机位看都成立，
/// 不需要为了镜头去调整通道平面。
///
/// 段记录的消费方是粒子 ribbon 顶点阶段（见 <see cref="ParticleDraw"/> 第 12/13 行的契约）：
/// 每段一个 <see cref="ParticleInstance"/>，Position 是段中点、Velocity 是半段向量、Size 是米制半宽、
/// Color.a 是该段的静态亮度权重。逐帧的包络亮度由帧缓冲 row 13 的强度统一乘上去，不写进实例。
/// </summary>
public sealed class LightningChannel
{
    /// <summary>一段带：世界坐标的两个端点、米制半宽、[0,1] 的静态亮度权重。</summary>
    public readonly record struct Segment(Vector3 A, Vector3 B, float HalfWidth, float Brightness);

    /// <summary>实例缓冲的上限。主通道 64 + 四条支路各 16 + 四条子支路各 8 = 160，留出余量。</summary>
    public const int MaxSegments = 192;

    const int MainLevels = 6;
    const int MainPoints = (1 << MainLevels) + 1;
    const int BranchLevels = 4;
    const int BranchPoints = (1 << BranchLevels) + 1;
    const int TwigLevels = 3;
    const int TwigPoints = (1 << TwigLevels) + 1;

    /// <summary>云内闪的比例：约三成的落雷不出云，只留下云内发光，不生成任何可见通道。</summary>
    const float IntraCloudChance = 0.30f;

    readonly Segment[] _segments = new Segment[MaxSegments];

    /// <summary>已生成的段数。0 表示这一帧没有可见通道（云内闪、或通道还没生成）。</summary>
    public int Count { get; private set; }

    /// <summary>地面落点。云内闪时为默认值，调用方必须先用 <see cref="Visible"/> 判断。</summary>
    public Vector3 GroundPoint { get; private set; }

    /// <summary>云内击穿点，与 <see cref="GroundPoint"/> 一起给出通道的跨度。</summary>
    public Vector3 CloudPoint { get; private set; }

    /// <summary>是否有可见通道。云内闪、参数非法、或跨度太小时为假。</summary>
    public bool Visible => Count > 0;

    public ReadOnlySpan<Segment> Segments => _segments.AsSpan(0, Count);

    /// <summary>清空几何，但保留数组。天气切出雷暴或落雷被遣散时调用。</summary>
    public void Clear()
    {
        Count = 0;
        GroundPoint = default;
        CloudPoint = default;
    }

    /// <summary>
    /// 由落雷种子与两个端点生成整条通道。同一个 <paramref name="code"/> 永远给出同一条几何，
    /// 于是重放、录制和两端调试看到的是同一道闪电。
    /// </summary>
    /// <param name="code">落雷的种子，与闪络包络同源（区域种子 × 落雷序号）。</param>
    /// <param name="cloudPoint">云内击穿点，通常就是 <see cref="WeatherVisualState.StrikePoint"/>。</param>
    /// <param name="cloudBase">云底高度：通道在这里以下完全可见，以上逐渐隐入云中。</param>
    /// <param name="groundY">地面高度，地闪的端点落在这个平面上。</param>
    /// <param name="baseHalfWidth">地面处的米制半宽，通道向上收窄到它的 45%。</param>
    /// <returns>true 表示生成了可见通道；false 表示这次是云内闪，几何已清空。</returns>
    public bool Build(uint code, Vector3 cloudPoint, float cloudBase, float groundY, float baseHalfWidth)
    {
        Clear();
        if (!float.IsFinite(cloudPoint.X) || !float.IsFinite(cloudPoint.Y) || !float.IsFinite(cloudPoint.Z)
            || !float.IsFinite(cloudBase) || !float.IsFinite(groundY) || !float.IsFinite(baseHalfWidth))
            return false;

        float height = cloudPoint.Y - groundY;
        if (!(height > 20f)) return false;

        // 云内闪：不出云，也就不该有通道。判定放在这里而不是调用方，是因为"这道雷看起来是什么样"
        // 本来就是几何的职责；调用方只需要看 Visible。
        if (Hash01(code ^ 0x1B873593u) < IntraCloudChance) return false;

        CloudPoint = cloudPoint;
        // 落点带一点横向偏移：真实地闪几乎不会正好落在击穿点的正下方，
        // 而完全垂直的一道闪电看起来像是被人拿尺子画的。
        uint h = code ^ 0x27D4EB2Fu;
        float lateral = height * 0.16f;
        GroundPoint = new(cloudPoint.X + (Hash01(h) * 2f - 1f) * lateral, groundY,
            cloudPoint.Z + (Hash01(h + 1u) * 2f - 1f) * lateral);

        Span<Vector3> pts = stackalloc Vector3[MainPoints];
        Subdivide(pts, cloudPoint, GroundPoint, height * 0.12f, code * 0x9E3779B9u + 0x85EBCA6Bu);

        // 顶部隐入云中的深度：云层薄时按通道高度的 6% 兜底，否则按击穿点高出云底的部分再放宽一半，
        // 让通道在云缝里"长出来"而不是在云底被一刀切断。
        float fadeSpan = MathF.Max((cloudPoint.Y - cloudBase) * 1.5f, height * 0.06f);
        float span = MathF.Max(cloudPoint.Y - groundY, 1e-3f);

        for (int i = 0; i < MainPoints - 1; i++)
        {
            Emit(pts[i], pts[i + 1], cloudPoint.Y, span, fadeSpan, baseHalfWidth, 1f);
        }

        // 支路从主通道的下半段分出去：越靠近地面越容易分叉，这也是真实地闪的形状。
        uint branchSeed = code ^ 0x68E31DA4u;
        int branches = 3 + (int)(Hash01(branchSeed) * 2f);
        Span<Vector3> branch = stackalloc Vector3[BranchPoints];
        Span<Vector3> twig = stackalloc Vector3[TwigPoints];
        for (int k = 0; k < branches && Count < MaxSegments; k++)
        {
            uint bh = code ^ (0x9E3779B9u * (uint)(k + 1));
            float t = 0.30f + 0.62f * Hash01(bh);
            int index = 1 + (int)(t * (MainPoints - 2));
            Vector3 origin = pts[index];

            float angle = Hash01(bh + 1u) * MathF.Tau;
            // 相对竖直方向的水平分量：0.35 接近直落，0.80 明显斜出。
            float tilt = 0.35f + 0.45f * Hash01(bh + 2u);
            Vector3 direction = Vector3.Normalize(
                new Vector3(MathF.Cos(angle) * tilt, -1f, MathF.Sin(angle) * tilt));
            float length = (origin.Y - groundY) * (0.35f + 0.35f * Hash01(bh + 3u));
            Vector3 end = origin + direction * length;
            if (end.Y < groundY + 4f) end.Y = groundY + 4f;

            Subdivide(branch, origin, end, length * 0.14f, bh * 0x85EBCA6Bu + 0x165667B1u);
            float branchWidth = baseHalfWidth * 0.55f;
            for (int i = 0; i < BranchPoints - 1 && Count < MaxSegments; i++)
            {
                Emit(branch[i], branch[i + 1], cloudPoint.Y, span, fadeSpan, branchWidth, 0.45f);
            }

            // 一条子支路，从父支路的中段再分出去；只分一层，再多就变成噪声了。
            if (Hash01(bh + 4u) > 0.45f) continue;
            Vector3 twigOrigin = branch[BranchPoints / 2];
            float twigAngle = angle + (Hash01(bh + 5u) * 2f - 1f) * 0.9f;
            Vector3 twigDirection = Vector3.Normalize(
                new Vector3(MathF.Cos(twigAngle) * 0.6f, -1f, MathF.Sin(twigAngle) * 0.6f));
            Vector3 twigEnd = twigOrigin + twigDirection * (length * 0.45f);
            if (twigEnd.Y < groundY + 4f) twigEnd.Y = groundY + 4f;

            Subdivide(twig, twigOrigin, twigEnd, length * 0.10f, bh * 0x27D4EB2Fu + 0x2545F491u);
            for (int i = 0; i < TwigPoints - 1 && Count < MaxSegments; i++)
            {
                Emit(twig[i], twig[i + 1], cloudPoint.Y, span, fadeSpan, baseHalfWidth * 0.3f, 0.28f);
            }
        }

        return Count > 0;
    }

    /// <summary>
    /// 通道上离听者最近的距离。雷声按这个距离而不是云内击穿点算延迟与音量：
    /// 听者听见的是最先到达的那一段，通常就是最低、最近的那一段。
    /// 没有可见通道（云内闪）时返回正无穷，调用方回退到击穿点距离。
    /// </summary>
    public float NearestDistance(Vector3 listener)
    {
        float best = float.PositiveInfinity;
        var span = _segments.AsSpan(0, Count);
        for (int i = 0; i < span.Length; i++)
        {
            float d = DistanceToSegment(listener, span[i].A, span[i].B);
            if (d < best) best = d;
        }
        return best;
    }

    static float DistanceToSegment(in Vector3 p, in Vector3 a, in Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-8f) return Vector3.Distance(p, a);
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>
    /// 中点位移：把 [第一个点, 最后一个点] 递归细分 <paramref name="levels"/> 级，
    /// 每一级把新中点沿该段的垂直方向推开 <paramref name="amplitude"/>（逐级衰减到 55%）。
    /// 端点保持不动，所以相邻段首尾必然相接。
    /// </summary>
    static void Subdivide(Span<Vector3> points, Vector3 start, Vector3 end, float amplitude, uint seed)
    {
        int levels = 0;
        while ((1 << levels) + 1 < points.Length) levels++;
        int count = (1 << levels) + 1;
        points[0] = start;
        points[count - 1] = end;

        uint state = seed | 1u;
        float offset = amplitude;
        int step = count - 1;
        while (step > 1)
        {
            int half = step >> 1;
            for (int i = half; i < count - 1; i += step)
            {
                Vector3 a = points[i - half], b = points[i + half];
                Vector3 mid = (a + b) * 0.5f;
                Vector3 segment = b - a;
                float length = segment.Length();
                if (length < 1e-4f) { points[i] = mid; continue; }

                Vector3 direction = segment / length;
                state = state * 1664525u + 1013904223u;
                Vector3 r = RandomUnit(state);
                Vector3 perpendicular = r - direction * Vector3.Dot(r, direction);
                float perpendicularLength = perpendicular.Length();
                if (perpendicularLength < 1e-4f)
                {
                    // r 与段平行：换一条固定轴做叉积，此时除段方向外任何方向都等价。
                    Vector3 axis = MathF.Abs(direction.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
                    perpendicular = Vector3.Cross(direction, axis);
                    perpendicularLength = perpendicular.Length();
                    if (perpendicularLength < 1e-4f) { points[i] = mid; continue; }
                }

                state = state * 1664525u + 1013904223u;
                float d = (Hash01(state) * 2f - 1f) * offset;
                points[i] = mid + perpendicular / perpendicularLength * d;
            }

            offset *= 0.55f;
            step = half;
        }
    }

    void Emit(in Vector3 a, in Vector3 b, float topY, float span, float fadeSpan,
        float baseHalfWidth, float brightness)
    {
        if (Count >= MaxSegments) return;
        Vector3 delta = b - a;
        if (delta.LengthSquared() < 1e-6f) return;

        float midY = (a.Y + b.Y) * 0.5f;
        // 越靠地面越粗：击穿点的电流路径在地面附近最亮最宽，向上逐渐收细。
        float groundT = Math.Clamp((topY - midY) / span, 0f, 1f);
        float halfWidth = baseHalfWidth * (0.45f + 0.55f * groundT);
        // 顶部在云里渐隐：亮度按"高出云底多少"线性到零，云底以下恒为 1。
        float fade = Math.Clamp((topY - midY) / fadeSpan, 0f, 1f);
        _segments[Count++] = new Segment(a, b, halfWidth, brightness * fade);
    }

    static Vector3 RandomUnit(uint value)
    {
        float z = Hash01(value) * 2f - 1f;
        float angle = Hash01(value ^ 0x2545F491u) * MathF.Tau;
        float r = MathF.Sqrt(MathF.Max(1f - z * z, 0f));
        return new(r * MathF.Cos(angle), z, r * MathF.Sin(angle));
    }

    /// <summary>
    /// 整数雪崩哈希取 [0,1)。调度（<see cref="WeatherVisualState"/>）与几何共用同一条公式：
    /// 落雷位置、包络和通道形状都从同一枚 <c>code</c> 派生，任何一处改动都会让三者对不上。
    /// 只用于生成形状，不参与渲染积分，不需要任何分布性质。
    /// </summary>
    internal static float Hash01(uint value)
    {
        value ^= value >> 16; value *= 0x7FEB352Du;
        value ^= value >> 15; value *= 0x846CA68Bu;
        value ^= value >> 16;
        return (value & 0x00FFFFFFu) * (1f / 16777215f);
    }
}
