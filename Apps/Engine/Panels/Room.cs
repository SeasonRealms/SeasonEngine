// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Room : Panel
{
    // Indoor six-sided box used as a CSM test scene. Each wall part is a unit-box Mesh3D plus a room-local
    // layout range in roomPartMins and roomPartMaxs, and Update recomputes the world transform from panel parameters every frame.
    internal Mesh3D[] roomParts;

    // Room-local layout ranges for each wall box, one-to-one with roomParts.
    Vector3[] roomPartMins, roomPartMaxs;

    Season.Controls.Model light1, light2, light3;

    Season.Rendering.LightSource lightSource1, lightSource2, lightSource3;

    const float LightIntensity = 80f;

    // One-time size guard for the ceiling-light marker after bounds become available.
    bool lightSized;

    // Orientation convention: north = +Z, south = -Z, east = +X, west = -X.
    // Room placement is fully parameterized by PosX/PosY/PosZ and Width/Height/Depth instead of hard-coded offsets.
    // All interior layout is described in room-local coordinates and mapped to world space through RoomPointToWorld.
    static readonly Vector3 RoomLocalSize = new Vector3(16f, 10.4f, 16f);

    // Triangular roof in room-local coordinates. It is a two-slope roof with the ridge running along Z,
    // 4m of rise above the wall top, and 0.8m of overhang on all sides. The mesh is an outward-facing shell,
    // with the ceiling slab already closing the room from below.
    const float RoofBaseY = 10.4f;   // Equals RoomLocalSize.Y, so the roof base sits at the wall top.
    const float RoofRise = 4f;       // Peak rise, placing the ridge at y = 14.4.
    const float RoofOverhang = 0.8f; // Local-space eave overhang, scaled with the panel parameters.

    // Front-door stairs in room-local coordinates. The room floor slab raises the threshold above the ground,
    // so three white steps connect the south door opening back down to the road. The stair width matches the door width
    // and the whole structure scales and translates with the room.

    // Top face of the interior ceiling in room-local Y, shared by wall layout and automatic lamp placement.
    const float InteriorCeilingY = 9.2f;

    internal Season.Controls.Model bottle;

    internal Season.Controls.Model busterDrone;

    static readonly Vector3 LampLightColor1 = Season.Basic.Colors.Red.ToVector3() * 0.7f; // new Vector3(1f, 0.1f, 0.1f);

    static readonly Vector3 LampLightColor2 = Season.Basic.Colors.Yellow.ToVector3() * 0.7f; // new Vector3(0.1f, 1f, 0.1f);

    static readonly Vector3 LampLightColor3 = Season.Basic.Colors.Blue.ToVector3() * 0.7f; // new Vector3(0.1f, 0.1f, 1f);

    /// <summary>
    /// Ceiling-light position, computed automatically from the room-local ceiling point (0, InteriorCeilingY, 0)
    /// and mapped through <see cref="RoomPointToWorld"/> so it follows runtime room translation and scaling.
    /// </summary>
    Vector3 LampPosition => RoomPointToWorld(new Vector3(0f, InteriorCeilingY, 0f));

    internal Room()
    {
        // Six-sided room test scene for CSM. The room encloses the existing 3D objects, places the door on the south wall,
        // and keeps all placement and size driven by the panel transform through RoomPointToWorld.
        // The sun arc tilts toward the south, so noon light enters through the door and projects a rectangular patch on the floor.
        // Splitting the room into separate wall parts, a roof, and stair pieces preserves the hollow-room meaning for GI proxies.
        var roomLayout = BuildRoomParts();
        roomParts = new Mesh3D[roomLayout.Length];
        roomPartMins = new Vector3[roomLayout.Length];
        roomPartMaxs = new Vector3[roomLayout.Length];
        for (int i = 0; i < roomLayout.Length; i++)
        {
            roomParts[i] = roomLayout[i].Mesh;
            roomPartMins[i] = roomLayout[i].Min;
            roomPartMaxs[i] = roomLayout[i].Max;
        }

        // DDGI and SDF diffuse-lighting test: set GiAlbedo explicitly rather than inferring it from PBR materials,
        // matching the visual colors from BuildRoomParts so white walls bounce more light than the lighter gray floor and ceiling.
        foreach (var part in roomParts)
        {
            part.GiAlbedo = part.Name switch
            {
                "RoomFloor" => new Vector3(0.7f, 0.7f, 0.7f),
                "RoomCeiling" => new Vector3(0.7f, 0.7f, 0.7f),
                "RoomRoof" => new Vector3(0.3f, 0.3f, 0.3f),   // Dark roof albedo to match the visible roof color.
                _ => new Vector3(0.85f, 0.85f, 0.85f),
            };
            AddControl(part);
        }

        var pos = new Vector3(0f, 5.5f, 54f);

        var unit = 1f;

        light1 = new Season.Controls.Model()
        {
            Name = @"Assets/celling_lights.glb",
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(LampLightColor1.X, LampLightColor1.Y, LampLightColor1.Z, 1f),
            CastShadows = false,
            PosX = pos.X - unit,
            PosY = pos.Y,
            PosZ = pos.Z + unit,
            Width = 0.5f,
            Height = 1f,
            Depth = 0.5f
        };
        AddControl(light1);

        light2 = new Season.Controls.Model()
        {
            Name = @"Assets/celling_lights.glb",
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(LampLightColor2.X, LampLightColor2.Y, LampLightColor2.Z, 1f),
            CastShadows = false,
            PosX = pos.X + unit,
            PosY = pos.Y,
            PosZ = pos.Z + unit,
            Width = 0.5f,
            Height = 1f,
            Depth = 0.5f
        };
        AddControl(light2);

        light3 = new Season.Controls.Model()
        {
            Name = @"Assets/celling_lights.glb",
            Unlit = true,
            MaterialColor = new System.Numerics.Vector4(LampLightColor3.X, LampLightColor3.Y, LampLightColor3.Z, 1f),
            CastShadows = false,
            PosX = pos.X,
            PosY = pos.Y,
            PosZ = pos.Z - unit,
            Width = 0.5f,
            Height = 1f,
            Depth = 0.5f
        };
        AddControl(light3);

        lightSource1 = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "CeilingLamp1",
            Kind = Season.Rendering.LightKind.Spot,
            Color = LampLightColor1,
            Intensity = LightIntensity,
            // Emit from the bulb marker center, which matches the real fixture location.
            // Point lights in this engine do not cast shadows, so there is no need to offset the light away from the lamp mesh.
            Position = new Vector3(light1.PosX, light1.PosY - (float)light1.Height, light1.PosZ),
            Direction = new Vector3(0, -1, 0),
            Range = 8f,
            // DDGI and SDF diffuse-lighting test setup: brighter red light.
            // The cone angles matter only for Spot mode and are kept here so direct floor lighting and indirect wall bounce stay easy to distinguish.
            InnerConeAngle = 12f * MathF.PI / 180f,
            OuterConeAngle = 26f * MathF.PI / 180f,
            CastShadows = true,
            Priority = 50
        });

        lightSource2 = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "CeilingLamp2",
            Kind = Season.Rendering.LightKind.Spot,
            Color = LampLightColor2,
            Intensity = LightIntensity,
            Position = new Vector3(light2.PosX, light2.PosY - (float)light2.Height, light2.PosZ),
            Direction = new Vector3(0, -1, 0),
            Range = 8f,
            InnerConeAngle = 12f * MathF.PI / 180f,
            OuterConeAngle = 26f * MathF.PI / 180f,
            CastShadows = true,
            Priority = 50
        });

        lightSource3 = App.Instance.Lighting.Add(new Season.Rendering.LightSource
        {
            Name = "CeilingLamp3",
            Kind = Season.Rendering.LightKind.Spot,
            Color = LampLightColor3,
            Intensity = LightIntensity,
            Position = new Vector3(light3.PosX, light3.PosY - (float)light3.Height, light3.PosZ),
            Direction = new Vector3(0, -1, 0),
            Range = 8f,
            InnerConeAngle = 12f * MathF.PI / 180f,
            OuterConeAngle = 26f * MathF.PI / 180f,
            CastShadows = true,
            Priority = 50
        });

        bottle = new Season.Controls.Model()
        {
            Name = @"Assets/WaterBottle.glb",
            PosX = 3.5f,
            PosY = 2,
            PosZ = 58,
            Width = 1f,
            Height = 2.5f,
            Depth = 1f
        };
        AddControl(bottle);

        busterDrone = new Season.Controls.Model()
        {
            Name = @"Assets/busterDrone.glb",
            PosX = 0,
            PosY = 2,
            PosZ = 57,
            Width = 3,
            Height = 1f,
            Depth = 3
        };
        AddControl(busterDrone);
    }

    /// <summary>
    /// Room outer size on the three axes. Width, Height, and Depth come from the panel when provided,
    /// and otherwise fall back to the room-local baseline size so the default room shape still works.
    /// </summary>
    Vector3 RoomSize => new Vector3(
        Width.HasValue ? Width.Value : RoomLocalSize.X,
        Height.HasValue ? Height.Value : RoomLocalSize.Y,
        Depth.HasValue ? Depth.Value : RoomLocalSize.Z);

    /// <summary>
    /// Maps room-local coordinates to world space under the unified placement convention.
    /// (PosX, PosY, PosZ) is the outer-bounds center, <see cref="RoomSize"/> is the outer size,
    /// and the local-space minimum corner maps to the world-space minimum corner:
    /// world = Pos - size / 2 + (local / RoomLocalSize) * size.
    /// Because the mapping is axis-aligned scaling plus translation, layout sizes transform with the same formula.
    /// </summary>
    Vector3 RoomPointToWorld(Vector3 roomLocal)
    {
        var size = RoomSize;
        return new Vector3(PosX, PosY, PosZ) - size * 0.5f + roomLocal / RoomLocalSize * size;
    }

    /// <summary>
    /// Returns the walkable floor height in world Y at a given world-space XZ point.
    /// The method tests RoomFloor and each RoomStair against their room-local layout bounds and takes the highest matching top face.
    /// Outside the room and stairs it returns 0, meaning the grass or road ground level.
    /// Heights follow the same linear mapping as <see cref="RoomPointToWorld"/>, so moving or scaling the room updates the stairs as well.
    /// </summary>
    internal float FloorHeightAtWorld(float worldX, float worldZ)
    {
        var size = RoomSize;
        var origin = new Vector3(PosX, PosY, PosZ) - size * 0.5f;

        // Convert world space back to room-local coordinates through the inverse of RoomPointToWorld.
        var local = (new Vector3(worldX, 0f, worldZ) - origin) / size * RoomLocalSize;

        float topLocal = 0f;
        for (int i = 0; i < roomParts.Length; i++)
        {
            var name = roomParts[i].Name;
            if (name != "RoomFloor" && !name.StartsWith("RoomStair"))
                continue;

            var min = roomPartMins[i];
            var max = roomPartMaxs[i];

            if (local.X >= min.X && local.X <= max.X && local.Z >= min.Z && local.Z <= max.Z)
                topLocal = MathF.Max(topLocal, max.Y);
        }

        return topLocal <= 0f ? 0f : origin.Y + topLocal / RoomLocalSize.Y * size.Y;
    }

    /// <summary>
    /// Builds the room from eight wall boxes, one roof, and three stair boxes, with every part represented by its own Mesh3D.
    /// The interior spans X in [-6.8, 6.8], Y in [1.2, 9.2], and Z in [-6.8, 6.8], all surrounded by 1.2-thick outward-facing boxes.
    /// The south wall is split into three boxes to form the doorway, and all part ranges stay in room-local coordinates so Update
    /// can remap them every frame through RoomPointToWorld instead of baking world transforms.
    ///
    /// Splitting the room by wall is important for GI proxies. Proxy granularity is one control per world AABB, so a single Mesh3D room
    /// would produce one solid 13.6 x 8 x 13.6 block and force the whole interior into negative distance values. Per-wall boxes preserve
    /// the hollow-room meaning while remaining equivalent on the rendering side.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max)[] BuildRoomParts()
    {
        // DDGI and SDF test setup: wall thickness must stay above one SDF voxel, otherwise probe rays leak through
        // thin walls and sample sky radiance instead. The 2026-08 scale-up doubled the room axes and wall thickness.
        const float t = 1.2f;   // Wall thickness after the 2x scale-up.
        const float x0 = -6.8f, x1 = 6.8f, y0 = 1.2f, y1 = InteriorCeilingY, z0 = -6.8f, z1 = 6.8f;

        var white = new Vector4(0.85f, 0.85f, 0.85f, 1f);   // White walls with some headroom against HDR overexposure.
        var lightGray = new Vector4(0.7f, 0.7f, 0.7f, 1f); // Light gray floor and ceiling to separate them from the white walls.

        var walls = new (Mesh3D Mesh, Vector3 Min, Vector3 Max)[]
        {
            // Floor and ceiling: full X/Z coverage in light gray.
            MakeRoomPart("RoomFloor", new Vector3(x0 - t, y0 - t, z0 - t), new Vector3(x1 + t, y0, z1 + t), lightGray),
            MakeRoomPart("RoomCeiling", new Vector3(x0 - t, y1, z0 - t), new Vector3(x1 + t, y1 + t, z1 + t), lightGray),
            // South wall at -Z: lintel plus left and right jambs forming the doorway, using the same white as the walls.
            MakeRoomPart("RoomDoorLintel", new Vector3(x0, 6.4f, z0 - t), new Vector3(x1, y1, z0), white),
            MakeRoomPart("RoomDoorJambNegX", new Vector3(x0, y0, z0 - t), new Vector3(-1.6f, 6.4f, z0), white),
            MakeRoomPart("RoomDoorJambPosX", new Vector3(1.6f, y0, z0 - t), new Vector3(x1, 6.4f, z0), white),
            // North wall at +Z, spanning the full X range.
            MakeRoomPart("RoomWallPosZ", new Vector3(x0 - t, y0, z1), new Vector3(x1 + t, y1, z1 + t), white),
            // West wall at -X, spanning between the north and south walls.
            MakeRoomPart("RoomWallNegX", new Vector3(x0 - t, y0, z0), new Vector3(x0, y1, z1), white),
            // East wall at +X, now full-span after moving the door away.
            MakeRoomPart("RoomWallPosX", new Vector3(x1, y0, z0), new Vector3(x1 + t, y1, z1), white),
        };

        // Dark triangular roof added in 2026-08, sharing the same local-coordinate mapping as the wall boxes.
        var roof = BuildRoofPart();
        // White three-step entrance stairs added in 2026-08 to connect the raised threshold to the road.
        var stairs = BuildStairParts();
        var parts = new (Mesh3D Mesh, Vector3 Min, Vector3 Max)[walls.Length + 1 + stairs.Length];
        parts[0] = roof;
        walls.CopyTo(parts, 1);
        stairs.CopyTo(parts, 1 + walls.Length);
        return parts;
    }

    /// <summary>
    /// White three-step entrance stairs. The room floor slab raises the threshold above ground, so the stairs descend
    /// southward from the outer face of the south wall to the road. Each step is a solid thin box using the same unit-box
    /// geometry as other room parts, and each step stays separate so GI proxies fit the actual shape more closely.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max)[] BuildStairParts()
    {
        var white = new Vector4(0.85f, 0.85f, 0.85f, 1f);   // White, matching the wall color.
        const float stairHalfWidth = 1.6f;                  // Half-width matching the doorway.
        const float riser = 0.4f;                           // Riser height per step, totaling 1.2 at the threshold.
        const float tread = 0.6f;                           // Step depth.
        const float wallFaceZ = -8f;                        // Outer face of the south wall in room-local space.

        return new[]
        {
            // Bottom step: reaches the ground at y=0, with the tread at y=0.4.
            MakeRoomPart("RoomStair1", new Vector3(-stairHalfWidth, 0f, wallFaceZ - 3f * tread), new Vector3(stairHalfWidth, 1f * riser, wallFaceZ - 2f * tread), white),
            // Middle step: solid from the ground up, with the tread at y=0.8.
            MakeRoomPart("RoomStair2", new Vector3(-stairHalfWidth, 0f, wallFaceZ - 2f * tread), new Vector3(stairHalfWidth, 2f * riser, wallFaceZ - 1f * tread), white),
            // Top step against the wall: tread at y=1.2, level with the interior floor and threshold.
            MakeRoomPart("RoomStair3", new Vector3(-stairHalfWidth, 0f, wallFaceZ - 1f * tread), new Vector3(stairHalfWidth, 3f * riser, wallFaceZ), white),
        };
    }

    /// <summary>
    /// Dark gray triangular roof with two slopes. The ridge runs along Z, the gable triangles close the north and south ends,
    /// and the roof overhangs on all four sides. Geometry is an outward-facing shell with no bottom face because the ceiling slab
    /// already closes the room interior. Update maps the roof bounds just like the wall boxes.
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max) BuildRoofPart()
    {
        var darkGray = new Vector4(0.28f, 0.28f, 0.30f, 1f);   // Dark gray roof color.

        var part = new Mesh3D()
        {
            Name = "RoomRoof",
            CullingEnabled = false,
        };

        // Key roof coordinates in room-local space.
        const float eaveX = 8f + RoofOverhang;                  // Absolute X of the east and west eave edges.
        const float eaveZ = 8f + RoofOverhang;                  // Absolute Z of the north and south overhang edges.
        const float ridgeY = RoofBaseY + RoofRise;              // Ridge height.

        // Normals point outward. Winding follows the same contract as AddWallFace:
        // the geometric cross product of the index order must match the outward normal, which means u x v equals the negative outward normal.
        var slopeN = Vector3.Normalize(new Vector3(RoofRise, eaveX, 0f));   // Vector perpendicular to the roof slope.
        var eaveZ2 = 2f * eaveZ;

        // West roof slope. Choose u and v so AddRoofFace sees the correct winding for the outward normal.
        AddRoofFace(part, triangle: false, origin: new Vector3(-eaveX, RoofBaseY, eaveZ), u: new Vector3(0, 0, -eaveZ2), v: new Vector3(eaveX, RoofRise, 0), normal: new Vector3(-slopeN.X, slopeN.Y, 0f), color: darkGray);
        // East roof slope.
        AddRoofFace(part, triangle: false, origin: new Vector3(eaveX, RoofBaseY, -eaveZ), u: new Vector3(0, 0, eaveZ2), v: new Vector3(-eaveX, RoofRise, 0), normal: new Vector3(slopeN.X, slopeN.Y, 0f), color: darkGray);
        // South gable triangle above the doorway.
        AddRoofFace(part, triangle: true, origin: new Vector3(-8f, RoofBaseY, -8f), u: new Vector3(16f, 0, 0), v: new Vector3(8f, RoofRise, 0), normal: new Vector3(0, 0, -1), color: darkGray);
        // North gable triangle.
        AddRoofFace(part, triangle: true, origin: new Vector3(8f, RoofBaseY, 8f), u: new Vector3(-16f, 0, 0), v: new Vector3(-8f, RoofRise, 0), normal: new Vector3(0, 0, 1), color: darkGray);

        return (part, new Vector3(-eaveX, RoofBaseY, -eaveZ), new Vector3(eaveX, ridgeY, eaveZ));
    }

    /// <summary>
    /// Adds one roof face, either triangular or quad. Vertices come from origin, origin+u, origin+v,
    /// and optionally origin+u+v, and winding follows the same outward-facing contract as AddWallFace.
    /// The caller must therefore provide u and v so their cross product points opposite the outward normal.
    /// Material settings match the room walls with solid-color PBR shading.
    /// </summary>
    static void AddRoofFace(Mesh3D mesh, bool triangle, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal, Vector4 color)
    {
        var verts = triangle
            ? new[]
            {
                MakeCubeVertex(origin, new Vector2(0, 1), normal),
                MakeCubeVertex(origin + u, new Vector2(1, 1), normal),
                MakeCubeVertex(origin + v, new Vector2(0.5f, 0), normal),   // Triangle peak.
            }
            : new[]
            {
                MakeCubeVertex(origin, new Vector2(0, 1), normal),
                MakeCubeVertex(origin + u, new Vector2(1, 1), normal),
                MakeCubeVertex(origin + v, new Vector2(0, 0), normal),
                MakeCubeVertex(origin + u + v, new Vector2(1, 0), normal),
            };

        // Both faces use clockwise winding when viewed from the outside, matching AddWallFace.
        var indices = triangle
            ? new ushort[] { 0, 2, 1 }
            : new ushort[] { 2, 3, 1, 2, 1, 0 };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = color,
            MetallicFactor = 0f,
            RoughnessFactor = 0.9f,
            Unlit = false,
            Mode = SurfaceBlendMode.Opaque,
        });
    }
    
    /// <summary>
    /// Creates one wall-box control, with one Mesh3D carrying the six faces of a thin box.
    /// Geometry stays as a unit box centered at the origin, while the room-local min and max are returned
    /// so Update can remap Pos, Width, Height, and Depth through RoomPointToWorld every frame.
    /// Each name must stay unique because graphics backends key GPU resources by (Name, ID).
    /// </summary>
    static (Mesh3D Mesh, Vector3 Min, Vector3 Max) MakeRoomPart(string name, Vector3 min, Vector3 max, Vector4 color)
    {
        var part = new Mesh3D()
        {
            Name = name,
            CullingEnabled = false,
        };
        AddRoomBox(part, new Vector3(-0.5f), new Vector3(0.5f), color);
        return (part, min, max);
    }

    /// <summary>
    /// Adds an axis-aligned thin box from min and max corners, with six outward-facing faces and the same winding
    /// convention as BuildCubeSurfaces. Geometry remains purely local, and world placement is controlled entirely
    /// through Pos, Width, Height, and Depth outside this helper.
    /// </summary>
    static void AddRoomBox(Mesh3D mesh, Vector3 min, Vector3 max, Vector4 color)
    {
        var s = max - min;
        var sx = new Vector3(s.X, 0, 0);
        var sy = new Vector3(0, s.Y, 0);
        var sz = new Vector3(0, 0, s.Z);

        // Same six-face construction as BuildCubeSurfaces, only parameterized by size.
        AddWallFace(mesh, origin: new Vector3(min.X, min.Y, min.Z), u: sx, v: sy, normal: new Vector3(0, 0, -1), color: color);
        AddWallFace(mesh, origin: new Vector3(max.X, min.Y, max.Z), u: -sx, v: sy, normal: new Vector3(0, 0, +1), color: color);
        AddWallFace(mesh, origin: new Vector3(min.X, min.Y, max.Z), u: -sz, v: sy, normal: new Vector3(-1, 0, 0), color: color);
        AddWallFace(mesh, origin: new Vector3(max.X, min.Y, min.Z), u: sz, v: sy, normal: new Vector3(+1, 0, 0), color: color);
        AddWallFace(mesh, origin: new Vector3(min.X, max.Y, min.Z), u: sx, v: sz, normal: new Vector3(0, +1, 0), color: color);
        AddWallFace(mesh, origin: new Vector3(min.X, min.Y, max.Z), u: sx, v: -sz, normal: new Vector3(0, -1, 0), color: color);
    }

    /// <summary>
    /// Adds one room wall quad using solid-color PBR shading with metallic 0 and roughness 0.9,
    /// approximating rough plaster so CSM softness stays visible without strong highlights.
    /// </summary>
    static void AddWallFace(Mesh3D mesh, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal, Vector4 color)
    {
        var verts = new Vertex[4];
        verts[0] = MakeCubeVertex(origin, new Vector2(0, 1), normal);
        verts[1] = MakeCubeVertex(origin + u, new Vector2(1, 1), normal);
        verts[2] = MakeCubeVertex(origin + v, new Vector2(0, 0), normal);
        verts[3] = MakeCubeVertex(origin + u + v, new Vector2(1, 0), normal);

        // Both triangles are clockwise when viewed from the outside, matching AddFace.
        var indices = new ushort[] { 2, 3, 1, 2, 1, 0 };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = color,                                // Solid color with no texture.
            MetallicFactor = 0f,
            RoughnessFactor = 0.9f,
            Unlit = false,                                    // Enable PBR lighting.
            Mode = SurfaceBlendMode.Opaque,
        });
    }

    static Vertex MakeCubeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent works because Mesh3D v1 renderMode=0 does not read the TBN basis.
        return new Vertex
        {
            Position = pos,
            TexCoord = uv,
            Normal = normal,
            Tangent = new Vector4(1, 0, 0, 1),
            Joints = Vector4.Zero,
            Weights = Vector4.Zero,
        };
    }

    public override bool Update(float time, float? alpha = null, float? posX = null, float? posY = null, float? posZ = null, float? width = null, float? height = null, float? depth = null)
    {
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, posZ: posZ, width: width, height: height, depth: depth);

        // Recompute every wall-part world transform from the panel parameters each frame, then update each part for upload and draw submission.
        if (roomParts != null)
        {
            var roomSize = RoomSize;
            for (int i = 0; i < roomParts.Length; i++)
            {
                var part = roomParts[i];
                var min = roomPartMins[i];
                var max = roomPartMaxs[i];

                // Axis-aligned linear mapping: transform the part center and stretch size by RoomSize / RoomLocalSize per axis.
                var center = RoomPointToWorld((min + max) * 0.5f);
                var size = (max - min) / RoomLocalSize * roomSize;

                part.PosX = center.X;
                part.PosY = center.Y;
                part.PosZ = center.Z;
                part.Width = size.X;
                part.Height = size.Y;
                part.Depth = size.Z;

                part.Update(time);
            }
        }

        // Ceiling-light marker notes retained here for reference: it would share the spot position,
        // size once after bounds exist, and pin its local origin at LampPosition through AnchorWorldOffset.
        //if (light != null)
        //{
        //    if (!lightSized && light.LocalSize != Vector3.Zero)
        //    {
        //        light.Width = light.LocalSize.X * light.OriginalScale * 0.1f;
        //        light.Height = light.LocalSize.Y * light.OriginalScale * 0.1f;
        //        light.Depth = light.LocalSize.Z * light.OriginalScale * 0.1f;
        //        lightSized = true;
        //    }

        //    var lightPos =  LampPosition + light.AnchorWorldOffset;
        //    light.PosX = lightPos.X;
        //    light.PosY = lightPos.Y;
        //    light.PosZ = lightPos.Z;
        //    light.Alpha = 1f; // lampLevels[lampLevelIndex] > 0f ? 1f : 0f;
        //    light.Update(time: time);
        //}

        light1.Update(time);
        light2.Update(time);
        light3.Update(time);

        //float alpha = 0f;
        if (bottle != null)
        {
            if (bottle.Positive)
            {
                bottle?.Time += time;
            }
            else
            {
                bottle?.Time -= time;
            }

            if (bottle.Time >= 5f)
            {
                bottle.Positive = false;

                bottle.Time = 5f;
            }
            else if (bottle.Time <= 0f)
            {
                bottle.Positive = true;

                bottle.Time = 0f;
            }

            // Unified placement convention: write rotation first, then use the current AnchorWorldOffset
            // to pin the model-local origin at its display position in room-local coordinates.
            bottle.Rotation = App.Instance.Time;

            if (bottle.LoadComplete.HasValue)
            {
                float elapsed = (float)(DateTime.UtcNow - bottle.LoadComplete.Value).TotalSeconds;

                //alpha = Math.Min(elapsed / 2.0f, 2.0f);  // One-second fade-in.
            }
        }
        if (bottle.Update(time: time, alpha: 1f))
        {
            result = true;
        }

        if (busterDrone.Update(time: time, alpha: 0.5f + (float)Math.Sin(App.Instance.Time) / 2f))
        {
            result = true;
        }

        return result;
    }
}
