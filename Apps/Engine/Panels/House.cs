// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class House : Panel
{
    // 2026-08: moved outside the room door. The door faces +X, so the house is placed at X=24
    // and spaced evenly along Z=7.5 together with CubeField, Billboard, and Sphere.
    float OffsetX = 24f;
    float OffsetZ = 7.5f;

    internal Season.Controls.Model model;

    internal House()
    {
        model = new Season.Controls.Model()
        {
            Name = @"Assets/halloween_haunted_house.glb",
            Highlight = 
            { 
                Style = HighlightStyle.Wireframe
            },
            PosX = 40,
            PosY = 4,
            PosZ = 35,
            Width = 25,
            Height = 25,
            Depth = 25,
            // Model.Rotation is measured in radians around Y. The old value 90 was treated as
            // 90 radians, which normalizes to about 116.6 degrees, so the ObjectPicker reading
            // no longer matched the intended 90-degree turn. Convert degrees to radians instead.
            Rotation = MathF.PI / 2f
        };
        AddControl(model);
    }

    // Explicit non-zero constructor defaults take priority. Size is assigned only when still zero,
    // using normalized 2x settling. Position is assigned only when still all zeros, then converted
    // through AnchorWorldOffset, mirroring the engine's OnBoundsEstablished == 0 convention.
    // This settling is one-shot and must not be written back every frame, or ObjectPicker edits
    // would snap back on the next update.
    bool houseSized;

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        if (!houseSized && model.LocalSize != Vector3.Zero)
        {
            if (model.Width == 0) model.Width = model.LocalSize.X * model.OriginalScale * 2f;
            if (model.Height == 0) model.Height = model.LocalSize.Y * model.OriginalScale * 2f;
            if (model.Depth == 0) model.Depth = model.LocalSize.Z * model.OriginalScale * 2f;

            if (model.PosX == 0 && model.PosY == 0 && model.PosZ == 0)
            {
                // Unified placement convention: pin the model's local origin to
                // (OffsetX, 1, OffsetZ) through AnchorWorldOffset. ComputedScale is derived
                // from Width, Height, and Depth in real time, so reading it after size settles is correct.
                var pos = new System.Numerics.Vector3(OffsetX, 1f, OffsetZ) + model.AnchorWorldOffset;
                model.PosX = pos.X;
                model.PosY = pos.Y;
                model.PosZ = pos.Z;
            }

            houseSized = true;
        }
        //model?.Rotation = App.Instance.Time;
        //float houseAlpha = 0f;
        if (model.LoadComplete.HasValue)
        {
            //float elapsed = (float)(DateTime.UtcNow - model.LoadComplete.Value).TotalSeconds;
            //houseAlpha = Math.Min(elapsed / 3.0f, 3.0f);  // Fade in over 1 second.
        }
        
        if (model.Update(time))
        {
            result = true;
        }

        return result;
    }
}
