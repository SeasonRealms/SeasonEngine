// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Round flagpole on the open lawn exactly halfway between the haunted house and the runway, carrying a pirate
/// flag whose two faces are printed with a different picture.
///
/// <para><b>Where.</b> House.cs puts the house model at (40, 35) and AeroPlane.cs takes off from the middle of the
/// seaside road's strip at (40.875, -7.05), so the midpoint of that line is (40.4375, 13.975): 21 m from each
/// landmark, and well clear of the house's 25 m footprint, the pond, the road and the rock line. Both landmarks
/// therefore see the flag broadside, one face each.</para>
///
/// <para><b>How big.</b> The player model is exactly 1 m tall, so the brief reads directly in metres: the platform
/// under the pole is 1/5 player (0.2 m), the pole is 15 players (15 m) tall and half a player (0.5 m) across. The
/// pole started at 10 m and was raised to 1.5x so the flag has clear sky to climb into; every height in this file
/// is derived from <see cref="PoleHeight"/>, so the travel, the top stop and the cloth rest pose all follow. The
/// platform radius is the one size the brief leaves open; 0.9 m is what leaves a rim wide enough to stand on next
/// to a 0.5 m column.</para>
///
/// <para><b>The flag.</b> A 2.4 x 1.6 m cloth 3 cm thick, which is 3:2 - exactly the aspect of the 768 x 512 pictures
/// .dbg/_flag_assets writes, so neither face stretches the art. Two pictures mean two base-color textures, and a
/// Surface carries one, so the front and the back are separate surfaces of the same Mesh3D: one transform, one
/// control, three draw calls (two pictures plus the white rim that closes the 3 cm edge). The cloth is not flat -
/// a fold runs out from the pole and the fly end droops - but both are pinned to zero at the hoist, so the edge
/// against the pole stays straight whatever either number is tuned to.</para>
///
/// <para><b>Wind.</b> Where the backend can hand a compute buffer to the input assembler, the sheet is a GPU XPBD
/// cloth (<see cref="Season.Rendering.Effects.ClothSimulationEffect"/>) that writes all three surfaces' vertices every frame, so the scene and
/// the shadow draw the same moving flag through the ordinary shaders. The hoist column is pinned to a swivel that
/// eases round to <see cref="Wind.DirectionRadians"/>; calm leaves the cloth to gravity, and each level up lays it
/// flatter downwind with a sideways gust whose wave runs hoist to fly. Elsewhere the static mesh below is drawn as it
/// always was - it is also the simulation's starting pose, so the two never disagree about where the flag hangs.</para>
///
/// <para><b>Facing.</b> The faces point along +/-Z, so the house side reads the front picture and the runway side
/// the back one. A viewer on +Z sees +X to their left, so the front face's u runs from the fly end back toward the
/// pole while the back face's u runs pole to fly; that is the only asymmetry between the two, and it is what stops
/// one of the pictures reading backwards.</para>
///
/// <para><b>Button.</b> A red disc on the pole face one player above the platform runs the flag. A tap squashes it
/// flat for a moment and then lets it spring back, and the flag takes one step through its cycle: from the top it
/// starts falling, from the bottom rising, while moving it pauses, and a paused flag resumes the same direction.
/// The fall stops <see cref="LowStopGap"/> above the button, measured against the longest a calm cloth can hang -
/// the pinned 1.6 m hoist edge plus the full 2.4 m sheet width dropped straight down - so a dead-calm flag can
/// never cover the button that operates it.</para>
///
/// <para><b>Collision</b> splits the same way Tower does it: the platform is a walkable floor through
/// <see cref="FloorHeightAtWorld"/> (0.2 m is under PlayerCollider.StepLimit, so the player steps up onto it), and
/// the pole is a movement obstacle, so the column blocks the crossing and the disc around it does not.</para>
/// </summary>
internal class Flagpole : Panel
{
    // -- Where the pole stands. Both landmarks are mirrored here the way HotAirBalloon repeats the tower and the
    //    house: the numbers themselves belong to House.cs and AeroPlane.cs, and repeating them keeps the placement
    //    readable as a single line below.
    const float HouseCenterX = 40f;         // House.cs PosX
    const float HouseCenterZ = 35f;         // House.cs PosZ
    const float RunwayMidX = 40.875f;       // AeroPlane.TakeoffX, halfway along the asphalt
    const float RunwayMidZ = -7.05f;        // Pond.StairFootZ / SeaRoad centre line

    /// <summary>Midpoint of the two landmarks: 21 m from the house, 21 m from the runway.</summary>
    internal const float CenterX = (HouseCenterX + RunwayMidX) * 0.5f;   // 40.4375
    internal const float CenterZ = (HouseCenterZ + RunwayMidZ) * 0.5f;   // 13.975

    // -- Sizes in metres. A player height is 1 m, so every measurement in the brief is also a metre count. --
    const float PlatformHeight = 0.2f;      // 1/5 player
    const float PlatformRadius = 0.9f;      // open in the brief: a rim to stand on beside a 0.5 m column
    const float PoleHeight = 15f;           // 15 players: 1.5x the original 10 m, so the flag clears more sky
    const float PoleRadius = 0.25f;         // half a player across
    const float PoleTopY = PlatformHeight + PoleHeight;   // 15.2, standing on the platform
    const int PlatformSegments = 32;
    const int PoleSegments = 24;

    // -- Flag. 2.4 x 1.6 m of cloth, 3 cm thick. --
    const float FlagWidth = 2.4f;
    const float FlagHeight = 1.6f;
    const float ClothThickness = 0.03f;
    const float FlagTopY = PoleTopY - 0.15f;    // 15.05, leaving the pole's top cap visible above the cloth
    const float HoistX = 0.20f;                 // inside the pole radius, so the cloth leaves the pole with no gap
    const float FlyX = HoistX + FlagWidth;      // 2.60

    // -- Red operating button on the pole face, one player above the platform (a player is 1 m). It faces -Z, the
    //    runway side the player walks in from, so the column never hides it on approach. --
    const float ButtonY = PlatformHeight + 1f;  // 1.2, the cap's centre height
    const float ButtonRadius = 0.155f;          // 31 cm across: a fist-sized target
    const float ButtonThickness = 0.08f;        // cap depth, 1 cm of it sunk into the collar
    const float ButtonCollarRadius = 0.20f;
    const float ButtonCollarDepth = 0.035f;     // 3 cm proud of the pole plus 5 mm buried
    const float ButtonPressedScale = 0.3f;      // depth scale at full press: the 8 cm cap becomes 2.4 cm
    const float ButtonHitRadius = 0.30f;        // tap sphere, deliberately larger than the cap
    const float ButtonHoldSeconds = 0.08f;      // the squash is held this long before it starts to spring back
    const float ButtonPressRate = 40f;          // 1/s approach to full press
    const float ButtonReleaseRate = 8f;         // 1/s exponential recovery
    const int ButtonSegments = 24;

    // -- Flag travel. The bottom stop is derived from the button rather than authored as a height: in dead calm the
    //    cloth is pinned along a 1.6 m vertical hoist edge and hangs its 2.4 m width straight down, so the lowest
    //    point it can possibly reach is FlagHeight + FlagWidth below the hoist top. Keeping that worst case
    //    LowStopGap above the button's top is what stops a fallen flag from ever covering the button. --
    const float FlagTravelSpeed = 0.9f;         // m/s: about eleven seconds between the stops
    const float FlagTravelStep = 0.25f;         // longest frame the lift integrates in one step
    const float LowStopGap = 0.12f;             // clear air between the button's top and the longest calm hang
    const float ButtonTopY = ButtonY + ButtonRadius;
    const float LowStopLift = ButtonTopY + LowStopGap + FlagHeight + FlagWidth - FlagTopY;   // -9.575

    // -- Cloth shape: a gust frozen in the mesh. Both terms are zero at the hoist, which is what keeps the edge
    //    against the pole straight.
    const float FoldAmplitude = 0.09f;      // 9 cm peak of the fold
    const float FoldWaves = 1.8f;           // waves from hoist to fly
    const float FoldPhase = 0.7f;
    const float DroopAtFly = 0.08f;         // the fly end hangs 8 cm below the hoist
    const int ClothColumns = 24;            // 10 cm columns: 13 samples per wave, and a smooth silhouette
    const int ClothRows = 16;               // 10 cm rows too: the XPBD grid is square, one particle per vertex
    const float ClothSpacing = FlagWidth / ClothColumns;    // 0.1 m, and FlagHeight / ClothRows is the same
    const int ClothGridColumns = ClothColumns + 1;          // 25 particles hoist to fly
    const int ClothGridRows = ClothRows + 1;                // 17 particles top to bottom

    // -- Wind response, tuned against .dbg/_cloth_check/cloth_ref.py (calm droop 76°, level 3 44°, level 10 8°). --
    const float SwivelTimeConstant = 2f;    // seconds for the swivel to follow a change of wind bearing
    const float FlutterBaseHz = 0.6f;       // flutter frequency at a breath of wind ...
    const float FlutterHzPerSpeed = 0.12f;  // ... rising a little with every m/s
    const float GustGain = 0.2f;            // sideways gust in m/s = GustGain * speed^GustExponent: tension grows with the
    const float GustExponent = 1.75f;       // wind, so a linear gust would flutter less, not more, at the top of the ladder
    const float GustWaves = 0.6f;           // flutter wavelengths across the sheet, hoist to fly
    const float MaxClothStep = 1f / 30f;    // a longer frame is simulated as this one: a hitch slows the cloth, never tears it

    static readonly Vector4 PlatformColor = new(0.60f, 0.58f, 0.54f, 1f);
    static readonly Vector4 PoleColor = new(0.38f, 0.27f, 0.18f, 1f);
    static readonly Vector4 ClothEdgeColor = new(0.94f, 0.93f, 0.90f, 1f);
    static readonly Vector4 ButtonColor = new(0.72f, 0.10f, 0.08f, 1f);
    static readonly Vector4 ButtonCollarColor = new(0.20f, 0.19f, 0.18f, 1f);

    // Resolution by name under Raw/, the same rule the skybox textures follow. The files are written by
    // .dbg/_flag_assets: both are pirate art printed on paper white.
    const string FrontPicture = "Assets/FlagPirateFront.png";
    const string BackPicture = "Assets/FlagPirateBack.png";

    // Pole-local box of every part, remapped to world space by Update.
    readonly List<(Mesh3D Part, Vector3 Center, Vector3 Size)> parts = new();

    /// <summary>Stone disc under the pole. A walkable floor, never an obstacle; see <see cref="FloorHeightAtWorld"/>.</summary>
    internal Mesh3D Platform { get; }

    /// <summary>Pole column. App registers it as a movement obstacle, so the player walks around it.</summary>
    internal Mesh3D Pole { get; }

    /// <summary>Cloth: front picture, back picture and the rim that closes the 3 cm edge, in one mesh.</summary>
    internal Mesh3D Flag { get; }

    /// <summary>Red operating cap on the pole face. A tap squashes it along its own axis while it runs the flag; the
    /// dark collar it sits in is a separate static part.</summary>
    internal Mesh3D Button { get; }

    /// <summary>The GPU sheet driving <see cref="Flag"/>'s three surfaces, or null where the backend declined it and the
    /// static mesh is drawn instead.</summary>
    internal Season.Rendering.Effects.ClothSimulationEffect? Cloth => cloth;

    Season.Rendering.Effects.ClothSimulationEffect? cloth;

    // Host side of the wind response. All three are integrated with the frame delta rather than derived from elapsed
    // time, so a change of level bends the frequency and a change of bearing turns the swivel without either jumping.
    float swivelYaw;
    float swivelRate;
    float flutterPhase;
    bool swivelPrimed;

    // Pole-local centre of the red cap, the point the tap sphere is built around.
    readonly Vector3 buttonCenter;

    // Flag travel state. flagLift is 0 with the hoist at its top stop and LowStopLift at the bottom one; the
    // direction outlives a pause so the next tap resumes the same way. No serialization: the flag loads already
    // lowered to the bottom stop, so the first tap is what raises it.
    float flagLift = LowStopLift;
    float flagDirection = 1f;
    bool flagMoving;

    // Button squash state, 0 at rest and 1 fully flattened; buttonHold keeps it down briefly after a tap.
    float buttonPress;
    float buttonHold;

    internal Flagpole()
    {
        var platform = new CylinderBuilder();
        platform.Add(0f, PlatformHeight, PlatformRadius, PlatformSegments);
        Platform = platform.Build("FlagpolePlatform", PlatformColor, out var platformCenter, out var platformSize);
        AddPart(Platform, platformCenter, platformSize, Albedo(PlatformColor));

        var pole = new CylinderBuilder();
        pole.Add(PlatformHeight, PoleTopY, PoleRadius, PoleSegments);
        Pole = pole.Build("FlagpolePole", PoleColor, out var poleCenter, out var poleSize);
        AddPart(Pole, poleCenter, poleSize, Albedo(PoleColor));

        // Operating button: a dark collar bedded into the -Z face, and the red cap that squashes. Both are discs
        // whose axis points along -Z; the cap's back is sunk 1 cm into the collar so a press never opens a gap.
        const float collarBackZ = -(PoleRadius - 0.005f);
        const float collarFrontZ = -(PoleRadius + ButtonCollarDepth);

        var collar = new DiscBuilder();
        collar.Add(ButtonY, collarBackZ, collarFrontZ, ButtonCollarRadius, ButtonSegments);
        var collarPart = collar.Build("FlagpoleButtonCollar", ButtonCollarColor, 0.75f, out var collarCenter, out var collarSize);
        AddPart(collarPart, collarCenter, collarSize, Albedo(ButtonCollarColor));

        var button = new DiscBuilder();
        const float capBackZ = collarFrontZ + 0.01f;
        button.Add(ButtonY, capBackZ, capBackZ - ButtonThickness, ButtonRadius, ButtonSegments);
        Button = button.Build("FlagpoleButton", ButtonColor, 0.45f, out buttonCenter, out var buttonSize);
        AddPart(Button, buttonCenter, buttonSize, Albedo(ButtonColor));

        Flag = BuildCloth(out var clothCenter, out var clothSize);
        // Paper white, which is most of the cloth: the DDGI proxy should bounce what the flag actually looks like.
        AddPart(Flag, clothCenter, clothSize, new Vector3(0.85f, 0.85f, 0.83f));

        cloth = RegisterCloth(clothCenter);
    }

    /// <summary>
    /// Starts the GPU sheet and points the three surfaces at its streams, or returns null and leaves the static mesh
    /// alone. Same shape as Campfire.Register: a declined Initialize has already released its own resources, so the
    /// never-initialized effect is all there is left to dispose.
    /// </summary>
    Season.Rendering.Effects.ClothSimulationEffect? RegisterCloth(Vector3 flagCenter)
    {
        var graphics = Season.Basic.Graphics.Instance;
        if (!graphics.VertexInputStorageSupported) return null;

        var effect = new Season.Rendering.Effects.ClothSimulationEffect(CreateClothDesc(flagCenter));
        if (!Season.Rendering.FrameSchedule.RegisterCompute(graphics, effect))
        {
            effect.Dispose();
            return null;
        }

        Flag.Surfaces[0].VertexStream = effect.FrontVertices;
        Flag.Surfaces[1].VertexStream = effect.BackVertices;
        Flag.Surfaces[2].VertexStream = effect.RimVertices;
        return effect;
    }

    /// <summary>
    /// The sheet the kernel simulates, in pole-local metres - the pole axis is the simulation Y axis the swivel turns
    /// about and the collision cylinder wraps. It starts on the static mesh's mid-surface, and the hoist column is the
    /// pinned one; Origin is the mesh centre, so the kernel writes the same local space the static vertices live in.
    /// </summary>
    static Season.Rendering.Effects.ClothDesc CreateClothDesc(Vector3 flagCenter)
    {
        int count = ClothGridColumns * ClothGridRows;
        var rest = new Vector3[count];
        var pinned = new bool[count];

        for (int j = 0; j < ClothGridRows; j++)
        {
            for (int i = 0; i < ClothGridColumns; i++)
            {
                int p = j * ClothGridColumns + i;
                rest[p] = ClothPoint(GridC(i), GridT(j), 0f);
                pinned[p] = i == 0;
            }
        }

        return new Season.Rendering.Effects.ClothDesc
        {
            Columns = ClothGridColumns,
            Rows = ClothGridRows,
            Spacing = ClothSpacing,
            Rest = rest,
            Pinned = pinned,
            Origin = flagCenter,
            HalfThickness = ClothThickness * 0.5f,
            CollideRadius = PoleRadius + 0.02f,
            FlipFrontU = true,
        };
    }

    public override void Dispose()
    {
        if (cloth != null)
        {
            // Unregistering releases the streams, so no surface may keep pointing at them.
            foreach (var surface in Flag.Surfaces)
                surface.VertexStream = null;

            Season.Rendering.FrameSchedule.UnregisterCompute(cloth);
            cloth = null;
        }

        base.Dispose();
    }

    /// <summary>
    /// Walkable height in world Y at a world-space XZ point: the platform top, otherwise 0 for the grass. Same
    /// contract as Tower.FloorHeightAtWorld. On the pole itself the platform still reports its top because the
    /// column is an obstacle rather than a floor - the player stops at its face, not on top of it.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        float dx = worldX - PosX;
        float dz = worldZ - PosZ;

        return dx * dx + dz * dz <= PlatformRadius * PlatformRadius ? PosY + PlatformHeight : 0f;
    }

    /// <summary>DDGI albedo from a part's visible colour, the way Room, Pond and Tower pass theirs.</summary>
    static Vector3 Albedo(Vector4 color) => new(color.X, color.Y, color.Z);

    /// <summary>
    /// Registers one part for per-frame placement. The explicit control albedo matches the visible colour, as Room,
    /// Pond and Tower do for their parts.
    /// </summary>
    void AddPart(Mesh3D part, Vector3 center, Vector3 size, Vector3 albedo)
    {
        part.GiAlbedo = albedo;

        AddControl(part);
        parts.Add((part, center, size));
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // The frame delta drives the flag travel and the button squash; both clamp a long frame so a hitch moves
        // them by a bounded amount instead of teleporting either.
        float dt = float.IsFinite(time) ? Math.Clamp(time, 0f, FlagTravelStep) : 0f;
        StepFlag(dt);
        StepButton(dt);

        // Same per-frame placement as Room, Pond and Tower: translate each part center and keep its size in metres,
        // so ComputedScale stays 1 and nothing is scaled away from the authored dimensions. On top of that the flag
        // rides the pole by flagLift and the red cap squashes along its own -Z axis.
        for (int i = 0; i < parts.Count; i++)
        {
            var (part, center, size) = parts[i];

            part.PosX = PosX + center.X;
            part.PosY = PosY + center.Y + (ReferenceEquals(part, Flag) ? flagLift : 0f);
            part.PosZ = PosZ + center.Z;
            part.Width = size.X;
            part.Height = size.Y;
            part.Depth = size.Z;

            if (ReferenceEquals(part, Button))
            {
                // Scaling shrinks the cap about its centre, which would lift its mounted face off the collar; the
                // shift of half the removed depth puts that face back on the pole, so the cap flattens inward the
                // way a real button does instead of floating away from its bezel.
                float depthNow = size.Z * (1f - (1f - ButtonPressedScale) * buttonPress);
                part.Depth = depthNow;
                part.PosZ = PosZ + center.Z + (size.Z - depthNow) * 0.5f;
            }

            if (part.Update(time))
            {
                result = true;
            }
        }

        StepCloth(time);

        return result;
    }

    /// <summary>
    /// One step of the flag's travel. The lift is clamped to the two stops, and reaching either one ends the move;
    /// a paused flag simply stops integrating, which is what lets the next tap resume the same direction.
    /// </summary>
    void StepFlag(float dt)
    {
        if (!flagMoving || dt <= 0f)
            return;

        flagLift += flagDirection * FlagTravelSpeed * dt;

        if (flagLift >= 0f)
        {
            flagLift = 0f;
            flagMoving = false;
        }
        else if (flagLift <= LowStopLift)
        {
            flagLift = LowStopLift;
            flagMoving = false;
        }
    }

    /// <summary>
    /// Exponential squash and recovery of the red cap. The tap sets the hold, so the cap is already visibly flat by
    /// the end of the click frame, and the slower release rate is what reads as a spring.
    /// </summary>
    void StepButton(float dt)
    {
        if (buttonHold > 0f)
            buttonHold = MathF.Max(0f, buttonHold - dt);

        float target = buttonHold > 0f ? 1f : 0f;
        float rate = target > buttonPress ? ButtonPressRate : ButtonReleaseRate;

        if (dt > 0f)
            buttonPress += (target - buttonPress) * MathF.Min(1f, rate * dt);
    }

    /// <summary>
    /// One tap of the button: pause a moving flag, resume a paused one, and start a stopped one away from the stop
    /// it is resting at. The direction survives a pause, so a resume always retraces the interrupted travel.
    /// </summary>
    void ToggleFlag()
    {
        if (flagMoving)
        {
            flagMoving = false;
            return;
        }

        if (flagLift <= LowStopLift + 1e-4f)
            flagDirection = 1f;
        else if (flagLift >= -1e-4f)
            flagDirection = -1f;

        flagMoving = true;
    }

    /// <summary>
    /// Resolves a tap on the red button. App calls this before the object picker updates, so a release that lands
    /// on the button is consumed here and the picker does not also select the pole standing behind it; a miss
    /// leaves the release alone. The hit test is a forgiving sphere around the cap, because the disc itself is only
    /// 31 cm across, and only a ray arriving from -Z may press it - from the house side the column is in the way.
    /// </summary>
    internal void TryTapButton()
    {
        if (!TouchService.IsReleased || TouchService.PoX is not int pointerX || TouchService.PoY is not int pointerY)
            return;

        var app = App.Instance;

        if (app == null
            || !Season.Rendering.Picking.ScreenPointToRay(pointerX, pointerY, Season.Rendering.RenderView.Current,
                app.ExtendResolution.X, app.ExtendResolution.Y, out var origin, out var direction)
            || direction.Z <= 0f
            || !ButtonUnderRay(origin, direction))
            return;

        TouchService.IsReleased = false;   // The tap is this control's; the picker must not see the same click.
        ToggleFlag();
        buttonHold = ButtonHoldSeconds;
    }

    bool ButtonUnderRay(Vector3 origin, Vector3 direction)
    {
        var offset = origin - new Vector3(PosX + buttonCenter.X, PosY + buttonCenter.Y, PosZ + buttonCenter.Z);

        // direction is unit length, so the ray meets the sphere when b^2 >= c with the entry in front of the origin.
        float b = Vector3.Dot(offset, direction);
        float c = Vector3.Dot(offset, offset) - ButtonHitRadius * ButtonHitRadius;

        return c <= 0f || (b < 0f && b * b >= c);
    }

    /// <summary>
    /// Feeds one frame of wind to the GPU sheet. The pole's axes are the world's (the panel only translates), so
    /// <see cref="Wind.Velocity"/> is already in simulation space.
    /// </summary>
    void StepCloth(float time)
    {
        if (cloth == null)
            return;

        float dt = float.IsFinite(time) ? Math.Clamp(time, 0f, MaxClothStep) : 0f;
        if (dt <= 0f)
            return;

        float speed = Wind.Speed;

        // Swivel: a critically damped spring onto the wind bearing, over the shortest arc. The very first step snaps it
        // there instead, because the sheet's first dispatch resets onto the swivelled rest pose and a flag that spends
        // two seconds swinging round from +X on every load reads as a bug. Calm holds the last bearing.
        float target = Wind.DirectionRadians;
        if (!swivelPrimed)
        {
            if (speed > 0f)
                swivelYaw = target;
            swivelRate = 0f;
            swivelPrimed = true;
        }
        else if (speed > 0f)
        {
            float omega = 1f / SwivelTimeConstant;
            float error = MathF.IEEERemainder(target - swivelYaw, MathF.Tau);
            swivelRate += (omega * omega * error - 2f * omega * swivelRate) * dt;
            swivelYaw = MathF.IEEERemainder(swivelYaw + swivelRate * dt, MathF.Tau);
        }
        else
        {
            swivelRate = 0f;
        }

        float frequency = FlutterBaseHz + FlutterHzPerSpeed * speed;
        flutterPhase = MathF.IEEERemainder(flutterPhase + MathF.Tau * frequency * dt, MathF.Tau);

        cloth.Step(dt, Wind.Velocity, GustGain * MathF.Pow(speed, GustExponent), flutterPhase, MathF.Tau * GustWaves, swivelYaw);

        // The sheet moves without its bounds moving, so this is what tells a reused shadow tile to redraw it.
        Flag.VertexStreamGeneration++;
    }

    /// <summary>
    /// Builds the cloth as one Mesh3D of three indexed surfaces on the simulation grid: the +Z face with the front
    /// picture, the -Z face with the back picture, and the rim along the boundary that joins them. Each face has one
    /// vertex per particle and the rim two per boundary point, in exactly the order the cloth kernel writes them, so a
    /// compute stream can stand in for any of the three vertex arrays one for one. center and size come back in
    /// pole-local metres for <see cref="Update"/> to place the control with.
    /// </summary>
    static Mesh3D BuildCloth(out Vector3 center, out Vector3 size)
    {
        var front = new Sheet(ClothGridColumns * ClothGridRows);
        var back = new Sheet(ClothGridColumns * ClothGridRows);
        var rim = new Sheet(2 * Season.Rendering.Effects.ClothSimulationEffect.RimLoopLength(ClothGridColumns, ClothGridRows));

        AddClothFace(front, side: +1f, flipU: true);
        AddClothFace(back, side: -1f, flipU: false);
        AddClothRim(rim);

        var min = Vector3.Min(front.Min, Vector3.Min(back.Min, rim.Min));
        var max = Vector3.Max(front.Max, Vector3.Max(back.Max, rim.Max));

        center = (min + max) * 0.5f;
        size = max - min;

        var cloth = new Mesh3D()
        {
            Name = "FlagpoleFlag",
            // The simulated sheet swings round the pole and away from these bounds, so neither the view frustum nor a
            // shadow cascade may cull it by them.
            CullingEnabled = false,
        };

        cloth.Surfaces.Add(front.ToSurface(Vector4.One, FrontPicture, center));
        cloth.Surfaces.Add(back.ToSurface(Vector4.One, BackPicture, center));
        // No path: the empty string is the documented pure-color mode, so the rim is the cloth edge colour alone.
        cloth.Surfaces.Add(rim.ToSurface(ClothEdgeColor, string.Empty, center));

        return cloth;
    }

    /// <summary>Hoist-to-fly coordinate of grid column i, 0 at the pole and 1 at the fly.</summary>
    static float GridC(int i) => i / (float)ClothColumns;

    /// <summary>Top-to-bottom coordinate of grid row j, 0 along the top edge and 1 along the bottom.</summary>
    static float GridT(int j) => j / (float)ClothRows;

    /// <summary>
    /// One face of the cloth: vertex j * columns + i is particle (i, j). The texture coordinate is (u, t) with t the
    /// picture's vertical axis, 0 on the top edge, so the art is never rotated. flipU is the only difference between
    /// the two faces: a viewer on +Z has +X to their left, so the front picture's u has to run from the fly end back
    /// to the pole, while the back picture's u runs pole to fly. The kernel writes the same (u, t).
    /// </summary>
    static void AddClothFace(Sheet sheet, float side, bool flipU)
    {
        // Flat shading for the static sheet: it is lit as one plane per side, so the slight fold never turns into a
        // facet. The simulated sheet replaces this with its own per-particle normal.
        var normal = new Vector3(0f, 0f, side);

        for (int j = 0; j < ClothGridRows; j++)
        {
            for (int i = 0; i < ClothGridColumns; i++)
            {
                float c = GridC(i);
                float t = GridT(j);
                sheet.SetVertex(j * ClothGridColumns + i, ClothPoint(c, t, side), new Vector2(flipU ? 1f - c : c, t), normal);
            }
        }

        for (int j = 0; j < ClothRows; j++)
        {
            for (int i = 0; i < ClothColumns; i++)
            {
                int hoistTop = j * ClothGridColumns + i;
                int flyTop = hoistTop + 1;
                int hoistBottom = hoistTop + ClothGridColumns;
                int flyBottom = hoistBottom + 1;

                // Top-left as this side sees it: the hoist corner from -Z, the fly corner from +Z.
                if (flipU)
                    sheet.AddQuad(flyTop, hoistTop, hoistBottom, flyBottom);
                else
                    sheet.AddQuad(hoistTop, flyTop, flyBottom, hoistBottom);
            }
        }
    }

    /// <summary>
    /// Closes the slab. The boundary is walked clockwise in (c, t) - along the top, down the fly, back along the
    /// bottom, up the hoist, see ClothSimulationEffect.RimLoopParticle - and boundary point k owns vertex 2k on the
    /// front edge and 2k + 1 on the back edge. Each segment is a quad from the front edge to the back edge, and the
    /// vertex normal is the in-plane outward direction cross(+Z, T) the kernel also writes. The hoist edge is inside
    /// the pole and never seen, but it costs 32 triangles and keeps the shell closed.
    /// </summary>
    static void AddClothRim(Sheet sheet)
    {
        int length = Season.Rendering.Effects.ClothSimulationEffect.RimLoopLength(ClothGridColumns, ClothGridRows);

        for (int k = 0; k < length; k++)
        {
            var (c, t) = LoopCoordinate(k);
            var (cNext, tNext) = LoopCoordinate((k + 1) % length);
            var (cPrev, tPrev) = LoopCoordinate((k + length - 1) % length);

            var tangent = ClothPoint(cNext, tNext, 0f) - ClothPoint(cPrev, tPrev, 0f);
            var outward = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, tangent));
            var uv = new Vector2(c, t);

            sheet.SetVertex(2 * k, ClothPoint(c, t, +1f), uv, outward);
            sheet.SetVertex(2 * k + 1, ClothPoint(c, t, -1f), uv, outward);
        }

        for (int k = 0; k < length; k++)
        {
            int next = (k + 1) % length;
            sheet.AddQuad(2 * k, 2 * next, 2 * next + 1, 2 * k + 1);
        }
    }

    /// <summary>(c, t) of boundary point k, through the kernel's own walk so the two can never drift apart.</summary>
    static (float C, float T) LoopCoordinate(int k)
    {
        int p = Season.Rendering.Effects.ClothSimulationEffect.RimLoopParticle(ClothGridColumns, ClothGridRows, k);
        return (GridC(p % ClothGridColumns), GridT(p / ClothGridColumns));
    }

    /// <summary>
    /// A point on the cloth in pole-local metres. c runs 0 at the hoist to 1 at the fly, t runs 0 along the top edge
    /// to 1 along the bottom, and side is +1 for the face looking toward +Z and -1 for the one looking toward -Z.
    /// </summary>
    static Vector3 ClothPoint(float c, float t, float side)
    {
        // Pinned at the pole and free at the fly, so the fold can never lift the hoist edge off the pole. The
        // amplitude also grows toward the bottom edge, which is what makes the sheet read as hanging cloth rather
        // than as a cut board.
        float fold = FoldAmplitude * MathF.Sin(FoldPhase + FoldWaves * MathF.Tau * c) * MathF.Sqrt(c);
        fold *= 0.7f + 0.3f * t;

        float top = FlagTopY - DroopAtFly * c * c;

        return new Vector3(HoistX + FlagWidth * c, top - FlagHeight * t, fold + side * ClothThickness * 0.5f);
    }

    /// <summary>
    /// A short cylinder whose axis points along the local -Z: a side ring plus a front cap, with no back cap because
    /// the back sits against the pole. Geometry is fed in pole-local metres and <see cref="Build"/> recenters it on
    /// its own bounds, exactly like <see cref="CylinderBuilder"/>, so the control anchor is the disc centre and
    /// Update only has to translate and scale it. Winding follows the same rule: cross(b - a, c - a) points out of
    /// the surface for every triangle.
    /// </summary>
    sealed class DiscBuilder
    {
        readonly List<Vertex> vertices = new();
        readonly List<ushort> indices = new();
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        /// <summary>Circle centred on (0, y) in the XY plane, from zBack out to zFront (zFront the more negative),
        /// closed with a cap on the front.</summary>
        public void Add(float y, float zBack, float zFront, float radius, int segments)
        {
            min = Vector3.Min(min, new Vector3(-radius, y - radius, zFront));
            max = Vector3.Max(max, new Vector3(radius, y + radius, zBack));

            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments;
                float a1 = (i + 1) * MathF.Tau / segments;

                var b0 = Polar(y, zBack, radius, a0);
                var b1 = Polar(y, zBack, radius, a1);
                var f0 = Polar(y, zFront, radius, a0);
                var f1 = Polar(y, zFront, radius, a1);

                // One normal per quad at the mid angle, the same flat shading the pole uses at this size.
                var normal = new Vector3(MathF.Cos((a0 + a1) * 0.5f), MathF.Sin((a0 + a1) * 0.5f), 0f);
                AddTriangle(b0, f0, f1, normal);
                AddTriangle(b0, f1, b1, normal);

                // Front cap, wound so the fan faces -Z.
                AddTriangle(new Vector3(0f, y, zFront), f1, f0, -Vector3.UnitZ);
            }
        }

        static Vector3 Polar(float y, float z, float radius, float angle)
            => new(radius * MathF.Cos(angle), y + radius * MathF.Sin(angle), z);

        void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 normal)
        {
            ushort b = (ushort)vertices.Count;

            vertices.Add(MakeVertex(p0, new Vector2(0f, 0f), normal));
            vertices.Add(MakeVertex(p1, new Vector2(1f, 0f), normal));
            vertices.Add(MakeVertex(p2, new Vector2(0f, 1f), normal));

            indices.Add(b);
            indices.Add((ushort)(b + 1));
            indices.Add((ushort)(b + 2));
        }

        public Mesh3D Build(string name, Vector4 color, float roughness, out Vector3 center, out Vector3 size)
        {
            center = (min + max) * 0.5f;
            size = max - min;

            var verts = vertices.ToArray();
            for (int i = 0; i < verts.Length; i++)
                verts[i].Position -= center;

            var part = new Mesh3D()
            {
                Name = name,
                CullingEnabled = false,
            };

            part.Surfaces.Add(new Surface
            {
                Vertices = verts,
                Indices = indices.ToArray(),
                BaseColor = color,
                MetallicFactor = 0f,
                RoughnessFactor = roughness,
                // Written out because Surface.Unlit defaults to true, which would flatten the red to its base color
                // and lose the sun that makes the disc read as a physical button.
                Unlit = false,
                Mode = SurfaceBlendMode.Opaque,
            });

            return part;
        }
    }

    /// <summary>
    /// Accumulates upright columns into one indexed triangle list, so a part costs one Surface and one draw call.
    /// Geometry is fed in pole-local metres; <see cref="Build"/> then recenters it on its own bounds, which puts the
    /// control anchor at the local origin and leaves Update with nothing to do but translate. The construction is
    /// Tower's BoxBuilder.AddCylinder - one normal per side segment, plus a top cap - because both columns here stand
    /// on something and never show a bottom face.
    /// </summary>
    sealed class CylinderBuilder
    {
        readonly List<Vertex> vertices = new();
        readonly List<ushort> indices = new();
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        /// <summary>Column around the local Y axis, from y0 up to y1, closed with a cap at y1.</summary>
        public void Add(float y0, float y1, float radius, int segments)
        {
            min = Vector3.Min(min, new Vector3(-radius, y0, -radius));
            max = Vector3.Max(max, new Vector3(radius, y1, radius));

            var center = new Vector3(0f, y1, 0f);

            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments;
                float a1 = (i + 1) * MathF.Tau / segments;

                var b0 = Polar(y0, radius, a0);
                var b1 = Polar(y0, radius, a1);
                var t0 = Polar(y1, radius, a0);
                var t1 = Polar(y1, radius, a1);

                // One normal per quad, taken at the mid angle: flat shading is indistinguishable at this radius.
                var normal = new Vector3(MathF.Cos((a0 + a1) * 0.5f), 0f, MathF.Sin((a0 + a1) * 0.5f));
                AddTriangle(b0, t0, t1, normal);
                AddTriangle(b0, t1, b1, normal);

                // Top cap, wound so the fan faces +Y.
                AddTriangle(center, Polar(y1, radius, a1), Polar(y1, radius, a0), Vector3.UnitY);
            }
        }

        static Vector3 Polar(float y, float radius, float angle)
            => new(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));

        void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 normal)
        {
            ushort b = (ushort)vertices.Count;

            vertices.Add(MakeVertex(p0, new Vector2(0f, 0f), normal));
            vertices.Add(MakeVertex(p1, new Vector2(1f, 0f), normal));
            vertices.Add(MakeVertex(p2, new Vector2(0f, 1f), normal));

            indices.Add(b);
            indices.Add((ushort)(b + 1));
            indices.Add((ushort)(b + 2));
        }

        public Mesh3D Build(string name, Vector4 color, out Vector3 center, out Vector3 size)
        {
            center = (min + max) * 0.5f;
            size = max - min;

            var verts = vertices.ToArray();
            for (int i = 0; i < verts.Length; i++)
                verts[i].Position -= center;

            var part = new Mesh3D()
            {
                Name = name,
                CullingEnabled = false,
            };

            part.Surfaces.Add(new Surface
            {
                Vertices = verts,
                Indices = indices.ToArray(),
                BaseColor = color,
                MetallicFactor = 0f,
                RoughnessFactor = 0.85f,
                // Written out because Surface.Unlit defaults to true, which would flatten stone and wood to their
                // base color and lose the sun entirely.
                Unlit = false,
                Mode = SurfaceBlendMode.Opaque,
            });

            return part;
        }
    }

    /// <summary>
    /// One indexed surface under construction: a fixed vertex array addressed by grid or loop index, the triangles
    /// over it, and the bounds they cover. Quads go in through <see cref="AddQuad"/>, which fixes the winding once for
    /// every caller.
    /// </summary>
    sealed class Sheet
    {
        public readonly Vertex[] Vertices;
        public readonly List<ushort> Indices = new();
        public Vector3 Min = new(float.MaxValue);
        public Vector3 Max = new(float.MinValue);

        public Sheet(int vertexCount) => Vertices = new Vertex[vertexCount];

        public void SetVertex(int index, Vector3 position, Vector2 uv, Vector3 normal)
        {
            Min = Vector3.Min(Min, position);
            Max = Vector3.Max(Max, position);

            Vertices[index] = MakeVertex(position, uv, normal);
        }

        /// <summary>
        /// One quad in the engine's front-face order: top-left, top-right, bottom-right, bottom-left as seen from
        /// outside. Triangles are TL-TR-BR and TL-BR-BL, so cross(b - a, c - a) points out of the surface for every
        /// winding used here - the +Z face, the -Z face and the rim - without flipping the index pattern per side.
        /// </summary>
        public void AddQuad(int topLeft, int topRight, int bottomRight, int bottomLeft)
        {
            Indices.Add((ushort)topLeft);
            Indices.Add((ushort)topRight);
            Indices.Add((ushort)bottomRight);

            Indices.Add((ushort)topLeft);
            Indices.Add((ushort)bottomRight);
            Indices.Add((ushort)bottomLeft);
        }

        public Surface ToSurface(Vector4 color, string texturePath, Vector3 origin)
        {
            var verts = (Vertex[])Vertices.Clone();
            for (int i = 0; i < verts.Length; i++)
                verts[i].Position -= origin;

            return new Surface
            {
                Vertices = verts,
                Indices = Indices.ToArray(),
                BaseColorTexturePath = texturePath,
                BaseColor = color,
                MetallicFactor = 0f,
                RoughnessFactor = 0.9f,
                // The unlit default would paste the art back at full brightness and the flag would glow at night.
                // PBR keeps the paper white in daylight and lets the cloth fall dark with the rest of the island.
                Unlit = false,
                // One-sided on purpose: the cloth is a closed 3 cm slab, so whatever pose the simulation twists it into,
                // some face points at the camera and some face at the sun.
                DoubleSided = false,
                Mode = SurfaceBlendMode.Opaque,
            };
        }
    }

    static Vertex MakeVertex(Vector3 position, Vector2 uv, Vector3 normal)
    {
        return new Vertex
        {
            Position = position,
            TexCoord = uv,
            Normal = normal,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }
}
