// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Hot air balloon moored just off the tower deck, on the side that faces the haunted house.
///
/// The tower stands at (48, -26) with its walkable deck top at y = 20 (see <see cref="Tower"/>, DeckTop), and the
/// haunted house sits at (40, 35) (see <see cref="House"/>). "Toward the house" from the tower is therefore mostly
/// +Z (north) with a slight lean to -X, so the balloon is placed along that tower-to-house line rather than on a
/// plain axis, which is what keeps it reading as sitting in front of the tower from the deck.
///
/// <para><b>Horizontal placement.</b> The balloon center is dropped <see cref="DistanceFromTower"/> meters out from
/// the tower center along that line, which puts the basket right at the deck edge (the deck half-width is 1.65m) so
/// it hugs the platform without overlapping the shaft.</para>
///
/// <para><b>Vertical placement.</b> The model's own bottom - the lowest point of its bounding box, which is the waving
/// crew's rest-pose feet - is level with the underside of the deck slab, i.e. the platform bottom at
/// DeckTop - SlabThickness = 19.65m (see <see cref="PlatformBottomY"/>), so the balloon sits lower than the deck
/// surface rather than floating above it. That leaves the wicker underside a few centimetres higher (see
/// <see cref="BasketShellFraction"/>) and the wicker floor at 20.01m, a hair above the deck boards; the seat stands
/// higher still, see the basket paragraph. Since the anchor is the bounding-box center, PosY is that bottom plus half
/// the settled height.</para>
///
/// <para><b>Sizing.</b> The height is authored at 8 player heights (8m, the player model is exactly 1m tall). Width
/// and depth are not authored: they are settled once from the loaded GLB bounds by scaling every axis off the single
/// height factor, so the balloon keeps its natural proportions instead of being stretched, exactly the way
/// <see cref="Campfire"/> sizes its meshes from one factor. The bounds arrive asynchronously with the queued load,
/// so the settle is retried each frame until the model reports its LocalSize.</para>
///
/// <para><b>The basket is this balloon's deck.</b> The model's two lowest vertex rings are the wicker basket: their
/// plan rectangle is what the boarding test uses. The waving crew - the one figure the asset animates - stands on
/// the wicker floor facing the model's -X, and the seat is in front of it on that side, lifted so the half-size
/// body's head sits just under the crew's. Player, Skill and Direction reuse the boat's scheme: the long jump
/// predicts the basket at takeoff and starts shrinking there, contact anywhere over the basket binds the player to
/// the seat, and stepping off grows the body back. While aboard the body turns freely with the camera - the basket
/// never yaws - and the movement keys drive the balloon in the crew's own frame: up toward the face the crew turns,
/// down toward its back, left and right across the basket, never along the head or the camera. Every step that would
/// cut the basket into the tower's rim is refused, so the balloon can be flown around the deck but never through it.
/// Every derived offset below is a fraction of the settled bounding box, so the geometry follows the model instead
/// of repeating its measurements in world meters.</para>
/// </summary>
internal class HotAirBalloon : Panel
{
    // Tower deck center on the ground, and the haunted house it faces. Both mirror the values set in App.cs /
    // House.cs; kept here so the placement line is expressed in one place.
    const float TowerCenterX = 48f;
    const float TowerCenterZ = -26f;
    const float HouseCenterX = 40f;
    const float HouseCenterZ = 35f;

    // How far out from the tower center, along the tower-to-house line, the balloon center sits. 2.5m keeps the
    // basket just past the 1.65m deck half-width, so it hugs the platform edge without overlapping the shaft.
    const float DistanceFromTower = 2.5f;

    // Deck top height (matches Tower.DeckTop) and the deck slab thickness (matches Tower.SlabThickness). The
    // basket bottom is level with the underside of the deck slab, i.e. the platform bottom, not its top.
    const float DeckTopY = 20f;
    const float SlabThickness = 0.35f;
    const float PlatformBottomY = DeckTopY - SlabThickness;   // 19.65, the deck slab underside

    // Authored height in world meters, i.e. 8 player heights. Width/depth follow proportionally at settle time.
    const float BalloonHeight = 8f;

    const string BalloonGlbPath = @"Assets/hot_air_balloon_merged.glb";

    // -- Basket, measured off the GLB and kept as fractions of the settled box: whatever the height factor turns
    //    out to be, the wicker rectangle and the seat stay where the model puts them. Raw numbers behind these
    //    fractions, on the 1.2546 x 1.7833 x 1.2546 local box: basket half extents 0.0944 x 0.0908, box bottom
    //    -0.2535, the crew's animated body spanning x -0.0188..0.0405 (the -X side is the way it faces, so that is
    //    where the player stands), and the crew's head top at 0.0341 in its first animated frame. --
    const float BasketShellFraction = 0.0291f;   // basket underside above the box bottom, x Height (19.88 m)
    const float BasketHalfXFraction = 0.0752f;   // basket plan half width, x Width (0.42 m)
    const float BasketHalfZFraction = 0.0724f;   // basket plan half depth, x Depth (0.41 m)

    // The seat stands in front of the crew, on the -X side it faces. The -0.25 m offset is the widest stance the
    // narrow wicker allows: it keeps the half-size body clear of the crew's outstretched arms while leaving the
    // head, once lifted, above the front rim in the eye camera's view.
    const float SeatSideFraction = -0.0444f;     // seat offset toward the crew's front, x Width (-0.25 m)

    // The seat is lifted rather than laid on the wicker: the head of the half-size player (the body is 1 m tall
    // full size) is parked just under the crew's head, which is what puts the eye camera at the crew's eye level
    // and above the basket rim. Raw fractions on the same local box, all x Height.
    const float CrewHeadTopFraction = 0.1613f;   // crew head top above the box bottom (0.0341 + 0.2535) / 1.7833
    const float SeatedBodyHeight = 0.5f;         // half-size body height (m)
    const float SeatHeadClearance = 0.03f;       // player's head sits this far below the crew's head (m)

    /// <summary>
    /// Height of the tower's solid rim above whichever walkable top it stands on: the deck railings and the stair
    /// parapets are both half a player. The basket underside rides just below the deck top, so this is the number
    /// that tells the steering test the basket would cut the railing instead of flying past it.
    /// </summary>
    const float TowerRimHeight = 0.5f;

    internal Season.Controls.Model model;

    // One-shot sizing: needs the GLB bounds, which arrive with the async load, so retry until it lands.
    bool sized;

    /// <summary>
    /// True while the player stands in the basket. Entered and left only through <see cref="Board"/> and
    /// <see cref="Disembark"/>, which <see cref="Skill"/> calls once the long jump knows where it came down; the
    /// pose change stays with that landing code, so this flag is only a statement about the balloon.
    /// </summary>
    internal bool Aboard { get; private set; }

    // The basket underside, i.e. the lowest point of the wicker that could meet the tower.
    float BasketShellY => model.PosY - (model.Height ?? 0f) * 0.5f + BasketShellFraction * (model.Height ?? 0f);

    /// <summary>
    /// World Y of the surface the player's feet stand on while aboard: the wicker floor lifted so the half-size
    /// body's head sits <see cref="SeatHeadClearance"/> under the waving crew's head. Standing on the wicker itself
    /// would leave the player hidden behind the basket wall, which is exactly what this lift is for.
    /// </summary>
    internal float SeatFootY
    {
        get
        {
            float height = model.Height ?? 0f;
            float bottom = model.PosY - height * 0.5f;
            return bottom + CrewHeadTopFraction * height - SeatedBodyHeight - SeatHeadClearance;
        }
    }

    /// <summary>
    /// Heading the four movement keys use while the body is in the basket, in the shared character convention
    /// (forward = (-sin heading, 0, -cos heading)). It is the way the waving crew faces - the model's -X, turned
    /// with the model - so "up" flies the basket toward the crew's front, "down" toward its back and left/right
    /// across it. Neither the camera nor the body's own yaw takes part: the player turns freely on the spot while
    /// the keys keep pointing where the crew looks.
    /// </summary>
    internal float SteerHeading => model.Rotation + MathF.PI * 0.5f;

    /// <summary>
    /// World position the player stands on: the lifted seat in front of the waving crew. The lateral offset is
    /// turned by the model's yaw with the same row-vector rotation the mesh itself uses, so the seat moves with the
    /// balloon and cannot drift from the basket it stands in.
    /// </summary>
    internal Vector3 SeatPosition
    {
        get
        {
            float c = MathF.Cos(model.Rotation);
            float s = MathF.Sin(model.Rotation);
            float side = SeatSideFraction * (model.Width ?? 0f);

            return new Vector3(
                model.PosX + side * c,
                SeatFootY,
                model.PosZ - side * s);
        }
    }

    /// <summary>
    /// Takes the basket: from the next frame on the player is bound to <see cref="SeatPosition"/> and the movement
    /// keys steer in the crew's frame (<see cref="SteerHeading"/>), whichever way the body itself is turned.
    /// </summary>
    internal void Board() => Aboard = true;

    /// <summary>Leaves the basket where the balloon stands: it keeps its position and stays ready to be boarded again.</summary>
    internal void Disembark() => Aboard = false;

    /// <summary>
    /// Whether a world XZ point lies over the basket's plan rectangle. The long jump uses it both to predict the
    /// boarding at takeoff and to stop the flight the moment the body is over the basket. A negative
    /// <paramref name="margin"/> grows the rectangle, which is how the contact test reaches a body that the tower's
    /// own railing stopped a step short; a positive one shrinks it so the wicker rim does not count as floor.
    /// </summary>
    internal bool ContainsBasket(float x, float z, float margin = 0f)
    {
        float width = model.Width ?? 0f;
        float depth = model.Depth ?? 0f;
        if (width <= 1e-4f || depth <= 1e-4f)
            return false;   // Bounds not settled yet: there is no basket to stand over.

        float halfX = BasketHalfXFraction * width - margin;
        float halfZ = BasketHalfZFraction * depth - margin;
        if (halfX <= 0f || halfZ <= 0f)
            return false;

        float dx = x - model.PosX;
        float dz = z - model.PosZ;
        float c = MathF.Cos(model.Rotation);
        float s = MathF.Sin(model.Rotation);

        // World offset in the basket's own axes: the inverse of the yaw applied to the mesh.
        float lx = dx * c - dz * s;
        float lz = dx * s + dz * c;

        return MathF.Abs(lx) <= halfX
            && MathF.Abs(lz) <= halfZ;
    }

    /// <summary>
    /// Horizontal distance from a world XZ point to the basket's plan rectangle, zero when the point is over the
    /// wicker. Skill uses it to judge whether a walkable top close to the basket is close enough that a leap from
    /// it reaches the wicker before the flight ends; a per-axis test would overstate the reach on the diagonals.
    /// </summary>
    internal float PlanDistance(float x, float z)
    {
        float width = model.Width ?? 0f;
        float depth = model.Depth ?? 0f;
        if (width <= 1e-4f || depth <= 1e-4f)
            return float.PositiveInfinity;   // Bounds not settled yet: there is no basket to reach.

        float c = MathF.Cos(model.Rotation);
        float s = MathF.Sin(model.Rotation);

        // World offset in the basket's own axes, the same inverse yaw ContainsBasket uses.
        float dx = x - model.PosX;
        float dz = z - model.PosZ;
        float lx = dx * c - dz * s;
        float lz = dx * s + dz * c;

        float gapX = MathF.Max(MathF.Abs(lx) - BasketHalfXFraction * width, 0f);
        float gapZ = MathF.Max(MathF.Abs(lz) - BasketHalfZFraction * depth, 0f);
        return MathF.Sqrt(gapX * gapX + gapZ * gapZ);
    }

    /// <summary>
    /// The first-person eye moved <paramref name="distance"/> along the horizontal view, and held inside the wicker
    /// plan. The rim in front of the eye cuts a wide wedge out of the lower view and hides the ground under the
    /// basket; the closer the eye comes to that rim, the smaller the wedge. The clamp is what makes the nudge safe:
    /// the eye stands above the rim, so without it a turn toward a near wall would carry the camera out of the basket.
    /// <paramref name="look"/> is the view's horizontal direction, normalized.
    /// </summary>
    internal Vector3 NudgedEye(Vector3 eye, Vector2 look, float distance)
    {
        float width = model.Width ?? 0f;
        float depth = model.Depth ?? 0f;
        if (width <= 1e-4f || depth <= 1e-4f)
            return eye;   // Bounds not settled yet: there is no wicker to hold the camera in.

        float c = MathF.Cos(model.Rotation);
        float s = MathF.Sin(model.Rotation);

        // The eye sits over the seat, so its offset from the balloon center is already a basket-local point.
        float dx = eye.X - model.PosX;
        float dz = eye.Z - model.PosZ;
        float lx = dx * c - dz * s;
        float lz = dx * s + dz * c;

        // The same turn applied to the look vector, then the nudge along it.
        lx += (look.X * c - look.Y * s) * distance;
        lz += (look.X * s + look.Y * c) * distance;

        // Stop clear of the walls. The eye is above the rim, so a few centimetres of wicker are enough to keep the
        // camera in the basket without the near plane cutting through the wall it looks over.
        const float wallClearance = 0.04f;
        float halfX = BasketHalfXFraction * width - wallClearance;
        float halfZ = BasketHalfZFraction * depth - wallClearance;
        lx = Math.Clamp(lx, -halfX, halfX);
        lz = Math.Clamp(lz, -halfZ, halfZ);

        return new Vector3(
            model.PosX + lx * c + lz * s,
            eye.Y,
            model.PosZ - lx * s + lz * c);
    }

    /// <summary>
    /// One steering request, in world metres. The balloon only translates - it has no yaw to swing around, which is
    /// all the basket needs now that the player stands in front of the crew and turns on the spot - and a request
    /// that would cut the basket into the tower is refused. A blocked diagonal is retried one axis at a time so the
    /// balloon slides along the tower instead of stopping dead against it. Returns whether the balloon actually moved.
    /// </summary>
    internal bool Steer(float dx, float dz)
    {
        if (!sized)
            return false;

        if (dx * dx + dz * dz < 1e-10f)
            return false;

        float x = model.PosX;
        float z = model.PosZ;

        if (CanFlyTo(x + dx, z + dz))
        {
            model.PosX = x + dx;
            model.PosZ = z + dz;
            return true;
        }

        // The diagonal is blocked: apply whichever single axis is still free. Only one, or the pair would add up
        // to the rejected diagonal.
        if (CanFlyTo(x + dx, z))
        {
            model.PosX = x + dx;
            return true;
        }

        if (CanFlyTo(x, z + dz))
        {
            model.PosZ = z + dz;
            return true;
        }

        return false;
    }

    /// <summary>Whether the basket can be at this world XZ: its plan rectangle must clear the tower's solid rim.</summary>
    bool CanFlyTo(float x, float z) => !HitsTower(x, z);

    /// <summary>
    /// Whether the basket's plan would cut into the tower at this spot. The tower publishes its walkable tops
    /// (deck slab, stair treads) through <see cref="Tower.FloorHeightAtWorld"/> and keeps its railings and parapets
    /// out of that data, so the rim height is added here. The basket hangs just below the deck top, which means any
    /// tower surface whose top plus rim reaches above the wicker underside is a collision: the deck and the top few
    /// treads block, while the lower flight passes underneath with room to spare.
    ///
    /// <para>The tower is read through <c>App.Instance</c> rather than captured in the constructor: the balloon is
    /// built before the tower in <see cref="App"/>'s panel list, so the field is still null at construction time.</para>
    /// </summary>
    bool HitsTower(float x, float z)
    {
        var tower = App.Instance.tower;
        float width = model.Width ?? 0f;
        float depth = model.Depth ?? 0f;
        if (tower == null || width <= 1e-4f || depth <= 1e-4f)
            return false;

        float halfX = BasketHalfXFraction * width;
        float halfZ = BasketHalfZFraction * depth;
        float underside = BasketShellY;

        float c = MathF.Cos(model.Rotation);
        float s = MathF.Sin(model.Rotation);

        // Center, corners and edge midpoints: the rectangle is small against the deck, and a ring of samples is
        // what PlayerCollider uses for the same kind of footprint query.
        for (int i = -1; i <= 1; i++)
        {
            for (int j = -1; j <= 1; j++)
            {
                float lx = i * halfX;
                float lz = j * halfZ;
                float wx = x + lx * c + lz * s;
                float wz = z - lx * s + lz * c;

                // Zero is "not on the tower": FloorHeightAtWorld returns it for every point outside the deck and
                // the flight, and the ground the tower stands on is that same zero.
                float top = tower.FloorHeightAtWorld(wx, wz);
                if (top > 0f && top + TowerRimHeight > underside)
                    return true;
            }
        }

        return false;
    }

    internal HotAirBalloon()
    {
        // Direction from the tower toward the house, in the XZ plane, normalized.
        float dx = HouseCenterX - TowerCenterX;
        float dz = HouseCenterZ - TowerCenterZ;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        float nx = dx / len;
        float nz = dz / len;

        // Basket bottom is level with the platform bottom; the anchor is the bbox center, so add half the height.
        float centerY = PlatformBottomY + BalloonHeight * 0.5f;

        model = new Season.Controls.Model()
        {
            Name = BalloonGlbPath,
            Highlight =
            {
                Style = HighlightStyle.Wireframe
            },
            PosX = TowerCenterX + nx * DistanceFromTower,
            PosY = centerY,
            PosZ = TowerCenterZ + nz * DistanceFromTower,
            // Height is authored; Width/Depth stay 0 until the bounds settle them from the natural aspect ratio.
            Height = BalloonHeight,
        };
        AddControl(model);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (!sized)
            sized = Settle();

        if (model.Update(time))
        {
            result = true;
        }

        return result;
    }

    /// <summary>
    /// One-shot width/depth settle once the GLB has reported its bounds. Every axis is scaled off the single height
    /// factor so the balloon keeps its natural proportions; PosY is re-derived from the settled height so the basket
    /// floor stays pinned at the platform bottom regardless of the authored 8m.
    /// </summary>
    bool Settle()
    {
        var local = model.LocalSize;
        if (local.Y <= 1e-6f)
            return false;   // Bounds not established yet; retry on the next frame.

        float scale = BalloonHeight / local.Y;
        model.Width = local.X * scale;
        model.Height = BalloonHeight;
        model.Depth = local.Z * scale;

        // Keep the bottom flush with the platform underside even if the settled height differs from the constant.
        model.PosY = PlatformBottomY + (model.Height ?? 0f) * 0.5f;

        return true;
    }
}
