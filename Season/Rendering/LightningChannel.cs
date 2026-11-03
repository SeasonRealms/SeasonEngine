// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// Visible channel geometry for a single strike: a polyline from the in-cloud breakdown point down to the
/// ground, plus several branches splitting off the main channel.
///
/// Why CPU geometry instead of GPU: the channel shape must be locked in on the frame of the strike, after
/// which the whole bolt only brightens and dims with the envelope and never changes shape again. The eye is
/// extremely sensitive to "a bolt regrowing every frame" -- that reads as jittering noise rather than a single
/// breakdown. A strike is roughly 150 segments, the generation cost is on the order of one array pass, and
/// strike intervals start at 2.6 seconds, so there is no reason to put this on the GPU.
///
/// Why midpoint displacement instead of splines: each level of midpoint displacement depends only on the
/// endpoints of the previous level, so it is naturally self-similar, letting "main channel / branch / twig"
/// share the same function with only the level count, amplitude, and direction constraints differing. Splines
/// require solving for a set of control points first, which is actually more expensive.
///
/// The geometry is 3D: the displacement direction is taken as a random direction in the plane perpendicular to
/// each segment, so the same channel holds up from any camera position without adjusting the channel plane for
/// the shot.
///
/// The consumer of the segment records is the particle ribbon vertex stage (see the contract in
/// <see cref="ParticleDraw"/> on lines 12/13): one <see cref="ParticleInstance"/> per segment, with Position
/// the segment midpoint, Velocity the half-segment vector, Size the metric half width, and Color.a the static
/// brightness weight of that segment. The per-frame envelope brightness is applied uniformly by the intensity
/// in frame buffer row 13 and is not written into the instance.
/// </summary>
public sealed class LightningChannel
{
    /// <summary>A ribbon segment: the two world-space endpoints, the metric half width, and a [0,1] static brightness weight.</summary>
    public readonly record struct Segment(Vector3 A, Vector3 B, float HalfWidth, float Brightness);

    /// <summary>Upper bound on the instance buffer. Main channel 64 + four branches at 16 each + four twigs at 8 each = 160, with headroom.</summary>
    public const int MaxSegments = 192;

    const int MainLevels = 6;
    const int MainPoints = (1 << MainLevels) + 1;
    const int BranchLevels = 4;
    const int BranchPoints = (1 << BranchLevels) + 1;
    const int TwigLevels = 3;
    const int TwigPoints = (1 << TwigLevels) + 1;

    /// <summary>Fraction of intra-cloud flashes: about a third of strikes stay in the cloud, leaving only an intra-cloud glow and no visible channel.</summary>
    const float IntraCloudChance = 0.30f;

    readonly Segment[] _segments = new Segment[MaxSegments];

    /// <summary>Number of segments generated. 0 means there is no visible channel this frame (an intra-cloud flash, or a channel not yet generated).</summary>
    public int Count { get; private set; }

    /// <summary>Ground strike point. Default for an intra-cloud flash, so callers must check <see cref="Visible"/> first.</summary>
    public Vector3 GroundPoint { get; private set; }

    /// <summary>In-cloud breakdown point; together with <see cref="GroundPoint"/> it gives the span of the channel.</summary>
    public Vector3 CloudPoint { get; private set; }

    /// <summary>Whether there is a visible channel. False for an intra-cloud flash, invalid parameters, or too small a span.</summary>
    public bool Visible => Count > 0;

    public ReadOnlySpan<Segment> Segments => _segments.AsSpan(0, Count);

    /// <summary>Clear the geometry but keep the array. Called when the weather leaves a thunderstorm or a strike is dismissed.</summary>
    public void Clear()
    {
        Count = 0;
        GroundPoint = default;
        CloudPoint = default;
    }

    /// <summary>
    /// Build the whole channel from the strike seed and the two endpoints. The same <paramref name="code"/>
    /// always yields the same geometry, so replay, recording, and debugging on both ends see the same bolt.
    /// </summary>
    /// <param name="code">Seed of the strike, from the same source as the flash envelope (region seed x strike serial).</param>
    /// <param name="cloudPoint">In-cloud breakdown point, usually the <see cref="WeatherVisualState.StrikePoint"/>.</param>
    /// <param name="cloudBase">Cloud base height: the channel is fully visible below this and gradually fades into the cloud above.</param>
    /// <param name="groundY">Ground height; the ground-strike endpoint lands on this plane.</param>
    /// <param name="baseHalfWidth">Metric half width at the ground; the channel narrows to 45% of this upward.</param>
    /// <returns>true if a visible channel was generated; false if this was an intra-cloud flash and the geometry was cleared.</returns>
    public bool Build(uint code, Vector3 cloudPoint, float cloudBase, float groundY, float baseHalfWidth)
    {
        Clear();
        if (!float.IsFinite(cloudPoint.X) || !float.IsFinite(cloudPoint.Y) || !float.IsFinite(cloudPoint.Z)
            || !float.IsFinite(cloudBase) || !float.IsFinite(groundY) || !float.IsFinite(baseHalfWidth))
            return false;

        float height = cloudPoint.Y - groundY;
        if (!(height > 20f)) return false;

        // Intra-cloud flash: it does not leave the cloud, so it should have no channel. The decision lives here
        // rather than in the caller because "what this bolt looks like" is the geometry's job; the caller only
        // needs to check Visible.
        if (Hash01(code ^ 0x1B873593u) < IntraCloudChance) return false;

        CloudPoint = cloudPoint;
        // Give the strike point some lateral offset: a real ground strike almost never lands exactly below the
        // breakdown point, and a perfectly vertical bolt looks like it was drawn with a ruler.
        uint h = code ^ 0x27D4EB2Fu;
        float lateral = height * 0.16f;
        GroundPoint = new(cloudPoint.X + (Hash01(h) * 2f - 1f) * lateral, groundY,
            cloudPoint.Z + (Hash01(h + 1u) * 2f - 1f) * lateral);

        Span<Vector3> pts = stackalloc Vector3[MainPoints];
        Subdivide(pts, cloudPoint, GroundPoint, height * 0.12f, code * 0x9E3779B9u + 0x85EBCA6Bu);

        // Depth to which the top fades into the cloud: when the cloud is thin, fall back to 6% of the channel
        // height; otherwise take the part of the breakdown point above the cloud base and widen it by half again,
        // so the channel "grows out" of the cloud gap instead of being cut off flat at the cloud base.
        float fadeSpan = MathF.Max((cloudPoint.Y - cloudBase) * 1.5f, height * 0.06f);
        float span = MathF.Max(cloudPoint.Y - groundY, 1e-3f);

        for (int i = 0; i < MainPoints - 1; i++)
        {
            Emit(pts[i], pts[i + 1], cloudPoint.Y, span, fadeSpan, baseHalfWidth, 1f);
        }

        // Branches split off the lower half of the main channel: the closer to the ground, the more likely a fork,
        // which is also the shape of a real ground strike.
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
            // Horizontal component relative to vertical: 0.35 is nearly straight down, 0.80 clearly slants out.
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

            // A twig, splitting again from the middle of the parent branch; only one level down, any more turns into noise.
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
    /// Distance from the channel to the nearest point to the listener. Thunder computes its delay and volume from
    /// this distance rather than from the in-cloud breakdown point: what the listener hears is the segment that
    /// arrives first, usually the lowest and nearest one. Returns positive infinity when there is no visible
    /// channel (intra-cloud flash), and the caller falls back to the breakdown point distance.
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
    /// Midpoint displacement: recursively subdivide [first point, last point] over <paramref name="levels"/> levels,
    /// each level pushing the new midpoint out along the perpendicular of that segment by <paramref name="amplitude"/>
    /// (attenuating to 55% per level). The endpoints stay fixed, so adjacent segments necessarily meet end to end.
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
                    // r is parallel to the segment: use a different fixed axis for the cross product; here any
                    // direction other than the segment direction is equivalent.
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
        // Thicker toward the ground: the current path at the breakdown point is brightest and widest near the
        // ground and tapers upward.
        float groundT = Math.Clamp((topY - midY) / span, 0f, 1f);
        float halfWidth = baseHalfWidth * (0.45f + 0.55f * groundT);
        // Fade out into the cloud at the top: brightness ramps linearly to zero by "how far above the cloud base",
        // and stays at 1 below the cloud base.
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
    /// Integer avalanche hash to [0,1). The scheduler (<see cref="WeatherVisualState"/>) and the geometry share
    /// the same formula: strike position, envelope, and channel shape are all derived from the same <c>code</c>,
    /// so changing any one will make the three disagree. Used only for shape generation, not for the render
    /// integral, so it needs no distribution properties.
    /// </summary>
    internal static float Hash01(uint value)
    {
        value ^= value >> 16; value *= 0x7FEB352Du;
        value ^= value >> 15; value *= 0x846CA68Bu;
        value ^= value >> 16;
        return (value & 0x00FFFFFFu) * (1f / 16777215f);
    }
}
