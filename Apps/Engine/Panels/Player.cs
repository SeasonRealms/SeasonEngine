// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Player : Panel
{
    // Long-jump state: starts on click, advances in UpdateLongJump, and resets on landing.
    internal bool jumping;

    bool modelFirst = true;

    bool modelSettled;

    bool torchSettled;

    internal Season.Controls.Model model;

    internal Season.Controls.Model torch;

    // -- Torch rig: a carried torch welded to the front of the character, plus the point light it burns with. --
    const string TorchGlbPath = @"Assets/Torch.glb";

    // The flame marker reuses the unlit sphere the street lamp uses for its bulb. Torch.glb has no emissive
    // material, so without a marker the light would look like it comes out of bare wood once the sun is down.
    const string FlameGlbPath = @"Assets/Sun.glb";

    // The character stands 1 m tall, so a 0.7 m torch reads as something carried rather than a floor stand.
    const float TorchHeight = 0.7f;

    // Anchor-to-anchor distance in front of the character. The player box is 0.5 m deep and the scaled shaft is
    // about 0.2 m across, so 0.5 m keeps a clear gap instead of letting the torch intersect the body.
    const float TorchForward = 1f;

    // Bottom edge above the feet plane. The whole rig moves with the character, jump arc included, so a small
    // lift is what keeps it reading as carried instead of planted in the grass.
    const float TorchLift = 0.12f;

    const float FlameSize = 0.16f;

    // Warm flame colour. The torch is a carried light rather than a dusk-only lamp, so it keeps burning and keeps
    // warming the character at midday too, as asked, instead of switching itself off with the sun.
    static readonly Vector3 TorchLightColor = new Vector3(1f, 0.7f, 0.35f);

    // Point-light intensity follows the engine convention radiance = colour * intensity / d^2 with a window cutoff
    // at Range. 5 lights the ground out to roughly 4 m at night, which is what makes the surroundings readable,
    // while staying well below the sun by day; the 12 m range keeps the pool edge from ending on a hard circle.
    const float TorchLightIntensity = 5f;
    const float TorchLightRange = 12f;

    /// <summary>Flame marker at the torch tip: unlit, casts no shadows, never picked.</summary>
    Season.Controls.Model torchFlame;

    /// <summary>Always-on point light at the flame, moved every frame with the torch.</summary>
    Season.Rendering.LightSource torchLight;

    // Unified placement convention: (PosX, PosY, PosZ) is the world position of the
    // bounding-box anchor, namely the geometric center. This sample still thinks in terms
    // of "placing the model by its local origin", so writes are converted through
    // AnchorWorldOffset.
    // The local origin of 3DGodotRobot.glb is approximately at the center of the feet:
    // when the origin is at (0,0,0), the model stands on the origin, with the body in
    // y in [0,1] and the bottom resting on the y=0 plane, which matches grass level.
    internal Player()
    {
        model = new Season.Controls.Model()
        {
            Name = @"Assets/3DGodotRobot.glb",
            Highlight =
            {
                Style = HighlightStyle.Wireframe
            },
            PosX = 0,
            PosY = 0.5f,
            PosZ = 0,
            Width = 1,
            Height = 1,
            Depth = 0.5f,
            Rotation = (float)Math.PI
        };
        AddControl(model);

        // Size and position are settled from the character pose every frame, so only the asset is fixed here.
        // CastShadows keeps the default true: a carried torch is near-field geometry, not a background prop.
        torch = new Season.Controls.Model()
        {
            Name = TorchGlbPath,
        };
        AddControl(torch);

        torchFlame = new Season.Controls.Model()
        {
            Name = FlameGlbPath,
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(1.9f, 1.15f, 0.4f, 1f),
            CastShadows = false,
            Width = FlameSize,
            Height = FlameSize,
            Depth = FlameSize,
        };
        AddControl(torchFlame);

        // Registered once and then only ever moved: the light takes part in every frame's Bake as is, so it needs
        // no per-frame rebuild and stays lit day and night.
        // Point lights do not cast shadows in this engine, so the flame may emit from inside the torch head
        // without the mesh self-occluding the pool it throws.
        torchLight = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "Torch",
            Kind = Season.Rendering.LightKind.Point,
            Color = TorchLightColor,
            Intensity = TorchLightIntensity,
            Range = TorchLightRange,
            CastShadows = false,
            // Bake trims to eight GPU slots by priority, adding 1000 for directional lights and 500 for shadow
            // casters. 40 keeps the torch below every existing caster, so neither sun, moon, nor the lamps can
            // be displaced by it, while it still outranks plain unlit filler lights.
            Priority = 40,
        });

        // Place once at construction so the very first frame already lights the ground under the torch instead of
        // flashing at the world origin. Player.Update keeps it there from then on.
        PlaceTorch();
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Model defaults set explicitly in the constructor take priority.
        // Size values are assigned only when still zero, using normalized 0.2x scaling
        // (world meters = local size * OriginalScale * 0.2).
        // Position is assigned only when all components are zero, pinning the local origin to (0,0,0).
        if (!modelSettled && model.LocalSize != Vector3.Zero)
        {
            if (model.Width == 0) model.Width = model.LocalSize.X * model.OriginalScale * 0.2f;
            if (model.Height == 0) model.Height = model.LocalSize.Y * model.OriginalScale * 0.2f;
            if (model.Depth == 0) model.Depth = model.LocalSize.Z * model.OriginalScale * 0.2f;

            if (model.PosX == 0 && model.PosY == 0 && model.PosZ == 0)
            {
                var pos = model.AnchorWorldOffset;
                model.PosX = pos.X;
                model.PosY = pos.Y;
                model.PosZ = pos.Z;
            }
            modelSettled = true;
        }

        // Torch proportions come from the asset rather than from constants: Torch.glb is a thin shaft of unit
        // height, and a uniform scale derived from its bounds keeps that shape instead of squaring it off.
        if (!torchSettled && torch.LocalSize.Y > 1e-6f)
        {
            float scale = TorchHeight / torch.LocalSize.Y;

            torch.Width = torch.LocalSize.X * scale;
            torch.Height = TorchHeight;
            torch.Depth = torch.LocalSize.Z * scale;

            torchSettled = true;
        }

        // Direction and Skill have already moved and turned the character by the time App.Update reaches this
        // panel, so the rig follows this frame's final pose rather than a frame-old one.
        PlaceTorch();

        // Ready is set by the load queue before the shared-model continuation injects
        // Asset, and PlayAnimation silently no-ops while Asset is null. Retry every frame
        // until the switch actually lands, and only then consume the first-time flag.
        if (model.Ready && modelFirst)
        {
            if (model.PlayAnimation("Idle-loop") != null)
                modelFirst = false;
        }

        if (model.Update(time))
        {
            result = true;
        }

        // Panel.Update does not recurse into children, so the torch rig is driven explicitly here.
        if (torch.Update(time))
        {
            result = true;
        }

        if (torchFlame.Update(time))
        {
            result = true;
        }

        return result;
    }

    /// <summary>
    /// Welds the torch rig to the front of the character: the anchor is offset along the facing direction on the XZ
    /// plane, and every height is measured from the feet plane so steps and the long-jump arc carry the torch along
    /// instead of leaving it behind.
    ///
    /// The flame marker and the light share one point, so the illumination always comes out of the visible flame.
    /// SceneLighting.Bake has already run for this frame by the time this panel updates, so a new light position lands
    /// on the next frame's bake: a sub-frame offset, far below one frame of motion even at jump speed.
    /// </summary>
    void PlaceTorch()
    {
        float yaw = model.Rotation;

        // Character facing convention shared with Direction and Skill: forward = (-sin yaw, 0, -cos yaw).
        float forwardX = -MathF.Sin(yaw);
        float forwardZ = -MathF.Cos(yaw);

        // The anchor is the bounding-box center, so the feet plane sits half a height below PosY.
        float feetY = model.PosY - (model.Height ?? 0f) * 0.5f;

        torch.PosX = model.PosX + forwardX * TorchForward;
        torch.PosZ = model.PosZ + forwardZ * TorchForward;
        torch.PosY = feetY + TorchLift + TorchHeight * 0.5f;   // Anchor is the torch box center.
        torch.Rotation = yaw;   // Same yaw as the character, so the shaft turns with it instead of staying put.

        // Flame sits just above the tip of the shaft; the light emits from that same point.
        float flameY = feetY + TorchLift + TorchHeight + FlameSize * 0.15f;

        torchFlame.PosX = torch.PosX;
        torchFlame.PosY = flameY;
        torchFlame.PosZ = torch.PosZ;

        // Point lights are the ones that read Position; Direction is ignored by the shader for this kind.
        torchLight.Position = new Vector3(torch.PosX, flameY, torch.PosZ);
    }
}
