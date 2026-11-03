// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;
using System.Runtime.InteropServices;

namespace Season.Rendering;

/// <summary>Draw request for a single waterfall: the backend uses it to fetch the corresponding slot from the frame buffer and resolve three textures by name.</summary>
/// <param name="Index">Slot of the sheet data in this frame buffer, passed by the backend to the shader as a root constant.</param>
/// <param name="Mask">Mask texture name; null means use the procedural default.</param>
/// <param name="Normal">Normal texture name; null means no perturbation.</param>
/// <param name="Flow">Flow texture name; null means no drift.</param>
/// <param name="Columns">Ring column count (including the duplicated first/last column), consistent with what the shared layer packs.</param>
/// <param name="Subdiv">Number of longitudinal subdivision segments between control points.</param>
/// <param name="VertexCount">Vertex count of this draw: (rows-1)×(columns-1)×6.</param>
public readonly record struct WaterfallDraw(int Index, string? Mask, string? Normal, string? Flow,
    int Columns, int Subdiv, int VertexCount);

/// <summary>
/// Per-frame waterfall input handed to the backend. Unlike <see cref="WaterInputs"/> it <b>has no refraction snapshot</b>:
/// a sheet is a translucent water film, and what is seen behind it is the scene color itself (with the water surface and fog already drawn), with no need for screen-space refraction,
/// so the color target is both the blend base and the only write target, and depth is used only as a sampling source for the software depth test.
/// </summary>
/// <param name="Color">This frame's scene color, used only as the color target; this Pass has no depth attachment.</param>
/// <param name="Depth">This frame's SceneDepth, used only as a sampling source to cull fragments occluded by cliff walls/trees.</param>
/// <param name="FrameBuffer">Contiguous buffer of frame header + sheet array, layout in <see cref="WaterfallMath"/>.</param>
/// <param name="Draws">Slot table ordered by draw order; the first <paramref name="Count"/> entries are valid.</param>
/// <param name="Count">Number of valid sheets this frame; the prefix comes from <paramref name="Draws"/>.</param>
public readonly record struct WaterfallInputs(RenderTarget Color, RenderTarget Depth,
    StorageBuffer FrameBuffer, WaterfallDraw[] Draws, int Count);

/// <summary>
/// Backend entry point for waterfall sheets. Isomorphic to <see cref="WaterPipeline"/>: the shared layer handles culling, sorting and packing,
/// while the backend only folds the frame buffer into descriptors and draws once inside <see cref="RenderPassId.SceneWaterfall"/>.
/// Returning false means this frame abandons drawing (resource shape mismatch, missing capability), and the caller must treat it as "no waterfall".
///
/// Why a separate pipeline rather than part of the water surface: the sheet geometry is a "band unwrapped along control points",
/// which cannot share a vertex path with a horizontal rectangle; the material also only partially overlaps with the water surface (scrolling normals, Flow, depth test),
/// and forcibly merging them into one chain would double the water surface's constants while the two almost never appear on the same pixel.
/// </summary>
public abstract class WaterfallPipeline : IDisposable
{
    /// <summary>Resolve resources outside the Pass, capture descriptor snapshots and transition state; returning false means this frame abandons.</summary>
    public abstract bool Prepare(in WaterfallInputs inputs, RenderTarget destination);

    /// <summary>Consumed once inside <see cref="RenderPassId.SceneWaterfall"/>; returning false means the contract check failed and this frame produced no output.</summary>
    public abstract bool Draw();

    /// <summary>Release the root signature, PSO and descriptor slots; native resources go through deferred reclamation.</summary>
    public abstract void Dispose();
}

/// <summary>
/// Byte layout of the waterfall frame buffer, shared verbatim across the four backends and C#.
///
/// All float4s are stored in <see cref="System.Numerics"/> raw row order:
/// <list type="bullet">
/// <item>0..3 the anti-jitter VP (including the backend NDC transform), used by the vertex shader to place world coordinates into clip space;</item>
/// <item>4 (camera world position.xyz, time in seconds); 5 (width, height, 1/width, 1/height);</item>
/// <item>6 (sun direction.xyz, sun intensity); 7 (sun color.rgb, 0); 8 (ambient color.rgb, ambient intensity);</item>
/// <item>9 (sheet count, 0, 0, 0); 10..15 reserved;</item>
/// <item>from 16, each sheet takes <see cref="SheetStride"/> float4s:
/// 0..31 are <see cref="MaxControlPoints"/> control points, two float4 per point
/// (0 = section midpoint.xyz + half width, 1 = outward direction.x, 0, .z, 0), unused rows are zero;
/// 32 (control point count, arc length in meters, max half width, texture flags);
/// 33 (body color.rgb, opacity); 34 (foam color.rgb, foam amount);
/// 35 (scroll speed, normal strength, normal tiling, erosion amount);
/// 36 (lip foam, foot foam, edge foam, edge softness);
/// 37 (sort key, lateral bow, section thickness, 0);
/// 38..41 placement matrix (local-to-world, row vectors), see <see cref="PlacementOffset"/>; 42..47 reserved.</item>
/// </list>
///
/// The slot stride is 48 (rather than the tightly packed 43) for two reasons: the control point section is exactly an integer run of 32 float4s,
/// and 48 is a multiple of 16, so slot addresses are always aligned to 768 bytes - dynamic indexing in the vertex shader therefore never crosses a cache line.
/// </summary>
public static class WaterfallMath
{
    /// <summary>Number of float4s occupied by the frame header (0..15): matrices, camera, screen, lighting and sheet count.</summary>
    public const int HeaderVectorCount = 16;

    /// <summary>Starting slot index of the first sheet (in float4s), equal to <see cref="HeaderVectorCount"/>.</summary>
    public const int SheetOffset = HeaderVectorCount;

    /// <summary>Number of float4s each sheet occupies. 48 is a multiple of 16, so slot addresses are always aligned to 768 bytes.</summary>
    public const int SheetStride = 48;

    /// <summary>
    /// Starting float4 index of the placement matrix within the slot. It occupies four float4s at 38..41, exactly the first half of the reserved section,
    /// so moving "local-to-world" from a shader constant to per-sheet data changed neither the slot capacity nor the root signature.
    /// </summary>
    public const int PlacementOffset = 38;

    /// <summary>Control point limit. The P0 probe gave 12 rows, leaving a margin of 4 rows for longer waterfalls.</summary>
    public const int MaxControlPoints = 16;

    /// <summary>Number of float4s for control points in the slot: one each for "midpoint + half width" and "outward direction" per point.</summary>
    public const int ControlVectorCount = MaxControlPoints * 2;

    /// <summary>
    /// Ring column count limit. Columns are now the columns around the section (including the duplicated first/last column), so it is the section precision:
    /// 12 segments already hide the facets, 16 is a margin for "a rounder close-up", and more would just waste vertices.
    /// </summary>
    public const int MaxColumns = 16;

    /// <summary>Upper limit on subdivision segments between control points.</summary>
    public const int MaxSubdiv = 4;

    /// <summary>Maximum drawable sheets per frame, from the same source as <see cref="SceneWaterfalls.MaxActiveWaterfalls"/>.</summary>
    public const int MaxSheets = SceneWaterfalls.MaxActiveWaterfalls;

    /// <summary>Total number of float4s in the whole frame buffer (header + all slots).</summary>
    public const int VectorCount = SheetOffset + MaxSheets * SheetStride;

    /// <summary>Byte count of the whole frame buffer; the backend allocates a StorageBuffer based on it.</summary>
    public const int ByteCount = VectorCount * 16;

    /// <summary>Texture flags: bit0 Mask, bit1 Normal, bit2 Flow. When 0 the shader skips the corresponding sampling entirely.</summary>
    public const int FlagMask = 1;

    /// <summary>Flag: this sheet samples the normal texture.</summary>
    public const int FlagNormal = 2;

    /// <summary>Flag: this sheet samples the Flow texture.</summary>
    public const int FlagFlow = 4;

    /// <summary>Write this frame's header and sheet array into the buffer. The caller is responsible for having sorted the sheet table first.</summary>
    public static void Pack(Span<Vector4> data, in Matrix4x4 jitteredVp, Vector3 camera, float width, float height,
        Vector3 sunDirection, Vector3 sunColor, float sunIntensity, Vector3 ambient, float ambientIntensity,
        float time, ReadOnlySpan<WaterfallSheet> sheets)
    {
        if (data.Length < VectorCount) throw new ArgumentException("Waterfall frame buffer is too small.", nameof(data));
        data.Clear();
        WriteMatrix(data, 0, jitteredVp);
        data[4] = new(camera, time);
        data[5] = new(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f);
        data[6] = new(sunDirection, sunIntensity);
        data[7] = new(sunColor, 0f);
        data[8] = new(ambient, ambientIntensity);
        data[9] = new(sheets.Length, 0f, 0f, 0f);

        int offset = SheetOffset;
        for (int i = 0; i < sheets.Length; i++)
        {
            var sheet = sheets[i];
            int count = Math.Clamp(sheet.Points.Count, 0, MaxControlPoints);
            float arc = 0f;
            float maxHalfWidth = 0f;
            for (int p = 0; p < count; p++)
            {
                var point = sheet.Points[p];
                float halfWidth = MathF.Max(point.HalfWidth, 0.02f);
                maxHalfWidth = MathF.Max(maxHalfWidth, halfWidth);
                data[offset + p * 2] = new(point.Position, halfWidth);

                // The outward direction uses only the horizontal components; a zero vector degenerates to +Z, so the consequence of "forgetting to fill it" is a fixed orientation
                // rather than the NaN produced by normalize(0) throwing the whole sheet outside clip space.
                var outward = new Vector3(point.Outward.X, 0f, point.Outward.Z);
                outward = outward.LengthSquared() > 1e-6f ? Vector3.Normalize(outward) : new Vector3(0f, 0f, 1f);
                data[offset + p * 2 + 1] = new(outward.X, 0f, outward.Z, 0f);

                if (p > 0) arc += Vector3.Distance(sheet.Points[p - 1].Position, point.Position);
            }

            int flags = 0;
            if (sheet.MaskTexture != null) flags |= FlagMask;
            if (sheet.NormalTexture != null) flags |= FlagNormal;
            if (sheet.FlowTexture != null) flags |= FlagFlow;
            data[offset + 32] = new(count, arc, maxHalfWidth, flags);
            data[offset + 33] = new(sheet.DeepColor, sheet.Opacity);
            data[offset + 34] = new(sheet.FoamColor, sheet.FoamAmount);
            data[offset + 35] = new(sheet.ScrollSpeed, sheet.NormalStrength, sheet.NormalTiling, sheet.Erosion);
            data[offset + 36] = new(sheet.LipFoam, sheet.FootFoam, sheet.EdgeFoam, sheet.EdgeSoftness);
            // .z is the section thickness: the vertex shader uses it to expand a center line into a water body with thickness.
            data[offset + 37] = new(sheet.SortOrder, sheet.Bow, sheet.Thickness, 0f);
            // Placement matrix: Identity when null, fully equivalent to old data that only provided control points.
            // Written here rather than uploaded by the backend, so that "driver" and "data" share the same source:
            // the bounding box used for culling and the transform used by the shader both come from the same sheet.Transform.
            WriteMatrix(data, offset + PlacementOffset, sheet.Transform);
            offset += SheetStride;
        }
    }

    /// <summary>
    /// Vertex count of this sheet. The row count is determined by the control point count and subdivision segments, and the column count is given by the author;
    /// the backend must issue vertices with the same function, otherwise the quad indices from <c>SV_VertexID</c> would run outside the mesh.
    /// </summary>
    public static int VertexCount(in WaterfallSheet sheet)
    {
        int count = Math.Clamp(sheet.Points.Count, 2, MaxControlPoints);
        int columns = Math.Clamp(sheet.Columns, 2, MaxColumns);
        int subdiv = Math.Clamp(sheet.Subdiv, 1, MaxSubdiv);
        int rows = (count - 1) * subdiv + 1;
        return (rows - 1) * (columns - 1) * 6;
    }

    /// <summary>
    /// CPU-side row interpolation: shape-identical to <c>WfRowCenter</c> and <c>WfRowOutward</c> in <c>WaterfallShaders.VertexHlsl</c>
    /// (same endpoint clamping, same Catmull-Rom coefficients, half width is interpolated along with it,
    /// and the interpolated outward direction is reduced to its horizontal components and normalized, with a zero vector degenerating to +Z).
    ///
    /// Only the proxy mesh (picking, outline, bounding box) uses it: that mesh must coincide with the band actually drawn,
    /// otherwise the outline the user sees would be offset from the water body by half a meter. The control points themselves lie on this curve, so taking control points row by row
    /// is not enough - the bow between two control points lies entirely outside the chord.
    /// </summary>
    /// <param name="points">Control point table.</param>
    /// <param name="s">Row parameter, in units of control point index (0..count-1); out-of-range values are clamped automatically.</param>
    public static WaterfallRow SampleRow(ReadOnlySpan<WaterfallPoint> points, float s)
    {
        int count = points.Length;
        if (count == 0)
            return new WaterfallRow(Vector3.Zero, 0f, Vector3.UnitZ);

        float clamped = Math.Clamp(s, 0f, count - 1);
        int i1 = (int)MathF.Floor(clamped);
        float f = clamped - i1;
        int i0 = i1 > 0 ? i1 - 1 : 0;
        int i2 = Math.Min(i1 + 1, count - 1);
        int i3 = Math.Min(i1 + 2, count - 1);

        var centerRow = CatmullRom(
            Row(points[i0]), Row(points[i1]), Row(points[i2]), Row(points[i3]), f);
        var outwardRow = CatmullRom(
            Outward(points[i0]), Outward(points[i1]), Outward(points[i2]), Outward(points[i3]), f);

        // Same fallback as the shader: normalize(0) is NaN, better a wrong orientation than throwing the whole sheet outside clip space.
        var outward = new Vector3(outwardRow.X, 0f, outwardRow.Z) + new Vector3(0f, 0f, 1e-5f);
        outward = Vector3.Normalize(outward);

        return new WaterfallRow(new Vector3(centerRow.X, centerRow.Y, centerRow.Z), centerRow.W, outward);
    }

    /// <summary>
    /// CPU-side section sampling: shape-identical to the section formula in <c>WaterfallShaders.VertexHlsl</c>.
    ///
    /// The section is a closed ring around the control point polyline; the parameter <paramref name="fu"/> goes a full turn from 0 to 1, with 0 at the front center
    /// and 0.5 at the back center against the rock; the lateral semi-axis = that row's half width, the longitudinal semi-axis = half the thickness, plus the front bow term.
    /// The two sides (<paramref name="fu"/> = 0.25 / 0.75) fall exactly on the control point polyline, coinciding with the old single sheet:
    /// the cliff face is on that line, and if the sides were pulled back they would be culled entirely by the software depth test as being "inside the rock".
    ///
    /// <see cref="WaterfallSection.U"/> is the texture horizontal coordinate, taken from the <b>lateral position</b> rather than the ring parameter:
    /// fu=0 and fu=1 are both the front center with u=0.5, the two sides are 0 and 1, so both position and texture are continuous at the seam where the ring wraps.
    /// </summary>
    /// <param name="row">The row's center, half width and outward direction, from <see cref="SampleRow"/>.</param>
    /// <param name="thickness">Total section thickness (meters); non-positive values are clamped to 0.04.</param>
    /// <param name="bow">Front bow amount (meters); applies only to the front half.</param>
    /// <param name="fu">Ring parameter [0,1]; out-of-range values are clamped automatically.</param>
    public static WaterfallSection Section(in WaterfallRow row, float thickness, float bow, float fu)
    {
        float halfDepth = MathF.Max(thickness, 0.04f) * 0.5f;
        float theta = Math.Clamp(fu, 0f, 1f) * MathF.Tau;
        float sin = MathF.Sin(theta);
        float cos = MathF.Cos(theta);

        var lateral = new Vector3(row.Outward.Z, 0f, -row.Outward.X);
        float front = MathF.Max(cos, 0f);
        float lateralOffset = sin * row.HalfWidth;
        float depthOffset = cos * halfDepth + bow * front * front;
        var position = row.Center + lateral * lateralOffset + row.Outward * depthOffset;

        // Section normal: rotate the 2D tangent (dLat, dOut) by 90° to get (-dOut, dLat), pointing outward in the same direction as the ring parameter.
        // Taking the derivative directly rather than using neighboring column differences is to make the normals of the first/last column line up exactly too.
        float slope = halfDepth + 2f * bow * front;
        var normal = lateral * (sin * slope) + row.Outward * (cos * row.HalfWidth);
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;

        return new WaterfallSection(position, normal, sin * 0.5f + 0.5f);
    }

    /// <summary>Read a control point as "midpoint.xyz + halfwidth.w", aligned with the GPU-side float4 layout.</summary>
    static Vector4 Row(in WaterfallPoint point)
        => new(point.Position, MathF.Max(point.HalfWidth, 0.02f));

    /// <summary>Read a control point as "horizontal outward.x, 0, .z, 0", matching the form <see cref="Pack"/> writes into the buffer (normalized, zero vector degenerating to +Z).</summary>
    static Vector4 Outward(in WaterfallPoint point)
    {
        var outward = new Vector3(point.Outward.X, 0f, point.Outward.Z);
        outward = outward.LengthSquared() > 1e-6f ? Vector3.Normalize(outward) : Vector3.UnitZ;
        return new Vector4(outward.X, 0f, outward.Z, 0f);
    }

    /// <summary>Catmull-Rom basis function, term-for-term matching 0.5*(2p1 + (p2-p0)f + (2p0-5p1+4p2-p3)f² + (3p1-p0-3p2+p3)f³) in HLSL.</summary>
    static Vector4 CatmullRom(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, float f)
        => 0.5f * (2f * p1 + (p2 - p0) * f
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f * f
            + (3f * p1 - p0 - 3f * p2 + p3) * f * f * f);

    static void WriteMatrix(Span<Vector4> data, int offset, Matrix4x4 matrix)
        => MemoryMarshal.Cast<Matrix4x4, Vector4>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).CopyTo(data[offset..]);
}

/// <summary>
/// One row from <see cref="WaterfallMath.SampleRow"/>: the section midpoint, half width and that row's own horizontal outward direction.
/// It is only a CPU-side intermediate result and does not participate in the frame buffer layout.
/// </summary>
/// <param name="Center">Section midpoint (author coordinates, before the placement matrix).</param>
/// <param name="HalfWidth">Half width of that row's water band (meters, already including half width interpolation).</param>
/// <param name="Outward">That row's outward direction (horizontal, normalized).</param>
public readonly record struct WaterfallRow(Vector3 Center, float HalfWidth, Vector3 Outward);

/// <summary>
/// A section point from <see cref="WaterfallMath.Section"/>: position, outward normal and texture horizontal coordinate.
/// Used only by the proxy mesh (picking, outline, bounding box); does not participate in the frame buffer layout.
/// </summary>
/// <param name="Position">Point on the section (author coordinates, before the placement matrix).</param>
/// <param name="Normal">Section normal (unit vector, pointing outside the water body).</param>
/// <param name="U">Texture horizontal coordinate [0,1], equal to the normalized lateral position, consistent with the old single sheet.</param>
public readonly record struct WaterfallSection(Vector3 Position, Vector3 Normal, float U);
