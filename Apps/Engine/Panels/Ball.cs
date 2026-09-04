// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Ball : Panel
{
    internal Season.Controls.Model model;

    // Size and anchor are unknown before bounds are established, so default placement is
    // deferred until LocalSize becomes available and applied only once.
    // Non-zero properties explicitly set in the constructor are preserved.
    bool ballSettled;

    internal Season.Controls.Model bee;

    readonly float beeSpeed = 1.00f;

    internal Ball()
    {
        model = new Season.Controls.Model()
        {
            Name = @"Assets/Sun.glb",
            PosX = 10,
            PosY = 0.5f,
            PosZ = 6,
            Width = 1,
            Height = 1,
            Depth = 1,
            Rotation = 0
        };
        AddControl(model);

        bee = new Season.Controls.Model()
        {
            Name = @"Assets/MorphStressTest.glb",
            // Full morph-target asset: shell deltas are expanded by shell vertex layout and
            // weights stay synchronized with the source, so the wireframe shell tracks morph animation every frame.
            Highlight = new Highlight { Style = HighlightStyle.Wireframe },
            PosX = -5,
            PosY = 0.3f,
            PosZ = 2,
            Width = 2,
            Height = 0.3f,
            Depth = 0.5f,
            Rotation = MathF.PI / 2f
        };
        AddControl(bee);
    }

    void SwitchBeeInstancesToNextAnimation()
    {
        bee?.SwitchToNextAnimation();
    }

    internal void SetBeeInstancesModel(string modelName, bool forceReload)
    {
        bee?.SetModel(modelName, forceReload: forceReload);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Ball: only when position is still all zeros do we pin the local origin to (0,0,3).
        // Size keeps the engine's default normalized settling.
        if (!ballSettled && model.LocalSize != Vector3.Zero)
        {
            if (model.PosX == 0 && model.PosY == 0 && model.PosZ == 0)
            {
                var pos = new Vector3(0f, 0f, 3f) + model.AnchorWorldOffset;
                model.PosX = pos.X;
                model.PosY = pos.Y;
                model.PosZ = pos.Z;
            }
            ballSettled = true;
        }

        if (model.Update(time))
        {
            result = true;
        }

        if (bee.Update(time: time * beeSpeed, alpha: 1f))
        {
            result = true;
        }

        return result;
    }
}
