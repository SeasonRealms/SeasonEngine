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

    internal Season.Controls.Model model;

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

        if (model.Ready && modelFirst)
        {
            model.PlayAnimation("Idle-loop");
            modelFirst = false;
        }

        if (model.Update(time))
        {
            result = true;
        }

        return result;
    }
}
