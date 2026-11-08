// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// A sailing boat (<c>Assets/sailing_boat.glb</c>) anchored on the open sea just off the east-coast rocks
/// (<see cref="Rocks"/>), riding the sea surface and flying a sail whose color cycles through the spectrum.
///
/// <para><b>One single boat, plus a seven-boat fleet.</b> The panel owns two controls over the same asset. The single
/// <see cref="Season.Controls.Model"/> keeps the per-part base color added to <see cref="Season.Controls.Model"/>: the sail
/// is one glTF material inside a six-material asset, and it is recolored in place without touching the hull, the glass,
/// the steel, or the deck paint.</para>
///
/// <para><b>The fleet exercises the generic per-instance extras slot instead.</b> Seven boats share one
/// <see cref="Season.Controls.InstancedModel"/>; each carries its own <see cref="MeshInstanceTransform.Color"/>, which the
/// backend writes into the per-instance record and the vertex / pixel stage reads. Only the primitives named by
/// <see cref="Season.Controls.InstancedModel.InstanceTintParts"/> read it, so the hull, glass, steel and deck of every
/// boat keep their authored materials. The slot itself is generic - a 32-byte record per instance whose remaining
/// channels are free for other per-instance scalars - rather than a color-only buffer.</para>
///
/// <para><b>Why the color is set by material name.</b> The sail is the single primitive of mesh 5 with material
/// <c>Hard_Textured_Plastic_White</c> (authored pure yellow, double-sided). Material names survive re-exports of this
/// asset, node names do not, and <see cref="Season.Controls.Model.SetPartColor"/> resolves material first and node
/// second, case-insensitively. <see cref="Season.Controls.Model.GetPartNames"/> lists what an asset offers.</para>
///
/// <para><b>The override is absolute RGB, and that is the whole point here.</b> The authored sail factor is saturated
/// yellow (0.97, 0.93, 0.0003), so a multiplicative tint can only make it redder or darker - never blue. Replacing the
/// RGB is what lets the same geometry pass through all seven hues; the alpha channel stays with
/// <see cref="Season.Controls.Control.Alpha"/>, so nothing here can change the boat's opacity.</para>
///
/// <para><b>Height comes from the drawn surface, not from the mean sea level.</b> <see cref="Sea.DefaultSeaLevel"/> is
/// only the average: the baked relief runs to about +/-4.5 m offshore, so a hull pinned to it would sit buried under
/// every crest. <see cref="Update"/> samples <see cref="Sea.SurfaceY"/> at the anchor and places the bounding-box centre
/// so that the keel stays <see cref="Draft"/> metres under the water the eye actually sees. That also tracks the
/// runtime sea-level setting for free, because the sampler reads the same value the water pass draws.</para>
/// </summary>
internal sealed class SailingBoats : Panel
{
    /// <summary>The boat asset: six materials, no animation, sail double-sided.</summary>
    internal const string GlbPath = @"Assets/sailing_boat.glb";

    /// <summary>
    /// Part key of the sail: its glTF material name. See the class summary for why the material name is used
    /// instead of the node name that owns the primitive.
    /// </summary>
    const string SailPart = "Hard_Textured_Plastic_White";

    // -- Placement. The rocks straddle the east shoreline at x about 74..75, so the anchor sits 23 m offshore of
    //    their line and level with the sea road's heading, where the beach and the road end both look straight at it. --

    const float AnchorX = 300f;
    const float AnchorZ = 4f;

    // -- Hull box, in meters. The asset's raw world box is 318 x 459.751 x 77.775 local units on X / Y / Z, with
    //    the long axis on X (bow-stern) and the box's bottom at local Y = 0. The three targets below are one uniform
    //    scale (0.0283) of that box, so the mesh is not distorted. --

    /// <summary>Bounding-box length along the asset's X axis, the bow-stern line.</summary>
    const float Length = 9f;

    /// <summary>Bounding-box height from keel to masthead (the asset's Y extent, 459.75 units).</summary>
    const float BoatHeight = 13f;

    /// <summary>Bounding-box beam along the asset's Z axis.</summary>
    const float Beam = 2.2f;

    /// <summary>
    /// How deep the box's bottom sits below the water line. The hull's own waterline is about a third of its 2.2 m
    /// freeboard on this scale, which reads as a loaded cruising boat rather than a floating toy.
    /// </summary>
    const float Draft = 0.75f;

    /// <summary>
    /// Yaw, radians. The asset's long axis is local X and the sail lies in the local XY plane (its normal is local Z),
    /// so a quarter turn puts the bow-stern line on world Z and turns the sail broadside to the beach - which is the
    /// side the player views it from. Both sail faces are double-sided in the asset, so no face culling issue arises.
    /// </summary>
    const float Heading = MathF.PI / 2f;

    /// <summary>Seconds each hue holds. Seven hues at 5 s give the 35 s cycle the scene was specified with.</summary>
    const float ColorSeconds = 5f;

    // -- Fleet placement lives in FleetLayout below, next to SailColors, so the seven hues and the seven stations are
    //    authored in one place and cannot drift apart. --

    /// <summary>
    /// The cycle, in order: red, orange, yellow, green, cyan, blue, purple. Values are linear PBR base-color factors,
    /// and the yellow entry is the sail's authored factor so the authored look is one phase of the loop rather than a
    /// color the loop skips.
    /// </summary>
    static readonly Vector4[] SailColors =
    {
        new(0.90f, 0.05f, 0.04f, 1f),   // red
        new(0.95f, 0.35f, 0.02f, 1f),   // orange
        new(0.97f, 0.93f, 0.00f, 1f),   // yellow (the authored sail color)
        new(0.08f, 0.60f, 0.10f, 1f),   // green
        new(0.02f, 0.65f, 0.68f, 1f),   // cyan
        new(0.03f, 0.12f, 0.75f, 1f),   // blue
        new(0.36f, 0.05f, 0.65f, 1f),   // purple
    };

    /// <summary>
    /// The fleet's seven authored stations, in the same order as <see cref="SailColors"/>: the boat at index i starts on
    /// SailColors[i] and inherits it again every 35 s.
    /// <para>
    /// The spacing is deliberately irregular. Both axes move between every pair of neighbours, yet the step sizes stay
    /// within a few meters of each other (X 18-23 m, Z 13-14 m, straight-line distance 23-26 m, about two and a half
    /// hull lengths), so the boats read as scattered traffic instead of a rank, an arc, or a lattice. Nothing here is
    /// generated from a formula: a formula is what produced the evenly spaced line this replaced, and any two boats
    /// sharing a step length is what made it read as a queue. The list is the only source of the layout - Update never
    /// moves a boat, it only advances hues.
    /// </para>
    /// </summary>
    static readonly List<SailingBoat> FleetLayout = new()
    {
        new SailingBoat { Color = SailColors[0], PosX = AnchorX + 19f,  PosZ = AnchorZ - 184f },   // red
        new SailingBoat { Color = SailColors[1], PosX = AnchorX + 39f, PosZ = AnchorZ - 151f },   // orange
        new SailingBoat { Color = SailColors[2], PosX = AnchorX + 21f, PosZ = AnchorZ - 97f },    // yellow
        new SailingBoat { Color = SailColors[3], PosX = AnchorX + 43f, PosZ = AnchorZ + 76f },    // green
        new SailingBoat { Color = SailColors[4], PosX = AnchorX + 23f, PosZ = AnchorZ + 109f },   // cyan
        new SailingBoat { Color = SailColors[5], PosX = AnchorX + 46f, PosZ = AnchorZ + 162f },   // blue
        new SailingBoat { Color = SailColors[6], PosX = AnchorX + 25f, PosZ = AnchorZ + 265f },   // purple
    };

    /// <summary>
    /// The sea panel the hull's height is read from. Held as the panel's dependency rather than a duplicate of the
    /// level, so a runtime sea-level change or a mode switch to Dynamic is followed without extra wiring.
    /// </summary>
    readonly Sea sea;

    /// <summary>Panel-local clock, advanced by the same clamped delta the rest of the scene uses.</summary>
    float clock;

    /// <summary>Index into <see cref="SailColors"/> already pushed into the model; -1 before the first frame.</summary>
    int colorIndex = -1;

    /// <summary>
    /// The fleet: seven instances of the same asset in one draw batch. It stays apart from <see cref="boat"/> on purpose --
    /// the single model demonstrates the per-part override on one hull, while this batch demonstrates the generic
    /// per-instance extras slot, which every boat addresses with its own color and nothing else on board reads.
    /// </summary>
    internal readonly InstancedModel fleet;

    /// <summary>Hue rotation already pushed into the fleet; -1 before the first frame.</summary>
    int fleetColorStep = -1;

    /// <summary>
    /// The boat itself, exposed like <see cref="Boat.Hull"/> so the object picker can register it later without this
    /// panel having to know about picking.
    /// </summary>
    internal readonly Model boat;

    internal SailingBoats(Sea sea)
    {
        this.sea = sea;

        PosX = AnchorX;
        PosZ = AnchorZ;

        boat = new Model
        {
            Name = GlbPath,
            Width = Length,
            Height = BoatHeight,
            Depth = Beam,
            Rotation = Heading,
        };
        AddControl(boat);

        fleet = new InstancedModel
        {
            Name = GlbPath + ":fleet",
            ModelName = GlbPath,
            // The stations are spread around the island, so most of the fleet is off screen at any heading.
            DrawPath = InstanceDrawPath.Indirect,
            // Only the sail opts into the per-instance color. Everything else on board keeps TintMode = 0, so the
            // instance color never reaches the hull, the glass, the steel, or the deck paint.
            InstanceTintParts = new[] { SailPart },
        };

        // Seven authored stations, one per hue. Color is CPU-side instance data; the backends fold it into their
        // per-instance extras ring every frame, so it can be written before the template loads.
        for (int i = 0; i < FleetLayout.Count; i++)
        {
            var station = FleetLayout[i];
            fleet.Instances.Add(new MeshInstanceTransform
            {
                Name = $"sailing-boat-{i + 1}",
                PosX = station.PosX,
                PosZ = station.PosZ,
                // Same target box as the single boat, so both controls render the asset at exactly one scale.
                Width = Length,
                Height = BoatHeight,
                Depth = Beam,
                Rotation = Quaternion.CreateFromYawPitchRoll(Heading, 0f, 0f),
                Color = station.Color,
            });
        }

        // AddControl last: it queues the template load, so the instance list is already complete when it starts.
        AddControl(fleet);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Same frame-delta contract the boat and the campfire use: a stalled frame must not jump the hue.
        float delta = float.IsFinite(time) ? Math.Clamp(time, 0f, 0.1f) : 0f;
        clock += delta;

        // Ride the surface the sea pass actually draws. The anchor is the bounding-box centre, so the box bottom is
        // BoatHeight / 2 below it; placing the centre Draft above the surface keeps the keel that far under water, and
        // every crest or trough moves the whole hull with it. Safe before the sea finishes loading: the sampler
        // returns the flat level until the relief grid exists.
        boat.PosX = PosX;
        boat.PosZ = PosZ;
        boat.PosY = sea.SurfaceY(PosX, PosZ) + BoatHeight * 0.5f - Draft;

        // Sail hue: one color per ColorSeconds, wrapping after the seventh. The model stores the last value, so the
        // comparison keeps SetPartColor (and the backend's version counter) quiet on the frames in between.
        int index = (int)(clock / ColorSeconds) % SailColors.Length;
        if (index != colorIndex)
        {
            colorIndex = index;
            boat.SetPartColor(SailPart, SailColors[index]);
        }

        // The fleet rides the same drawn surface, one sample per hull.
        for (int i = 0; i < fleet.Instances.Count; i++)
        {
            var instance = fleet.Instances[i];
            instance.PosY = sea.SurfaceY(instance.PosX, instance.PosZ) + BoatHeight * 0.5f - Draft;
        }

        // Fleet hues: every boat owns one color, and the fleet advances one slot every ColorSeconds, so each hull
        // passes through all seven colors in turn and the cycle closes after 35 s. Only a step change writes, which keeps
        // the per-instance extras upload identical on the frames in between.
        int fleetStep = (int)(clock / ColorSeconds) % SailColors.Length;
        if (fleetStep != fleetColorStep)
        {
            fleetColorStep = fleetStep;
            for (int i = 0; i < fleet.Instances.Count; i++)
                fleet.Instances[i].Color = SailColors[(i + fleetStep) % SailColors.Length];
        }

        if (boat.Update(time))
            result = true;

        if (fleet.Update(time))
            result = true;

        return result;
    }
}

/// <summary>
/// One station of the seven-boat fleet: the hue it starts on and where its hull anchor floats.
/// <para>
/// Kept as an authored record instead of three loose arrays so a boat's position and starting color are read and edited
/// as one row. The list of these stations is the layout's only source: the fleet's Update advances hues and follows the
/// sea surface, but it never moves a boat.
/// </para>
/// </summary>
internal sealed class SailingBoat
{
    /// <summary>Hue this boat starts the 35 s cycle on, in linear PBR base-color space.</summary>
    public Vector4 Color { get; init; }

    /// <summary>World X of the hull anchor (the bounding-box center), in meters.</summary>
    public float PosX { get; init; }

    /// <summary>World Z of the hull anchor, in meters.</summary>
    public float PosZ { get; init; }
}
