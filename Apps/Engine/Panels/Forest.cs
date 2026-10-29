// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

/// <summary>
/// Island grove panel: thirty-six trees in six rows by six columns on the open meadow on the side of the island
/// opposite the coastal rocks. Rocks owns the east shoreline, so the grove sits west of the road, clear of the
/// east-west sunrise and sunset corridor, the room, and the house.
///
/// Tree.glb is not handled like Rocks.glb. The rock asset is split into 15 variants and baked into
/// InstancedMesh3D templates through GLTFInstance.BakeMeshNode, whose index buffer is ushort and therefore
/// cannot address more than 65535 vertices. Tree.glb is a single 183k-vertex mesh, so the whole tree is drawn by
/// one InstancedModel instead: one shared template, thirty-six GPU instances, one instanced draw, and the asset keeps
/// its own material and textures through the standard Model loading path.
///
/// Sizing waits for the template bounds because the asset is not cube-shaped. A tree stands exactly twice as tall
/// as the Player, which pins its model to Height = 1 world meter, and Width/Depth are derived with the same
/// uniform scale so the natural aspect ratio is kept instead of being stretched into a cube. The instance anchor
/// is the template-box center, so PosY = TreeHeight / 2 rests the lower edge on the grass at y = 0.
///
/// Loading follows the BaseApp queue contract: AddControl queues the InstancedModel template, Load only marks the
/// panel ready, and Update then harvests the template bounds on the main thread, sizes every instance once,
/// and afterwards only forwards the per-frame update.
/// </summary>
// Explicit ILoadable declaration: App.Create queues the whole panel through RequestLoad.
// All other members come from Panel or BaseControl.
internal class Forest : Panel, ILoadable
{
    // -- Grove layout: six rows along Z by six columns along X. --
    const int Rows = 6;
    const int Columns = 6;

    // A tree stands exactly twice as tall as the Player. Player pins its model to Height = 1 world meter,
    // so the same value also serves as "one tree size" for the grid pitch below.
    const float TreeHeight = 2f;

    // Center-to-center spacing of neighbouring trees: two tree sizes, so the wider grove stays open between trunks
    // instead of closing into one wall of foliage.
    const float GridSpacing = TreeHeight * 2f;

    // Grove center on the west meadow. Rocks fill the east shoreline and the road runs along x = 0,
    // so the grove keeps clear of both and stays well inside the 150x150 grass tile, where x and z are in [-75, 75].
    // Six trees at GridSpacing span five pitches, so the grove covers x in [-60, -40] and z in [-30, -10]:
    // inside the grass tile and still clear of the west mountain line, whose nearest edge sits at x = -66.
    const float CenterX = -50f;
    const float CenterZ = -20f;

    const int Seed = 0x0F0E2026;   // Fixed seed so the yaw spread stays reproducible across launches.

    const string GlbPath = "Assets/Tree.glb";

    /// <summary>Shared tree template plus one instance per tree, created in the constructor and queued by AddControl.</summary>
    internal InstancedModel treeField;

    /// <summary>One-shot flags: picking and collision registration on the first frame, template-dependent sizing once the bounds arrive.</summary>
    bool registered;
    bool sized;

    internal Forest()
    {
        treeField = new InstancedModel()
        {
            Name = "Forest",
            ModelName = GlbPath,
            // CastShadows keeps the default true: these are near-field solid trees, not background silhouettes.
        };

        var rng = new Random(Seed);

        // The grid is centered on the meadow anchor, with two tree sizes between neighbouring centers.
        float firstX = CenterX - (Columns - 1) * GridSpacing * 0.5f;
        float firstZ = CenterZ - (Rows - 1) * GridSpacing * 0.5f;

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                treeField.Instances.Add(new MeshInstanceTransform()
                {
                    Name = $"tree{row * Columns + column + 1}",
                    PosX = firstX + column * GridSpacing,
                    PosZ = firstZ + row * GridSpacing,
                    // Yaw only turns a tree around the template-box center, so the grid itself stays aligned.
                    // PosY and the per-axis sizes depend on the template bounds and are settled in Update.
                    Rotation = Quaternion.CreateFromYawPitchRoll(rng.NextSingle() * MathF.Tau, 0f, 0f),
                });
            }
        }

        // AddControl last: it queues the template load, so the instance list is already complete when it starts.
        AddControl(treeField);
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Registration happens on the first frame because App.Create builds the picker and the collider after this
        // panel, so both are still null inside the constructor. One InstancedModel covers the whole grove, which keeps
        // per-instance picking and collision at single-tree granularity without registering one control per tree.
        if (!registered)
        {
            App.Instance.picker.InstancedTargets.Add(treeField);

            App.Instance.collider.InstancedObstacles.Add(treeField);

            registered = true;
        }

        // Template bounds arrive asynchronously with the InstancedModel load, so sizing retries until they exist.
        if (!sized)
            SettleTrees();

        if (treeField.Update(time))
            result = true;

        return result;
    }

    /// <summary>
    /// Queue-driven Load entry point. Tree.glb is a single shared template, so InstancedModel already carries the
    /// background work through its own queue entry: AddControl requested it when the field was created. The panel
    /// therefore has nothing to extract and only reports whether it survived the queue.
    /// </summary>
    public override Task<bool> Load() => Task.FromResult(!IsDisposed);

    /// <summary>
    /// One-shot instance sizing once the InstancedModel template bounds have arrived.
    /// Height becomes exactly twice the Player height, and Width/Depth follow the same uniform scale, so the tree
    /// keeps the aspect ratio measured from the asset instead of being stretched into a cube.
    /// The instance anchor is the template-box center, so half the tree height puts the lower edge on the grass at y = 0.
    /// </summary>
    void SettleTrees()
    {
        var local = treeField.TemplateLocalSize;
        if (local.Y <= 1e-6f)
            return;   // Template bounds not established yet; retry on the next frame.

        float scale = TreeHeight / local.Y;

        for (int i = 0; i < treeField.Instances.Count; i++)
        {
            var tree = treeField.Instances[i];

            tree.Width = local.X * scale;
            tree.Height = TreeHeight;
            tree.Depth = local.Z * scale;
            tree.PosY = TreeHeight * 0.5f;
        }

        sized = true;
    }
}
