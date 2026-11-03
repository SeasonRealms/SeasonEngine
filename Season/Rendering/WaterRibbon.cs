// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// A control point on a river ribbon. A ribbon is a <b>horizontal</b> band of water: each control point gives the centerline
/// point, the half width of the water and the <b>flow direction</b> at that point. The backend expands the band on the fly from
/// these points (no vertex buffer), exactly like <see cref="WaterfallPoint"/> does for a curtain.
///
/// <see cref="Flow"/> uses only the horizontal components, and it is the direction the water <b>travels</b>: the band's lateral
/// axis is that direction rotated 90 degrees in the XZ plane, and the texture scrolls along it. Authoring the direction per
/// control point rather than deriving it from the polyline is what lets a bend keep an even width all the way through the turn
/// (a derived tangent would pinch the inside of a sharp bend), and it gives the author the only knob that matters for the
/// "segments must look like one river" requirement: neighbouring segments agree if their flow directions agree at the joint.
/// </summary>
/// <param name="Position">Centerline point (author coordinates, before the placement matrix); its Y is the water surface height.</param>
/// <param name="HalfWidth">Half width of the water band at this point (meters), must be positive.</param>
/// <param name="Flow">Water travel direction (horizontal); a zero vector degenerates to +Z in the backend.</param>
public readonly record struct WaterRibbonPoint(Vector3 Position, float HalfWidth, Vector3 Flow);

/// <summary>
/// Complete author data for one river ribbon. It is deliberately a <b>sibling</b> of <see cref="WaterfallSheet"/> rather than a
/// subclass: the two share the frame buffer layout (<see cref="WaterRibbonMath"/> mirrors <see cref="WaterfallMath"/> slot for
/// slot, so the four backends need no structural change) but they disagree on what the cross-section means, and a flat ribbon
/// has no ring, no thickness, no bow and no lip/foot.
///
/// Why not reuse <see cref="WaterPlane"/>: that one is an axis-aligned rectangle whose six vertices are computed from
/// SV_VertexID. A river has to follow a centerline with bends, so the geometry has to come from control points; this is the
/// same reason the waterfall curtain cannot be a rectangle.
///
/// Flow continuity across segments is a matter of three numbers, all of which the author must keep equal between neighbours:
/// <list type="bullet">
/// <item><see cref="FlowSpeed"/> in <b>meters per second</b> (not "tiles per second"), so two ribbons of different length scroll
/// at the same world speed;</item>
/// <item><see cref="Phase"/> in meters: the arc length from the river's source to this ribbon's first point. The shader samples
/// at <c>phase + arc - time * speed</c>, so at the joint both sides read the same world distance for every frame;</item>
/// <item>the shared control point itself, which must have the same position, half width and flow direction on both sides.
/// </list>
/// <see cref="River"/> wires all three automatically when it splits one centerline into selectable segments.
/// </summary>
public sealed class WaterRibbon
{
    internal SceneWaterRibbons? Owner;

    /// <summary>Whether this ribbon participates in drawing. When off, the whole ribbon costs nothing (not even the split test).</summary>
    public bool Enabled
    {
        get => _enabled && (EnabledProbe?.Invoke() ?? true);
        set => _enabled = value;
    }

    bool _enabled = true;

    /// <summary>
    /// External switch probe, injected by the river segment control (reads its Enable and Alpha).
    /// Same reasoning as <see cref="WaterfallSheet.EnabledProbe"/>: a hidden control has no Update timing to write back on.
    /// </summary>
    public Func<bool>? EnabledProbe { get; set; }

    /// <summary>
    /// Placement delegate: the control points stay in author (segment-local) coordinates and the local-to-world transform is
    /// evaluated here once per frame. null means the control points are already world coordinates.
    ///
    /// It is a delegate rather than a matrix for the same reason as <see cref="WaterfallSheet.Placement"/>: the panel writes
    /// PosX/PosY/PosZ every frame, and a snapshot would make the water lag the banks by one frame - which on a river reads as
    /// the water sliding out of its own channel.
    /// </summary>
    public Func<Matrix4x4>? Placement { get; set; }

    /// <summary>This frame's local-to-world matrix; the identity matrix when <see cref="Placement"/> is null.</summary>
    public Matrix4x4 Transform => Placement?.Invoke() ?? Matrix4x4.Identity;

    /// <summary>
    /// Control points, at least 2 and at most <see cref="WaterRibbonMath.MaxControlPoints"/>. The order is the flow order:
    /// the first point is v=0 and the last is v=1, and the arc length in between accumulates from point distances.
    /// </summary>
    public List<WaterRibbonPoint> Points { get; } = new();

    /// <summary>Water body color (linear HDR). Shallow river water: darker and less saturated than foam.</summary>
    public Vector3 WaterColor { get; set; } = new(0.09f, 0.28f, 0.31f);

    /// <summary>Foam color; used on the two banks and in the periodic foam bands carried along by the flow.</summary>
    public Vector3 FoamColor { get; set; } = new(0.90f, 0.96f, 0.98f);

    /// <summary>Base opacity where there is neither foam nor depth. A 13 cm channel shows its bed, so this stays low.</summary>
    public float Opacity { get; set; } = 0.62f;

    /// <summary>
    /// Flow speed in <b>meters per second</b>. Two ribbons must share this value to keep the current continuous across their
    /// joint; the shader multiplies by time and divides by the texture repeat length, so it is a real world speed.
    /// </summary>
    public float FlowSpeed { get; set; } = 0.75f;

    /// <summary>Amplitude multiplier of the normal perturbation. 0 samples no normal texture at all.</summary>
    public float NormalStrength { get; set; } = 0.35f;

    /// <summary>Baseline for the normal texture repeat across the band; the longitudinal repeat is fixed by the 3 m scroll length.</summary>
    public float NormalTiling { get; set; } = 1.6f;

    /// <summary>Strength of the B channel's periodic foam bands carried by the flow.</summary>
    public float FoamAmount { get; set; } = 0.55f;

    /// <summary>Proportion of the half width occupied by the foam bands on the two banks (0..0.5).</summary>
    public float EdgeFoam { get; set; } = 0.20f;

    /// <summary>Width of the alpha bite at the two banks (proportion of the half width); keeps the waterline off a hard line.</summary>
    public float EdgeSoftness { get; set; } = 0.16f;

    /// <summary>
    /// Water depth in meters, used only as a shading hint: the shader has no linear depth, so this scales the screen-space
    /// thickness ramp that drives the tint. It does not move any geometry - the surface height is the control points' Y.
    /// </summary>
    public float Depth { get; set; } = 0.13f;

    /// <summary>
    /// Arc length in meters from the river's source to this ribbon's first point. Together with <see cref="FlowSpeed"/> this is
    /// what makes the current continue across a joint instead of restarting at every selectable segment.
    /// </summary>
    public float Phase { get; set; }

    /// <summary>
    /// Columns across the band (2 is a single quad strip, 3..6 lets the ribbon follow a bend without faceting the waterline).
    /// Unlike <see cref="WaterfallSheet.Columns"/> these are not a ring: the first column is the left bank and the last is the right.
    /// </summary>
    public int Columns { get; set; } = 3;

    /// <summary>Number of longitudinal subdivision segments between every two control points (1..4).</summary>
    public int Subdiv { get; set; } = 2;

    /// <summary>Mask texture (R strands / G foam / B periodic bands / A organic edge). null means the procedural default.</summary>
    public string? MaskTexture { get; set; } = "Assets/WaterfallMask.png";

    /// <summary>Normal texture (RG slope). null means no perturbation.</summary>
    public string? NormalTexture { get; set; } = "Assets/WaterfallNormal.png";

    /// <summary>Flow texture (RG lateral drift, B speed multiplier). null means no drift.</summary>
    public string? FlowTexture { get; set; } = "Assets/WaterfallFlow.png";

    /// <summary>Same as <see cref="WaterPlane.SortOrder"/>: smaller values are drawn first, and the sort is stable.</summary>
    public float SortOrder { get; set; }

    /// <summary>Total arc length of the control point polyline (meters). Recomputed on demand, no refresh call needed.</summary>
    public float ArcLength
    {
        get
        {
            float total = 0f;
            for (int i = 1; i < Points.Count; i++)
                total += Vector3.Distance(Points[i - 1].Position, Points[i].Position);
            return total;
        }
    }

    /// <summary>This ribbon's vertex count in the shader: (rows-1)×(columns-1)×6, bit-for-bit identical to the DrawInstanced count.</summary>
    public int VertexCount => WaterRibbonMath.VertexCount(this);

    /// <summary>
    /// Copy the shape and material (not the placement, not the owner), for "same river shape placed twice".
    /// </summary>
    public void CopyFrom(WaterRibbon source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Bypass the property on purpose: it folds in the source instance's probe, which is "how it looks right now" rather
        // than the author's intent.
        _enabled = source._enabled;

        Points.Clear();
        Points.AddRange(source.Points);
        WaterColor = source.WaterColor;
        FoamColor = source.FoamColor;
        Opacity = source.Opacity;
        FlowSpeed = source.FlowSpeed;
        NormalStrength = source.NormalStrength;
        NormalTiling = source.NormalTiling;
        FoamAmount = source.FoamAmount;
        EdgeFoam = source.EdgeFoam;
        EdgeSoftness = source.EdgeSoftness;
        Depth = source.Depth;
        Phase = source.Phase;
        Columns = source.Columns;
        Subdiv = source.Subdiv;
        MaskTexture = source.MaskTexture;
        NormalTexture = source.NormalTexture;
        FlowTexture = source.FlowTexture;
        SortOrder = source.SortOrder;
    }
}

/// <summary>
/// Scene river registry, isomorphic to <see cref="SceneWaterfalls"/>. Kept separate so that "the river" and "the waterfall"
/// can be culled, packed and drawn by their own pipelines without either one spending slots on the other's data.
/// </summary>
public sealed class SceneWaterRibbons
{
    /// <summary>
    /// Upper limit on ribbons drawn at once. The river is split into selectable segments (straight runs and bends), and the
    /// whole course must fit in one draw table: <see cref="WaterfallMath.MaxSheets"/> is the shared ceiling because every
    /// backend sizes its per-draw state from it, and the Web build's frame bind group covers exactly the same 6400 bytes.
    /// Eight segments is what a source-to-sea river needs: a few straight runs plus the bends between them.
    /// </summary>
    public const int MaxActiveRibbons = 8;

    readonly List<WaterRibbon> _ribbons = new();
    readonly ReadOnlyCollection<WaterRibbon> _view;

    /// <summary>Construct an empty registry; the read-only view is allocated once so per-frame reads stay allocation-free.</summary>
    public SceneWaterRibbons() => _view = _ribbons.AsReadOnly();

    /// <summary>Ribbon table in registration order; the shared layer culls and sorts it every frame.</summary>
    public IReadOnlyList<WaterRibbon> Ribbons => _view;

    /// <summary>Number of currently enabled ribbons, used by the scheduler to decide whether to split the scene Pass.</summary>
    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _ribbons.Count; i++) if (_ribbons[i].Enabled) count++;
            return count;
        }
    }

    /// <summary>Register a ribbon; throws when it already belongs to another scene or the active limit would be exceeded.</summary>
    public void Add(WaterRibbon ribbon)
    {
        ArgumentNullException.ThrowIfNull(ribbon);
        if (ReferenceEquals(ribbon.Owner, this)) return;
        if (ribbon.Owner != null)
            throw new InvalidOperationException("The same water ribbon cannot be registered to multiple scenes at once.");
        if (ribbon.Enabled && ActiveCount >= MaxActiveRibbons)
            throw new InvalidOperationException($"Each scene supports at most {MaxActiveRibbons} active water ribbons.");
        _ribbons.Add(ribbon);
        ribbon.Owner = this;
    }

    /// <summary>Unregister a ribbon; false means it was not in this scene to begin with.</summary>
    public bool Remove(WaterRibbon ribbon)
    {
        if (!_ribbons.Remove(ribbon)) return false;
        ribbon.Owner = null;
        return true;
    }

    /// <summary>Clear the registry and detach ownership, for scene switching and test teardown.</summary>
    public void Clear()
    {
        for (int i = 0; i < _ribbons.Count; i++) _ribbons[i].Owner = null;
        _ribbons.Clear();
    }
}

/// <summary>
/// Byte layout of the river ribbon frame buffer.
///
/// It is <b>slot-for-slot identical</b> to <see cref="WaterfallMath"/> - same 16-vector header, same 48-vector slot stride, same
/// control point block at 0..31, same placement matrix at 38..41 - so the four backends reuse their existing root signature,
/// descriptor layout and Draw path unchanged and only swap the shader source. What differs is the <b>meaning</b> of a few
/// fields, listed here because a reader of the shader will look for it:
/// <list type="bullet">
/// <item>0..31 control points: (center.xyz, half width) and (flow.x, 0, flow.z, 0);</item>
/// <item>32 (point count, arc length in meters, max half width, texture flags);</item>
/// <item>33 (water color.rgb, opacity); 34 (foam color.rgb, foam amount);</item>
/// <item>35 (flow speed in m/s, normal strength, normal tiling, erosion);</item>
/// <item>36 (edge foam, edge softness, water depth in meters, 0);</item>
/// <item>37 (sort key, phase in meters, 0, 0) - the phase is what continues the current across segments;</item>
/// <item>38..41 placement matrix (local-to-world, row vectors).</item>
/// </list>
///
/// The unused tail of each field is zero, so a waterfall slot and a ribbon slot never alias: they live in two different
/// StorageBuffers that are never bound at the same time.
/// </summary>
public static class WaterRibbonMath
{
    /// <summary>Number of float4s in the frame header, equal to <see cref="WaterfallMath.HeaderVectorCount"/>.</summary>
    public const int HeaderVectorCount = WaterfallMath.HeaderVectorCount;

    /// <summary>First ribbon slot index (in float4s).</summary>
    public const int RibbonOffset = HeaderVectorCount;

    /// <summary>Float4s per ribbon, equal to <see cref="WaterfallMath.SheetStride"/> so the slot address stays 768-byte aligned.</summary>
    public const int RibbonStride = WaterfallMath.SheetStride;

    /// <summary>First float4 of the placement matrix inside a slot, same as <see cref="WaterfallMath.PlacementOffset"/>.</summary>
    public const int PlacementOffset = WaterfallMath.PlacementOffset;

    /// <summary>Control point limit, shared with the waterfall so one vertex shader shape covers both.</summary>
    public const int MaxControlPoints = WaterfallMath.MaxControlPoints;

    /// <summary>Column limit across the band; the waterfall's ring uses the same ceiling so the draw encoding matches.</summary>
    public const int MaxColumns = WaterfallMath.MaxColumns;

    /// <summary>Subdivision limit between control points, shared with the waterfall.</summary>
    public const int MaxSubdiv = WaterfallMath.MaxSubdiv;

    /// <summary>Ribbons drawable per frame, from the registry's own limit.</summary>
    public const int MaxRibbons = SceneWaterRibbons.MaxActiveRibbons;

    /// <summary>Total float4s of the whole frame buffer (header + all slots).</summary>
    public const int VectorCount = RibbonOffset + MaxRibbons * RibbonStride;

    /// <summary>Byte count of the whole frame buffer; the backend allocates its StorageBuffer from this.</summary>
    public const int ByteCount = VectorCount * 16;

    /// <summary>Texture flags, bit-identical to <see cref="WaterfallMath.FlagMask"/> and friends.</summary>
    public const int FlagMask = WaterfallMath.FlagMask;

    /// <summary>Flag: this ribbon samples the normal texture.</summary>
    public const int FlagNormal = WaterfallMath.FlagNormal;

    /// <summary>Flag: this ribbon samples the Flow texture.</summary>
    public const int FlagFlow = WaterfallMath.FlagFlow;

    /// <summary>Longitudinal length of one texture repeat in meters; the shader divides the flow offset by it.</summary>
    public const float ScrollRepeatMeters = 3f;

    /// <summary>Writes this frame's header and ribbon slots. The caller must have sorted the ribbon table first.</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, Vector3 camera, float width, float height,
        Vector3 sunDirection, Vector3 sunColor, float sunIntensity, Vector3 ambient, float ambientIntensity,
        float time, ReadOnlySpan<WaterRibbon> ribbons)
    {
        if (data.Length < VectorCount) throw new ArgumentException("Ribbon frame buffer is too small.", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, jitteredVp);
        data[4] = new(camera, time);
        data[5] = new(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f);
        data[6] = new(sunDirection, sunIntensity);
        data[7] = new(sunColor, 0f);
        data[8] = new(ambient, ambientIntensity);
        data[9] = new(ribbons.Length, 0f, 0f, 0f);

        int offset = RibbonOffset;
        for (int i = 0; i < ribbons.Length; i++)
        {
            var ribbon = ribbons[i];
            int count = Math.Clamp(ribbon.Points.Count, 0, MaxControlPoints);
            float arc = 0f;
            float maxHalfWidth = 0f;
            for (int p = 0; p < count; p++)
            {
                var point = ribbon.Points[p];
                float halfWidth = MathF.Max(point.HalfWidth, 0.02f);
                maxHalfWidth = MathF.Max(maxHalfWidth, halfWidth);
                data[offset + p * 2] = new(point.Position, halfWidth);

                // Same zero-vector fallback as the waterfall: normalize(0) is NaN and would throw the whole band out of clip space.
                var flow = new Vector3(point.Flow.X, 0f, point.Flow.Z);
                flow = flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.UnitZ;
                data[offset + p * 2 + 1] = new(flow.X, 0f, flow.Z, 0f);

                if (p > 0) arc += Vector3.Distance(ribbon.Points[p - 1].Position, point.Position);
            }

            int flags = 0;
            if (ribbon.MaskTexture != null) flags |= FlagMask;
            if (ribbon.NormalTexture != null) flags |= FlagNormal;
            if (ribbon.FlowTexture != null) flags |= FlagFlow;
            data[offset + 32] = new(count, arc, maxHalfWidth, flags);
            data[offset + 33] = new(ribbon.WaterColor, ribbon.Opacity);
            data[offset + 34] = new(ribbon.FoamColor, ribbon.FoamAmount);
            data[offset + 35] = new(ribbon.FlowSpeed, ribbon.NormalStrength, ribbon.NormalTiling, 0f);
            data[offset + 36] = new(ribbon.EdgeFoam, ribbon.EdgeSoftness, ribbon.Depth, 0f);
            // .y is the phase: the shader samples at phase + arc, which is what keeps the current continuous across a joint.
            data[offset + 37] = new(ribbon.SortOrder, ribbon.Phase, 0f, 0f);
            WriteMatrix(data, offset + PlacementOffset, ribbon.Transform);
            offset += RibbonStride;
        }
    }

    /// <summary>
    /// Vertex count of this ribbon: (rows-1)×(columns-1)×6. The backend must call the same function, otherwise the quads
    /// decoded from <c>SV_VertexID</c> would run past the end of the mesh into the neighbouring slot.
    /// </summary>
    public static int VertexCount(in WaterRibbon ribbon)
    {
        int count = Math.Clamp(ribbon.Points.Count, 2, MaxControlPoints);
        int columns = Math.Clamp(ribbon.Columns, 2, MaxColumns);
        int subdiv = Math.Clamp(ribbon.Subdiv, 1, MaxSubdiv);
        int rows = (count - 1) * subdiv + 1;
        return (rows - 1) * (columns - 1) * 6;
    }

    /// <summary>
    /// CPU-side row sampling, shape-identical to <c>RbRowCenter</c> / <c>RbRowFlow</c> in <c>WaterRibbonShaders.VertexHlsl</c>
    /// (same endpoint clamping, same Catmull-Rom coefficients, same zero-flow fallback).
    ///
    /// Both the bank mesh and the collision footprint use it, because they must
    /// coincide with the water actually drawn: a bank built from the raw control points would leave the water poking out of the
    /// outside of every bend, and a collision footprint likewise.
    /// </summary>
    /// <param name="points">Control point table.</param>
    /// <param name="s">Row parameter in control point index units (0..count-1); out-of-range values are clamped.</param>
    public static WaterRibbonRow SampleRow(ReadOnlySpan<WaterRibbonPoint> points, float s)
    {
        int count = points.Length;
        if (count == 0)
            return new WaterRibbonRow(Vector3.Zero, 0f, Vector3.UnitZ);

        float clamped = Math.Clamp(s, 0f, count - 1);
        int i1 = (int)MathF.Floor(clamped);
        float f = clamped - i1;
        int i0 = i1 > 0 ? i1 - 1 : 0;
        int i2 = Math.Min(i1 + 1, count - 1);
        int i3 = Math.Min(i1 + 2, count - 1);

        var centerRow = CatmullRom(
            Row(points[i0]), Row(points[i1]), Row(points[i2]), Row(points[i3]), f);
        var flowRow = CatmullRom(
            Flow(points[i0]), Flow(points[i1]), Flow(points[i2]), Flow(points[i3]), f);

        var flow = new Vector3(flowRow.X, 0f, flowRow.Z);
        flow = flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.UnitZ;

        return new WaterRibbonRow(new Vector3(centerRow.X, centerRow.Y, centerRow.Z), centerRow.W, flow);
    }

    /// <summary>Lateral axis from a flow direction: the flow turned 90 degrees in the XZ plane, normalized.</summary>
    public static Vector3 Lateral(in Vector3 flow) => new(flow.Z, 0f, -flow.X);

    /// <summary>Read a control point as "center.xyz + halfwidth.w", aligned with the GPU-side float4 layout.</summary>
    static Vector4 Row(in WaterRibbonPoint point) => new(point.Position, MathF.Max(point.HalfWidth, 0.02f));

    /// <summary>Read a control point as "flow.x, 0, flow.z, 0", matching what <see cref="Pack"/> writes (normalized, zero degenerating to +Z).</summary>
    static Vector4 Flow(in WaterRibbonPoint point)
    {
        var flow = new Vector3(point.Flow.X, 0f, point.Flow.Z);
        flow = flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.UnitZ;
        return new Vector4(flow.X, 0f, flow.Z, 0f);
    }

    /// <summary>Catmull-Rom basis, term-for-term identical to the HLSL form used by the waterfall.</summary>
    static Vector4 CatmullRom(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, float f)
        => 0.5f * (2f * p1 + (p2 - p0) * f
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f * f
            + (3f * p1 - p0 - 3f * p2 + p3) * f * f * f);

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, Vector4>(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);
}

/// <summary>
/// One row from <see cref="WaterRibbonMath.SampleRow"/>: centerline point, half width and that row's flow direction.
/// A CPU-side intermediate only; it never enters the frame buffer layout.
/// </summary>
/// <param name="Center">Centerline point (author coordinates, before the placement matrix).</param>
/// <param name="HalfWidth">Half width of the water band at this row (meters).</param>
/// <param name="Flow">Flow direction (horizontal, normalized).</param>
public readonly record struct WaterRibbonRow(Vector3 Center, float HalfWidth, Vector3 Flow);
