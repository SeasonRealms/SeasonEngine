// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Windmill cottage standing at the foot of the southern range, between the bridge and the south branch:
/// the player spawns at (0, 0) looking down -Z, the branch runs due south along x = 0 and the bridge span
/// centres on (-32, -52.9). The house sits at (-20, -68) on the meadow west of the branch and south of
/// the main channel (which passes z = -49.7 at this x), closer to the range than the house was
/// originally: its 5 m depth reaches z = -70.5, just past the point where the branch itself sinks into
/// the hillside at z = -69.5, so the rear of the plot meets the rising ground.
///
/// <para>The visible model is an AI-processed Sketchfab GLB whose node chain bakes a 0.01 unit scale, so
/// its rest-pose model-space extents are only about 0.196 x 0.169 x 0.109 meters (measured through node
/// world matrices, the same source the engine's ComputeRestBounds uses). The constructor writes explicit
/// non-zero Width/Height/Depth of 10 x 10 x 5 m -- about 51x, 59x and 46x those extents, deliberately
/// squarer than the natural 1.8:1 footprint -- which the size settling honours as authored defaults, and
/// the property panel can resize the house from there exactly like <see cref="House"/>.</para>
///
/// <para><b>The clip.</b> The asset carries one animation ("Take 001", 15 s) that turns the windmill's two
/// blade nodes; the engine's animation player loops by default, so playing the first
/// clip once the queued load makes the model Ready keeps the sails turning forever. The play request is
/// retried every frame until it lands because <c>PlayAnimation</c> silently no-ops while the asset has not
/// arrived, the same contract the player's idle loop follows in <see cref="Player"/>. The clip name is
/// cached after the first query so the pending retry costs a copy of one string, not a fresh
/// <c>GetAnimationNames</c> allocation every frame.</para>
///
/// <para><b>The asset is the merged variant.</b> The pristine Sketchfab export
/// (<c>little_house_game_ready.glb</c>, no longer shipped) is a DCC export that never merged geometry:
/// 1041 nodes, 466 meshes holding exactly one primitive each, 8,498 triangles in total (about 18 per
/// primitive), against 1-39 primitives for every other prop here and 1 for the haunted
/// <see cref="House"/> after it was merged the same way. Its material is <c>alphaMode: BLEND</c> with <c>doubleSided</c>, so the renderer
/// puts it in the transparent bucket and draws each primitive twice (front-cull, then back-cull): that
/// file costs 932 submissions plus a 466-entry per-frame depth sort. <c>little_house_game_ready_merged.glb</c>
/// bakes the 464 static meshes' node world matrices into their positions/normals/tangents and merges them
/// into a single primitive, keeping only the node chain and the two blade meshes the clip drives:
/// 17 nodes, 3 primitives (one baked body plus two blades) = 6 draws under the same two-sided rule. The
/// rest-pose bounds match the original to 3e-9, so the authored size below is unchanged, and the material,
/// its four textures, and both animation samplers are byte-identical copies. The one behavioural delta is
/// that the transparent body is no longer depth-sorted part by part (one primitive cannot be reordered
/// internally), which can only show up on the ~3% of base-colour texels that are partially transparent.</para>
///
/// <para>Registered by <see cref="App"/> both as a picker target (click to select, then edit Pos/Width/
/// Height/Depth/Rotation in the property panel) and as a collider obstacle (the player cannot walk in).</para>
/// </summary>
internal class LittleHouse : Panel
{
    /// <summary>The cottage GLB, carried in the Raw asset set. This is the merged variant baked offline
    /// from the pristine Sketchfab export (little_house_game_ready.glb); see the class header for what
    /// the merge preserves. The pristine file is no longer shipped -- its 466 primitives were the whole
    /// reason for the merge -- so there is deliberately no source-path constant pointing at a missing
    /// asset. Re-bake from a fresh export of the same name if the merge ever has to be redone.</summary>
    internal const string LittleHouseGlbPath = @"Assets/little_house_game_ready_merged.glb";

    // -- Placement, in world meters on the flat meadow (grass is y = 0 here). --

    const float CenterX = -20f;
    const float CenterZ = -68f;

    // Authored size, set from the property panel: 10 x 10 x 5 m, i.e. about 51x, 59x and 46x the
    // natural extents above. The footprint is therefore squarer than the model's own 1.8:1. The anchor
    // is the bounding-box center, so PosY is half the height -- 5 m, which puts the bbox bottom on the
    // grass at y = 0. The merged asset keeps the original rest bounds to 3e-9, so these values map onto
    // it unchanged.
    const float ModelWidth = 10f;
    const float ModelHeight = 10f;
    const float ModelDepth = 5f;

    internal Season.Controls.Model model;

    // Clip handshake: the clip list is read exactly once when the asset arrives, then the play request
    // is retried until the switch actually lands, not merely until Update first runs.
    string? clipName;
    bool clipResolved;
    bool clipStarted;

    internal LittleHouse()
    {
        model = new Season.Controls.Model()
        {
            Name = LittleHouseGlbPath,
            // Wireframe like House and Player, which is only affordable because the merged asset submits
            // one body primitive: the channel builds one shell per primitive, so this is 3 shells here.
            // Pointing this style at a many-primitive asset (the pristine export is 466) builds a shell
            // per primitive on the first hover -- thousands of GPU buffers in one frame -- and then
            // re-writes and re-draws all of them every highlighted frame. Keep the asset merged, or
            // switch this model back to HighlightStyle.Bounds.
            Highlight =
            {
                Style = HighlightStyle.Wireframe
            },
            PosX = CenterX,
            PosY = ModelHeight * 0.5f,
            PosZ = CenterZ,
            Width = ModelWidth,
            Height = ModelHeight,
            Depth = ModelDepth,
            // Facing the spawn side (+Z) puts the mill's front toward the player's first look down -Z.
            // Rotation is in radians, so MathF.PI is the 180 degrees the panel shows.
            Rotation = MathF.PI
        };
        AddControl(model);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Start the windmill loop when the asset has arrived. The clip is picked by index rather than
        // hard-coded name, matching Boat: the exporter's "Take 001" is not a stable contract.
        // The list is read once and the name cached, because GetAnimationNames allocates and this retry
        // can span several frames while the queued load is still resolving. An asset with no clip also
        // resolves here, so the query never repeats on every frame.
        if (!clipResolved && model.Ready)
        {
            var names = model.GetAnimationNames();
            clipName = names.Count > 0 ? names[0] : null;
            clipResolved = true;
        }

        if (!clipStarted && clipName != null)
            clipStarted = model.PlayAnimation(clipName) != null;

        if (model.Update(time))
        {
            result = true;
        }

        return result;
    }
}
