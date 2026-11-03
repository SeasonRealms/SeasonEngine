// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// A control point on a waterfall sheet. A sheet is neither a horizontal rectangle (<see cref="WaterPlane"/>) nor a zero-thickness quad,
/// but a body of water sliding down a cliff face: each control point gives the <b>section center</b>, half width and outward direction,
/// and the backend does Catmull-Rom interpolation along the control points in the vertex shader, then spreads a closed section ring around the center line.
///
/// <see cref="Outward"/> uses only the horizontal components: the sheet hugs the cliff face, the normal always points "away from the rock",
/// the vertical component is meaningless and keeping it would only skew the lateral tangent on steep sections of the flow. <see cref="HalfWidth"/> is the half width of that row's visible water band,
/// and it is the section's lateral semi-axis; for thickness see <see cref="WaterfallSheet.Thickness"/>.
/// Edges are left to the texture alpha to bite out an organic outline.
/// </summary>
/// <param name="Position">Midpoint of that row's sheet section (world coordinates, already including the gap from the rock face).</param>
/// <param name="HalfWidth">Half width of that row's water band (meters), must be positive.</param>
/// <param name="Outward">That row's outward direction (horizontal, world coordinates); a zero vector makes the backend degenerate to +Z.</param>
public readonly record struct WaterfallPoint(Vector3 Position, float HalfWidth, Vector3 Outward);

/// <summary>
/// Complete author data for one waterfall sheet. The geometry is computed on the fly by the backend from the control points (no vertex buffer),
/// so this only describes "shape + material", consistent with the role of <see cref="WaterPlane"/>.
///
/// The biggest difference from the water surface is that it is <b>not horizontal</b>: the control points are laid out downward along the cliff face, and each row spreads a closed section around the center line
/// (lateral semi-axis = that row's half width, longitudinal semi-axis = <see cref="Thickness"/> / 2), so seen head-on it is a water band and
/// seen from the side it is a water body with thickness - the same shape gives a different silhouette from a different viewing angle, and only on a cliff face does it not look like a piece of paper.
/// The section can be curved (control points with different outward directions), so a sheet can wrap around a protruding rock.
///
/// The texture is still mapped only once: u follows the section's <b>lateral coordinate</b> (not the ring parameter), so the texture on the front half is
/// pixel-for-pixel identical to the old single sheet, the back half is its mirror, and the seam falls on the two side edges and the back center, where the texture alpha has already bitten both to 0.
///
/// The R/G channels' texture is deliberately non-tileable: they describe the whole structure "from outlet to landing point", and u maps the sheet width only once.
/// Scrolling therefore does not use R/G, but B/A (periodic Worley / fbm) and the normal texture, avoiding seams when wrapping vertically.
/// </summary>
public sealed class WaterfallSheet
{
    internal SceneWaterfalls? Owner;

    /// <summary>
    /// Whether this sheet participates in drawing. Once off, not even the criterion for splitting the scene Pass is met, and the whole sheet costs nothing.
    /// Shared with the <see cref="Waterfall"/> control: when the control is hidden this also returns false, see <see cref="EnabledProbe"/>.
    /// </summary>
    public bool Enabled
    {
        get => _enabled && (EnabledProbe?.Invoke() ?? true);
        set => _enabled = value;
    }

    bool _enabled = true;

    /// <summary>
    /// Probe for the external switch, injected by the <see cref="Waterfall"/> control (reads the control's Enable and Alpha).
    ///
    /// A probe is used rather than having the control write back <see cref="Enabled"/> every frame: a write-back needs an occasion that is guaranteed to be called every frame,
    /// and when the control is hidden there is exactly no such occasion - which is precisely when the sheet needs to be turned off too.
    /// null means only the author-written <see cref="Enabled"/> is consulted.
    /// </summary>
    public Func<bool>? EnabledProbe { get; set; }

    /// <summary>
    /// Accessor delegate for the placement matrix: the control points remain the author's "reference coordinates", and the local-to-world transform is computed here once per frame.
    /// null means no placement (<see cref="Transform"/> is always the identity matrix) and the control points are world coordinates - this is the old P1 behavior,
    /// so usage that supplies only data and no delegate is unchanged word for word.
    ///
    /// A delegate is stored here rather than a matrix because the matrix must follow the control's position/orientation/scale for the current frame:
    /// the panel changes PosX/PosY/PosZ every frame, and if the matrix were a snapshot the sheet would lag by one frame.
    /// The same delegate is read once each by culling (<see cref="WaterfallEffect"/>) and packing (<see cref="WaterfallMath.Pack"/>),
    /// so both always see the same transform, and there is no misalignment of "drawn here but culled there".
    /// </summary>
    public Func<Matrix4x4>? Placement { get; set; }

    /// <summary>This frame's local-to-world matrix. The identity matrix when <see cref="Placement"/> is null.</summary>
    public Matrix4x4 Transform => Placement?.Invoke() ?? Matrix4x4.Identity;

    /// <summary>
    /// Control points, at least 2 and at most <see cref="WaterfallMath.MaxControlPoints"/>. The order must go from outlet to landing point:
    /// the shader treats the first point as v=0 (lip) and the last point as v=1 (landing point), and the arc length parameter in between is accumulated by the shared layer from point distances.
    /// </summary>
    public List<WaterfallPoint> Points { get; } = new();

    /// <summary>Body base color (linear HDR): brighter where thin, darker where thick, contrasting in brightness with the foam color.</summary>
    public Vector3 DeepColor { get; set; } = new(0.10f, 0.30f, 0.34f);

    /// <summary>Foam color. The cartoon look relies on it to "lift" the lip, landing point and edges out of the water body, so it is close to white rather than pure white.</summary>
    public Vector3 FoamColor { get; set; } = new(0.92f, 0.97f, 1.00f);

    /// <summary>Base opacity where there is no foam. A sheet is a translucent medium, and the rock behind it should show through a little.</summary>
    public float Opacity { get; set; } = 0.82f;

    /// <summary>
    /// Longitudinal scroll speed (sheet parameters/second). The actual sampling coordinate goes through an acceleration mapping:
    /// slow at the lip, fast at the landing point, so this value describes the "lip speed", and the landing point is roughly twice it.
    /// </summary>
    public float ScrollSpeed { get; set; } = 0.55f;

    /// <summary>Amplitude multiplier of the normal perturbation. 0 samples no normal texture at all, leaving only the geometric normal.</summary>
    public float NormalStrength { get; set; } = 0.55f;

    /// <summary>Baseline for the normal texture's longitudinal repeat count (counted per 6-meter segment); the actual count is determined by the arc length.</summary>
    public float NormalTiling { get; set; } = 2.5f;

    /// <summary>Strength of the G channel's white foam; at 0 only the B channel's scrolling foam bands and the lip/landing-point foam remain.</summary>
    public float FoamAmount { get; set; } = 0.85f;

    /// <summary>Proportion of the sheet length occupied by the lip foam band (v starts at 0).</summary>
    public float LipFoam { get; set; } = 0.10f;

    /// <summary>Proportion of the sheet length occupied by the landing-point foam band (v ends at 1).</summary>
    public float FootFoam { get; set; } = 0.10f;

    /// <summary>Proportion of the half width occupied by the foam bands on the two side edges (0..0.5).</summary>
    public float EdgeFoam { get; set; } = 0.22f;

    /// <summary>
    /// Edge softness width (proportion of the half width). The sheet geometry is wider than the visible water band, and the edges are bitten out by alpha;
    /// the larger this value the softer the outline, and the smaller it is the more it looks knife-cut. It is modulated by the A channel noise, so it is never a straight line.
    /// </summary>
    public float EdgeSoftness { get; set; } = 0.16f;

    /// <summary>Erosion strength of the A channel noise on the water body alpha. 0 is an intact water film, and 1 bites out a lot of holes.</summary>
    public float Erosion { get; set; } = 0.35f;

    /// <summary>
    /// Front bow amount (meters). Added only on the outward half of the section (cosθ&gt;0), making the front arc fuller than a pure ellipse;
    /// 0 is a pure elliptical section, and too large a value (&gt;0.4) pushes the front wall too far from the cliff face.
    /// </summary>
    public float Bow { get; set; } = 0.12f;

    /// <summary>
    /// Section thickness (meters, total rather than half). The water body spreads a closed section around the control point polyline:
    /// the front protrudes <see cref="Thickness"/> / 2 + <see cref="Bow"/>, the back sinks into the rock body by <see cref="Thickness"/> / 2, and the two sides still fall on the control point polyline.
    ///
    /// The two sides aligning with the old single sheet's position is deliberate: the cliff face is right there, and if the sides were changed to pull back, the edges originally hugging the rock face would be culled entirely by the software depth test
    /// as being "inside the rock", and the waterfall would appear to narrow for no reason. The thickness only grows backward (into the rock body) and bulges forward (the visible water body).
    ///
    /// A value of 0 degenerates into a nearly coincident double plane (the back half is projected onto the same plane), so do not use less than 0.05 in practice.
    /// </summary>
    public float Thickness { get; set; } = 1.0f;

    /// <summary>
    /// Ring column count (including the duplicated first/last column, so the quad count is <c>Columns-1</c> and the mesh closes automatically end to end).
    /// The column count determines how round the section is: at 6 you can see the polyhedron, at 12 the facets are already invisible. It only affects the outline and normal interpolation, not the texture density.
    /// Default 12: the front half then has 5 segments, comparable in coarseness to the old single sheet.
    /// </summary>
    public int Columns { get; set; } = 12;

    /// <summary>Number of longitudinal subdivision segments between every two control points (1..4). The total row count is (Points.Count-1)×Subdiv+1.</summary>
    public int Subdiv { get; set; } = 2;

    /// <summary>Mask texture name (R strands / G white foam / B periodic Worley / A periodic fbm). null means use the procedural default.</summary>
    public string? MaskTexture { get; set; } = "Assets/WaterfallMask.png";

    /// <summary>Normal texture name (RG slope, B outward). null means no perturbation.</summary>
    public string? NormalTexture { get; set; } = "Assets/WaterfallNormal.png";

    /// <summary>Flow texture name (RG lateral drift, B speed multiplier). null means no drift.</summary>
    public string? FlowTexture { get; set; } = "Assets/WaterfallFlow.png";

    /// <summary>Same as <see cref="WaterPlane.SortOrder"/>: smaller values are drawn first, and a stable sort keeps equal values in registration order.</summary>
    public float SortOrder { get; set; }

    /// <summary>Total sheet arc length (meters). Summed per frame from the control points, no manual refresh needed after the author changes points.</summary>
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

    /// <summary>This sheet's vertex count in the shader: (rows-1)×(columns-1)×6, bit-for-bit identical to the backend's DrawInstanced vertex count.</summary>
    public int VertexCount => WaterfallMath.VertexCount(this);

    /// <summary>
    /// Copy a full set of author data (shape + material + texture names + sort key), for "placing the same shape at multiple positions".
    /// Deliberately does not copy <see cref="Owner"/> and <see cref="Placement"/>: the former is registration ownership and the latter belongs to the consumer (the control);
    /// both are "where this sheet is now" rather than "what this sheet looks like".
    /// </summary>
    public void CopyFrom(WaterfallSheet source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Write the field directly rather than going through the property: the property is affected by the source instance's own probe (the control switch),
        // and copying a value that "happens to be false right now" would permanently extinguish the copy. What is wanted here is the author's intent, not the current frame's state.
        _enabled = source._enabled;

        // Clear first, then copy: this method is called CopyFrom, it overwrites rather than appends.
        Points.Clear();
        Points.AddRange(source.Points);
        DeepColor = source.DeepColor;
        FoamColor = source.FoamColor;
        Opacity = source.Opacity;
        ScrollSpeed = source.ScrollSpeed;
        NormalStrength = source.NormalStrength;
        NormalTiling = source.NormalTiling;
        FoamAmount = source.FoamAmount;
        LipFoam = source.LipFoam;
        FootFoam = source.FootFoam;
        EdgeFoam = source.EdgeFoam;
        EdgeSoftness = source.EdgeSoftness;
        Erosion = source.Erosion;
        Bow = source.Bow;
        Thickness = source.Thickness;
        Columns = source.Columns;
        Subdiv = source.Subdiv;
        MaskTexture = source.MaskTexture;
        NormalTexture = source.NormalTexture;
        FlowTexture = source.FlowTexture;
        SortOrder = source.SortOrder;
    }
}

/// <summary>
/// Scene waterfall registry, isomorphic to <see cref="SceneWaters"/>: the author layer only adds and removes, and the render layer collects on demand every frame.
///
/// The limit is counted as one draw per sheet: a sheet has only a few hundred vertices, and the real cost is the per-pixel texture sampling,
/// so this number is deliberately conservative -- 8 on screen is already a "waterfall cluster" rather than "one waterfall".
/// Slots are allocated at a fixed length (48 float4s each), so raising 4 to 8 adds only 3KB of frame buffer,
/// in exchange for being able to copy the same-shaped waterfall many times, each placed at its own position, without changing any backend code.
/// </summary>
public sealed class SceneWaterfalls
{
    /// <summary>Upper limit on active sheets on screen. When exceeded, <see cref="Add"/> throws rather than silently dropping.</summary>
    public const int MaxActiveWaterfalls = 8;

    readonly List<WaterfallSheet> _sheets = new();
    readonly ReadOnlyCollection<WaterfallSheet> _view;

    /// <summary>Construct an empty registry; the collection view is created once and reads allocate nothing each frame.</summary>
    public SceneWaterfalls() => _view = _sheets.AsReadOnly();

    /// <summary>Sheet table in registration order. The shared layer performs culling and sorting here every frame.</summary>
    public IReadOnlyList<WaterfallSheet> Waterfalls => _view;

    /// <summary>Number of currently enabled sheets. The scheduler uses it to decide whether to split the scene Pass for this view.</summary>
    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _sheets.Count; i++) if (_sheets[i].Enabled) count++;
            return count;
        }
    }

    internal void EnsureActiveCapacity()
    {
        if (ActiveCount >= MaxActiveWaterfalls)
            throw new InvalidOperationException($"Each scene supports at most {MaxActiveWaterfalls} active waterfalls.");
    }

    /// <summary>Register a sheet. The same one cannot be registered to two scenes; throws when the active count exceeds the limit.</summary>
    public void Add(WaterfallSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (ReferenceEquals(sheet.Owner, this)) return;
        if (sheet.Owner != null)
            throw new InvalidOperationException("The same waterfall cannot be registered to multiple scenes at once.");
        if (sheet.Enabled) EnsureActiveCapacity();
        _sheets.Add(sheet);
        sheet.Owner = this;
    }

    /// <summary>Unregister a sheet; returning false means it was not in this scene to begin with.</summary>
    public bool Remove(WaterfallSheet sheet)
    {
        if (!_sheets.Remove(sheet)) return false;
        sheet.Owner = null;
        return true;
    }

    /// <summary>Clear the registry and detach all ownership, used for scene switching and test teardown.</summary>
    public void Clear()
    {
        for (int i = 0; i < _sheets.Count; i++) _sheets[i].Owner = null;
        _sheets.Clear();
    }
}
