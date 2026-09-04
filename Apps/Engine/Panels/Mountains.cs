// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Materials;   // KnownChannel
using SharpGLTF.Schema2;

namespace Engine.Panels;

/// <summary>
/// Data source for instanced mountain rendering. A Mountain is the instance object itself, inherited from
/// MeshInstanceTransform and referenced directly from an InstancedMesh3D.Instances collection. PosX, PosY,
/// PosZ, Width, Height, and Depth therefore keep the standard unified-placement meaning, with the instance
/// anchor sitting at the world-space position of the mountain template bounds center. Only Yaw is added here,
/// and Mountains.Update converts it to a Rotation quaternion every frame when needed.
/// </summary>
internal class Mountain : MeshInstanceTransform
{
    /// <summary>Yaw angle around the Y axis in radians. Mountains.Update converts it to a Rotation quaternion.</summary>
    internal float Yaw { get; set; }
}

/// <summary>
/// Background mountain-ring panel. It extracts four mountain meshes from background_mountains.glb at runtime,
/// bakes node-chain transforms, recenters them to the origin, and assigns one Surface to one InstancedMesh3D
/// for GPU instancing. Geometry extraction, baking, and in-memory texture handling all reuse the shared
/// GLTFInstance toolchain.
///
/// Each placed mountain chooses one of four variants with randomized yaw and width-height ratios to create
/// layered silhouettes. Diffuse and normal textures are decoded once into in-memory pixel sources and shared
/// across variants. The old specGloss texture path is intentionally ignored because its channel semantics do not
/// match the engine's metallic-roughness contract, so matte rock is approximated through MetallicFactor and RoughnessFactor.
///
/// Loading follows the BaseApp queue contract: AddPanel triggers RequestLoad, Load performs background extraction,
/// Update harvests the result on the main thread, and each child control then loads progressively. A clear corridor
/// is left along the east-west axis so the sun and moon can rise and set above open water when paired with the expanded Sea panel.
/// </summary>
// Explicit ILoadable declaration: this panel really does have background work, so it can be queued through RequestLoad.
// All other members come from Panel or BaseControl.
internal class Mountains : Panel, ILoadable
{
    // -- Ring-layout parameters. --
    const int Count = 80;             // Total mountain count before corridor filtering.
    const float RingRadius = 85f;     // Ring radius in meters.
    const float RadiusJitter = 20f;   // Radial jitter in meters for depth layering.
    const float BaseWidth = 50f;      // Base mountain width in meters.
    const float HeightAspect = 0.36f; // Height-to-width ratio measured from the source asset.
    const float Sink = 10f;           // Downward sink in meters so lower seams stay hidden below the default sea level.
    const int Seed = 2026;            // Fixed seed so layout stays reproducible.

    // East-west rise-and-set corridor. The sun rises at +X and sets at -X, with the moon following a related arc,
    // so the horizon must stay open along that axis. Corridor filtering uses |PosZ| as the transverse distance
    // from the east-west line. Even with arbitrary yaw, the mountain footprint's outer radius remains safely below 55m,
    // leaving enough margin while still letting SealOutside close the world boundary outside the ring.
    //const float CorridorClearance = 55f;

    // -- Heightfield parameters used as collision data for the player. --
    const float TerrainHalfExtent = 140f;   // Grid half-extent in meters, leaving margin beyond the ring's sealed boundary.
    const float TerrainCellSize = 0.5f;     // Grid cell size in meters.
    // Outer seal radius = ring radius + radial jitter + maximum mountain half-width, which is the farthest theoretical mountain edge.
    const float RingOuterRadius = RingRadius + RadiusJitter + BaseWidth * 1.2f * 0.5f;

    const string GlbPath = "Assets/background_mountains.glb";

    /// <summary>Flat view of all instances, with variant ownership defined by <c>groups</c>.</summary>
    //internal List<Mountain> mountains = new List<Mountain>();

    /// <summary><c>groups[i]</c> maps to <c>mountainFields[i]</c>: all instances that use the i-th mountain variant.</summary>
    List<Mountain>[] groups = new List<Mountain>[4] { new(), new(), new(), new() };

    /// <summary>Variant mountain template controls. Each mountain variant gets one InstancedMesh3D with one Surface, created only after extraction succeeds.</summary>
    internal InstancedMesh3D[] mountainFields;

    /// <summary>Queued Load result written by a background thread and harvested by Update on the main thread; null means not finished yet or already failed.</summary>
    volatile Extraction extraction;

    // Heightfield baking for collisions. Layout is static, so once all controls are ready it can be baked once in the background and then registered with the collider.
    internal MountainTerrain terrain;
    Task<MountainTerrain> terrainTask;
    bool terrainBuilt;
    bool terrainFailed;

    internal Mountains()
    {
        groups[0] = new List<Mountain>
        {
            new Mountain()
            {
                PosX = -75,
                PosY = 4,
                PosZ = 75,
                Width = 40,
                Height = 14,
                Depth = 35
            },
            new Mountain()
            {
                PosX = 40,
                PosY = 4,
                PosZ = 90,
                Width = 45,
                Height = 16,
                Depth = 40,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI / 2, 0f, 0f)
            },
            new Mountain()
            {
                PosX = -91,
                PosY = 6.5f,
                PosZ = -16,
                Width = 45,
                Height = 16,
                Depth = 39,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI / 2, 0f, 0f)
            },
            new Mountain()
            {
                PosX = -5,
                PosY = 7,
                PosZ = -88,
                Width = 45,
                Height = 15,
                Depth = 39
            }
        };

        groups[1] = new List<Mountain>
        {
            new Mountain()
            {
                PosX = -44,
                PosY = 6,
                PosZ = 85,
                Width = 40,
                Height = 14,
                Depth = 38
            },
            new Mountain()
            {
                PosX = 72,
                PosY = 6,
                PosZ = 86,
                Width = 45,
                Height = 16,
                Depth = 43,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI, 0f, 0f)
            },
            new Mountain()
            {
                PosX = -93,
                PosY = 6,
                PosZ = -55,
                Width = 45,
                Height = 15,
                Depth = 43,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI, 0f, 0f)
            },
            new Mountain()
            {
                PosX = 31,
                PosY = 7,
                PosZ = -91,
                Width = 45,
                Height = 16,
                Depth = 43
            }
        };

        groups[2] = new List<Mountain>
        {
            new Mountain()
            {
                PosX = -20,
                PosY = 5.5f,
                PosZ = 91,
                Width = 40,
                Height = 14,
                Depth = 37
            },
            new Mountain()
            {
                PosX = -90,
                PosY = 7,
                PosZ = 50,
                Width = 45,
                Height = 16,
                Depth = 42
            },
            new Mountain()
            {
                PosX = -76,
                PosY = 6,
                PosZ = -82,
                Width = 45,
                Height = 15,
                Depth = 42,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI, 0f, 0f)
            },
            new Mountain()
            {
                PosX = 65,
                PosY = 6,
                PosZ = -79,
                Width = 45,
                Height = 16,
                Depth = 42
            }
        };

        groups[3] = new List<Mountain>
        {
            new Mountain()
            {
                PosX = 14,
                PosY = 6,
                PosZ = 92,
                Width = 40,
                Height = 14,
                Depth = 39,
                Rotation = Quaternion.CreateFromYawPitchRoll((float)Math.PI / 2, 0f, 0f)
            },
            new Mountain()
            {
                PosX = -89,
                PosY = 7,
                PosZ = 19,
                Width = 45,
                Height = 16,
                Depth = 44
            },
            new Mountain()
            {
                PosX = -38,
                PosY = 7,
                PosZ = -86,
                Width = 45,
                Height = 16,
                Depth = 44
            }
        };

        //var rng = new Random(Seed);

        //for (int i = 0; i < Count; i++)
        //{
        //    // Base angles are evenly distributed with jitter so mountains do not look neatly queued, and width and height randomize independently for layering.
        //    float angle = MathF.Tau * i / Count + (rng.NextSingle() - 0.5f) * 0.4f;
        //    float radius = RingRadius + (rng.NextSingle() * 2f - 1f) * RadiusJitter;
        //    float width = BaseWidth * (0.85f + rng.NextSingle() * 0.35f);
        //    float height = width * HeightAspect * (0.85f + rng.NextSingle() * 0.4f);
        //    float yaw = rng.NextSingle() * MathF.Tau;
        //    int variant = rng.Next(4);

        //    // Filter out the east-west rise-and-set corridor only after drawing all random values,
        //    // keeping later random sequences identical to the no-corridor layout for the same seed.
        //    float posZ = MathF.Sin(angle) * radius;
        //    if (MathF.Abs(posZ) < CorridorClearance)
        //        continue;

        //    var mountain = new Mountain()
        //    {
        //        PosX = MathF.Cos(angle) * radius,
        //        PosY = height * 0.5f - Sink,   // The anchor is the box center, so the lower edge sinks by Sink while the center stays at half height.
        //        PosZ = posZ,
        //        Width = width,
        //        Height = height,
        //        Depth = width,                 // The asset's width and depth are nearly equal, so use uniform width.
        //        Yaw = yaw,
        //    };

        //    mountains.Add(mountain);
        //    groups[variant].Add(mountain); // Randomly assign one of the four variants.
        //}
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        // Extraction is driven by the load queue. No controls exist before it finishes,
        // and after a failure extraction stays null because Load returns false and is not retried.
        if (extraction == null)
            return result;

        // Harvest on the main thread by building variant controls and calling AddControl for each child.
        // This intentionally stays out of Load because the native Load path runs on a background thread,
        // and mutating Panel.Controls or picker.InstancedTargets there would race main-thread draw and picking traversals.
        if (mountainFields == null)
        {
            if (IsDisposed)
                return result;

            Build(extraction);
        }

        //ApplyYaw();

        for (int i = 0; i < mountainFields.Length; i++)
        {
            //SyncInstances(mountainFields[i], groups[i]);
            if (mountainFields[i].Update(time))
            {
                result = true;
            }
        }

        //if (!terrainBuilt)
        //{
        //    if (mountainFields.Length == 0)
        //        terrainBuilt = true;   // No variants in the asset, so there is nothing to bake.
        //    else
        //        UpdateTerrain();
        //}

        return result;
    }

    /// <summary>
    /// Queue-driven Load entry point. AddPanel triggers RequestLoad, then Load extracts the glb into four variant geometries
    /// plus shared textures on a background thread. Returning true lets BaseApp mark the panel ready, while control creation
    /// itself is still harvested later on the main thread from Update.
    /// </summary>
    public override async Task<bool> Load()
    {
        try
        {
            extraction = await Task.Run(ExtractAsync);   // Pure CPU parsing, transform baking, and texture decoding.
        }
        catch (Exception ex)
        {
            App.Instance.AddLog(LogType.Error, $"Mountains ExtractAsync failed: {ex.GetBaseException()}");
            return false;
        }

        return !IsDisposed;
    }

    // -- Heightfield path: once all variant controls are ready and their template bounds are finalized,
    //    snapshot matrices on the main thread, rasterize in the background, and register the result with PlayerCollider. --
    //void UpdateTerrain()
    //{
    //    terrainTask ??= TryStartTerrainBake();

    //    if (terrainTask == null || !terrainTask.IsCompleted)
    //        return;

    //    if (!terrainTask.IsCompletedSuccessfully)
    //    {
    //        if (!terrainFailed)
    //        {
    //            terrainFailed = true;
    //            App.Instance.AddLog(LogType.Error, $"Mountains Terrain bake failed: {terrainTask.Exception?.GetBaseException()}");
    //        }
    //        return;
    //    }

    //    terrain = terrainTask.Result;
    //    App.Instance.collider.Terrain = terrain;
    //    terrainBuilt = true;
    //}

    /// <summary>Starts only after all variant controls are ready: snapshot geometry and per-instance matrices on the main thread, where BuildInstanceMatrix is the engine's single matrix-authority path, then rasterize in the background. Return null if anything is not ready yet so the next frame can retry.</summary>
    //Task<MountainTerrain> TryStartTerrainBake()
    //{
    //    var snapshot = new List<(Vertex[] Vertices, ushort[] Indices, Matrix4x4[] Matrices)>(mountainFields.Length);

    //    for (int i = 0; i < mountainFields.Length; i++)
    //    {
    //        var field = mountainFields[i];

    //        if (!field.Ready)
    //            return null;   // TemplateLocalBoundsRaw is only filled during loading, so matrices are not usable before Ready.

    //        // Bake only enabled instances, matching the same rule used by PlayerCollider.CollectBoxes.
    //        var matrices = new List<Matrix4x4>(field.Instances.Count);
    //        for (int j = 0; j < field.Instances.Count; j++)
    //        {
    //            if (!field.Instances[j].Enable)
    //                continue;

    //            matrices.Add(field.BuildInstanceMatrix(field.Instances[j]));
    //        }

    //        var surface = field.Surfaces[0];
    //        snapshot.Add((surface.Vertices, surface.Indices, matrices.ToArray()));
    //    }

    //    return Task.Run(() => BakeTerrain(snapshot));
    //}

    ///// <summary>Background bake: transform template vertices into world space per instance, rasterize triangles into the heightfield, then seal the world boundary outside the ring radius. The instance layout is static, so the grid does not change afterward.</summary>
    //static MountainTerrain BakeTerrain(List<(Vertex[] Vertices, ushort[] Indices, Matrix4x4[] Matrices)> snapshot)
    //{
    //    var terrain = new MountainTerrain(TerrainHalfExtent, TerrainCellSize);

    //    for (int v = 0; v < snapshot.Count; v++)
    //    {
    //        var (vertices, indices, matrices) = snapshot[v];
    //        var world = new Vector3[vertices.Length];

    //        for (int m = 0; m < matrices.Length; m++)
    //        {
    //            var matrix = matrices[m];

    //            for (int i = 0; i < vertices.Length; i++)
    //                world[i] = Vector3.Transform(vertices[i].Position, matrix);

    //            for (int t = 0; t + 2 < indices.Length; t += 3)
    //                terrain.RasterizeTriangle(world[indices[t]], world[indices[t + 1]], world[indices[t + 2]]);
    //        }
    //    }

    //    terrain.SealOutside(0f, 0f, RingOuterRadius);

    //    return terrain;
    //}

    /// <summary>Builds N InstancedMesh3D controls on the main thread, one per variant, each with one Surface using centered vertices and shared texture pixel sources.</summary>
    void Build(Extraction extraction)
    {
        int variantCount = Math.Min(groups.Length, extraction.Geometries.Length);
        if (variantCount == 0)
        {
            mountainFields = Array.Empty<InstancedMesh3D>();   // Prevent Update from harvesting the same empty result repeatedly.
            return;
        }

        // If the asset exposes fewer variants than expected, merge the extra groups into the last available variant.
        for (int i = variantCount; i < groups.Length; i++)
        {
            groups[variantCount - 1].AddRange(groups[i]);
            groups[i].Clear();
        }

        mountainFields = new InstancedMesh3D[variantCount];

        for (int i = 0; i < variantCount; i++)
        {
            var geometry = extraction.Geometries[i];

            var field = new InstancedMesh3D()
            {
                Name = $"mountain{i + 1}",
                CastShadows = false,   // Background mountains stay out of shadow maps and GI proxies to avoid SDF pollution.
            };

            field.Surfaces.Add(new Surface()
            {
                Vertices = geometry.Vertices,
                Indices = geometry.Indices,
                TextureOverride = extraction.Diffuse,      // All four variants share the same in-memory pixel source, while each variant still registers its own composed texture name.
                NormalTextureOverride = extraction.Normal,
                MetallicFactor = 0f,          // Approximate the old specGloss material as matte rock.
                RoughnessFactor = 0.95f,
                Unlit = false,                // Use PBR lighting.
            });

            for (int j = 0; j < groups[i].Count; j++)
                field.Instances.Add(groups[i][j]);

            AddControl(field);
            mountainFields[i] = field;

            field.Highlight = new Highlight { Style = HighlightStyle.Wireframe };

            App.Instance.picker.InstancedTargets.Add(field);

            App.Instance.collider.InstancedObstacles.Add(field);
        }
    }

    ///// <summary>Converts Yaw to a Rotation quaternion around the template-bounds center.</summary>
    //void ApplyYaw()
    //{
    //    for (int i = 0; i < mountains.Count; i++)
    //        mountains[i].Rotation = Quaternion.CreateFromYawPitchRoll(mountains[i].Yaw, 0f, 0f);
    //}

    /// <summary>
    /// Keeps the instance list one-to-one and in order with the corresponding group, matching the pattern used in Robots.cs:
    /// first align counts by adding or removing at the tail, then reorder entries by reference comparison.
    /// </summary>
    //static void SyncInstances(InstancedMesh3D field, List<Mountain> group)
    //{
    //    var instances = field.Instances;

    //    while (instances.Count < group.Count)
    //        instances.Add(group[instances.Count]);

    //    while (instances.Count > group.Count)
    //        instances.RemoveAt(instances.Count - 1);

    //    for (int i = 0; i < group.Count; i++)
    //    {
    //        if (!ReferenceEquals(instances[i], group[i]))
    //            instances[i] = group[i];
    //    }
    //}

    // -- Background extraction: glb -> four variant geometries plus shared textures. --

    /// <summary>Extraction result: four centered variant geometries plus in-memory pixel caches for shared textures.</summary>
    class Extraction
    {
        internal (Vertex[] Vertices, ushort[] Indices)[] Geometries;
        internal TextureUpdateSource Diffuse;
        internal TextureUpdateSource Normal;
    }

    static async Task<Extraction> ExtractAsync()
    {
        var model = await GLTFInstance.LoadGlbAsync(GlbPath);

        var extraction = new Extraction();

        // 1) Shared textures: decode Diffuse and Normal once into in-memory pixel caches and share them across all four variants.
        //    The old specGloss path is intentionally dropped because its channel semantics do not match the engine's MR contract.
        await ExtractSharedTextures(model, extraction);

        // 2) Geometry: each mesh-bearing node is one mountain variant, sorted by name to keep ordering stable.
        var meshNodes = GLTFInstance.GetMeshNodes(model)
            .OrderBy(n => n.Name)
            .ToList();

        var geometries = new (Vertex[] Vertices, ushort[] Indices)[meshNodes.Count];

        for (int i = 0; i < meshNodes.Count; i++)
            geometries[i] = GLTFInstance.BakeMeshNode(meshNodes[i]);

        extraction.Geometries = geometries;

        return extraction;
    }

    /// <summary>Finds the shared Diffuse and Normal images from the material and decodes them into in-memory pixel caches for direct GPU upload.</summary>
    static async Task ExtractSharedTextures(ModelRoot model, Extraction extraction)
    {
        var gltfMaterial = model.LogicalMaterials.FirstOrDefault();

        // This asset uses KHR_materials_pbrSpecularGlossiness, so its base color lives in the Diffuse channel.
        var diffuseImage = GLTFInstance.FindChannelImage(gltfMaterial, KnownChannel.Diffuse);
        var normalImage = GLTFInstance.FindChannelImage(gltfMaterial, KnownChannel.Normal);

        extraction.Diffuse = await GLTFInstance.ExtractEmbeddedImageAsync(diffuseImage);
        extraction.Normal = await GLTFInstance.ExtractEmbeddedImageAsync(normalImage);
    }
}

/// <summary>
/// Heightfield for the mountain ring. It is a regular XZ grid where each cell stores the ground Y value in row-major order.
/// During baking, every mountain instance triangle is transformed by its instance matrix and rasterized into the grid with
/// scanlines over Z. Each covered cell stores the maximum height written by overlapping triangles, so rotations, concave
/// silhouettes, and intersecting instances are handled naturally. Cells outside the outer ring radius are filled with
/// WallHeight, making the mountain ring the effective scene boundary.
///
/// Mountains bakes this once from the static layout and PlayerCollider.TryMove consumes it. Today a footprint is blocked
/// when the sampled height rises too far above the current floor baseline. In the future, climbing mountain paths could be
/// supported by changing that blocking rule to follow the sampled height plus a slope threshold, while reusing the same bake
/// pipeline and query interface.
/// </summary>
internal sealed class MountainTerrain
{
    /// <summary>Sentinel height for the world boundary. Cells outside the ring are written to this value so every threshold treats them as blocked.</summary>
    internal const float WallHeight = 10000f;

    readonly float minX, minZ;
    readonly float cellSize;
    readonly int size;        // Cell count per side; each cell center is (minX + i*cell, minZ + j*cell).
    readonly float[] heights; // Ground height per cell; points outside the region are treated as flat ground at 0.

    /// <summary>Builds a grid centered at the origin with halfExtent as the half-width, including both boundary cell centers.</summary>
    internal MountainTerrain(float halfExtent, float cell)
    {
        minX = -halfExtent;
        minZ = -halfExtent;
        cellSize = cell;
        size = (int)MathF.Round(halfExtent * 2f / cell) + 1;
        heights = new float[size * size];
    }

    /// <summary>Returns the height at one grid sample. Points outside the covered area return 0, meaning flat unblocked ground.</summary>
    internal float HeightAt(float x, float z)
    {
        int i = (int)MathF.Round((x - minX) / cellSize);
        int j = (int)MathF.Round((z - minZ) / cellSize);

        if ((uint)i >= (uint)size || (uint)j >= (uint)size)
            return 0f;

        return heights[j * size + i];
    }

    /// <summary>Returns the maximum cell height covered by a footprint defined by its center and half-size. If the footprint lies fully outside the region, return 0.</summary>
    internal float FootprintMaxHeight(in Vector3 center, in Vector3 half)
    {
        int i0 = CellLow(center.X - half.X);
        int i1 = CellHigh(center.X + half.X);
        int j0 = CellLow(center.Z - half.Z);
        int j1 = CellHigh(center.Z + half.Z);

        if (i0 > i1 || j0 > j1)
            return 0f;

        float max = 0f;
        for (int j = j0; j <= j1; j++)
        {
            int row = j * size;
            for (int i = i0; i <= i1; i++)
            {
                float h = heights[row + i];
                if (h > max)
                    max = h;
            }
        }

        return max;
    }

    /// <summary>
    /// Rasterizes one world-space triangle by scanning rows over Z, finding the X interval where the triangle crosses each row,
    /// then linearly interpolating height across the row and writing the maximum. Shared-edge triangles can write the same cell
    /// repeatedly; the max operation makes that converge without conflict.
    /// </summary>
    internal void RasterizeTriangle(in Vector3 pa, in Vector3 pb, in Vector3 pc)
    {
        // Sort by ascending Z so p0.Z <= p1.Z <= p2.Z.
        Vector3 p0 = pa, p1 = pb, p2 = pc;
        if (p1.Z < p0.Z) (p0, p1) = (p1, p0);
        if (p2.Z < p1.Z) (p1, p2) = (p2, p1);
        if (p1.Z < p0.Z) (p0, p1) = (p1, p0);

        if (p2.Z - p0.Z < 1e-6f)
            return; // Degenerate in XZ projection, so it contributes zero area.

        int j0 = Math.Max(CellLow(p0.Z), 0);
        int j1 = Math.Min(CellHigh(p2.Z), size - 1);
        if (j0 > j1)
            return;

        for (int j = j0; j <= j1; j++)
        {
            float zc = minZ + j * cellSize;

            // The long edge p0->p2 always intersects the row. The short edge depends on which Z segment zc falls into.
            EdgeIntersect(p0, p2, zc, out float xA, out float yA);

            float xB, yB;
            if (zc < p1.Z)
                EdgeIntersect(p0, p1, zc, out xB, out yB);
            else
                EdgeIntersect(p1, p2, zc, out xB, out yB);

            float xLo, xHi, yLo, yHi;
            if (xA <= xB) { xLo = xA; xHi = xB; yLo = yA; yHi = yB; }
            else { xLo = xB; xHi = xA; yLo = yB; yHi = yA; }

            int i0 = Math.Max(CellLow(xLo), 0);
            int i1 = Math.Min(CellHigh(xHi), size - 1);
            if (i0 > i1)
                continue;

            float xRange = xHi - xLo;
            int row = j * size;

            for (int i = i0; i <= i1; i++)
            {
                // At fixed Z, Y varies linearly with X across the triangle plane, so interpolate between the two row-edge hits.
                float xc = minX + i * cellSize;
                float h = xRange < 1e-6f
                    ? (yA + yB) * 0.5f
                    : yLo + (yHi - yLo) * ((xc - xLo) / xRange);

                int idx = row + i;
                if (h > heights[idx])
                    heights[idx] = h;
            }
        }
    }

    /// <summary>Seals the outside of the ring by writing world-boundary heights into every cell farther than outerRadius from (cx,cz).</summary>
    internal void SealOutside(float cx, float cz, float outerRadius)
    {
        float r2 = outerRadius * outerRadius;

        for (int j = 0; j < size; j++)
        {
            float dz = minZ + j * cellSize - cz;
            int row = j * size;

            for (int i = 0; i < size; i++)
            {
                float dx = minX + i * cellSize - cx;
                if (dx * dx + dz * dz > r2)
                    heights[row + i] = WallHeight;
            }
        }
    }

    // -- Coordinate to grid-index helpers. --

    /// <summary>Lower grid index for a covered interval: the first cell whose center is greater than or equal to coord, with a tiny tolerance to avoid missing shared-edge cells.</summary>
    int CellLow(float coord) => (int)MathF.Ceiling((coord - minX) / cellSize - 1e-4f);

    /// <summary>Upper grid index for a covered interval: the last cell whose center is less than or equal to coord, with a tiny tolerance to avoid missing shared-edge cells.</summary>
    int CellHigh(float coord) => (int)MathF.Floor((coord - minX) / cellSize + 1e-4f);

    /// <summary>Intersects edge p->q with the horizontal line at z, returning X and the height Y interpolated along the edge. If the edge is degenerate in Z, return the endpoint.</summary>
    static void EdgeIntersect(in Vector3 p, in Vector3 q, float z, out float x, out float y)
    {
        float dz = q.Z - p.Z;

        if (MathF.Abs(dz) < 1e-9f)
        {
            x = p.X;
            y = p.Y;
            return;
        }

        float t = (z - p.Z) / dz;
        x = p.X + (q.X - p.X) * t;
        y = p.Y + (q.Y - p.Y) * t;
    }
}
