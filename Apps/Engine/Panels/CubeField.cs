// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class CubeField : Panel
{
    // 2026-08: all demo objects were moved outside the room.
    // The door is on the +X wall with z in [-0.8,0.8], and "outside" is about three room widths away
    // along X, which is approximately 24 meters because the room half-width is 8 meters.
    // The four demo groups are then arranged along Z at even 5-meter spacing in front of the door,
    // while keeping their previous indoor height.
    const float OffsetX = 24f;
    const float OffsetZ = -7.5f;

    // Shared grid-layout constants for BuildInstancedCubeField and AnimateInstancedCubeField.
    // Animation frames rebuild instance positions from grid points; see AnimateInstancedCubeField.
    const int FieldColumns = 12, FieldRows = 8;
    const float FieldSpacing = 0.35f;
    const float FieldStartZ = 0.10f;
    static readonly float FieldStartX = -((FieldColumns - 1) * FieldSpacing) * 0.5f;
    static readonly Vector3 FieldOffset = new Vector3(OffsetX, 0f, OffsetZ);

    Mesh3D cube;
    InstancedMesh3D instancedCubeField;

    internal CubeField()
    {
        // Mesh3D cube example with Sun.png shared by all six faces.
        // The cube has side length 0.6 and is centered at the origin, with outward normals along +/-X, +/-Y, and +/-Z.
        // The engine uses LH coordinates with FrontCounterClockwise=0, so clockwise triangles seen from outside are front-facing.
        cube = new Mesh3D()
        {
            Name = "Cube",
            // Unified placement convention: unit cube with local side length 1, scaled to 0.4 meters on each axis.
            Width = 0.4f,
            Height = 0.4f,
            Depth = 0.4f
        };
        BuildCubeSurfaces(cube, "Assets/Sun.png");
        AddControl(cube);

        instancedCubeField = new InstancedMesh3D()
        {
            Name = "InstancedCubeField"
        };
        BuildCubeSurfaces(instancedCubeField, "Assets/Sun.png");
        BuildInstancedCubeField(instancedCubeField);
        AddControl(instancedCubeField);
    }

    static void AnimateInstancedCubeField(InstancedMesh3D mesh, float time)
    {
        if (mesh.Instances.Count == 0)
            return;

        for (int i = 0; i < mesh.Instances.Count; i++)
        {
            var instance = mesh.Instances[i];
            float column = i % 12;
            float row = i / 12;
            float phase = time * 1.2f + column * 0.35f + row * 0.2f;

            instance.Rotation = Quaternion.CreateFromYawPitchRoll(time * 0.8f + row * 0.15f, time * 0.5f + column * 0.1f, 0f);

            // Unified placement convention: pin the template's local origin at the grid point, including the wave Y offset.
            // Rotation is applied first, then InstanceAnchorWorldOffset is queried from the current pose.
            // Instance size was settled to 0.1 meters in the constructor, so this stays equivalent to the old p*S*R+t transform.
            var target = new Vector3(
                FieldStartX + column * FieldSpacing,
                -0.15f + MathF.Sin(phase) * 0.04f,
                FieldStartZ + row * FieldSpacing) + FieldOffset;
            var pos = target + mesh.InstanceAnchorWorldOffset(instance);
            instance.PosX = pos.X;
            instance.PosY = pos.Y;
            instance.PosZ = pos.Z;
        }
    }

    /// <summary>
    /// Builds six cube-face surfaces for a unit cube centered at the origin, with four vertices and two triangles per face.
    /// The engine convention is LH coordinates with FrontCounterClockwise=0, so clockwise triangles seen from outside are front-facing.
    /// UVs cover the four corners of [0,1] on each face: TL=(0,0), TR=(1,0), BR=(1,1), BL=(0,1).
    /// </summary>
    static void BuildCubeSurfaces(Mesh3D mesh, string texPath)
    {
        const float h = 0.5f;

        // -Z, the near face looking toward the camera
        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, -h), u: new Vector3(+1, 0, 0), v: new Vector3(0, +1, 0), normal: new Vector3(0, 0, -1));
        // +Z, the far face
        AddFace(mesh, texPath,
            origin: new Vector3(+h, -h, +h), u: new Vector3(-1, 0, 0), v: new Vector3(0, +1, 0), normal: new Vector3(0, 0, +1));
        // -X, the left face
        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, +h), u: new Vector3(0, 0, -1), v: new Vector3(0, +1, 0), normal: new Vector3(-1, 0, 0));
        // +X, the right face
        AddFace(mesh, texPath,
            origin: new Vector3(+h, -h, -h), u: new Vector3(0, 0, +1), v: new Vector3(0, +1, 0), normal: new Vector3(+1, 0, 0));
        // +Y, the top face: u=+X and v=+Z, with origin moved to the -Z side so normal = -cross(u,v) = +Y
        AddFace(mesh, texPath,
            origin: new Vector3(-h, +h, -h), u: new Vector3(+1, 0, 0), v: new Vector3(0, 0, +1), normal: new Vector3(0, +1, 0));
        // -Y, the bottom face: u=+X and v=-Z, with origin moved to the +Z side so normal = -cross(u,v) = -Y
        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, +h), u: new Vector3(+1, 0, 0), v: new Vector3(0, 0, -1), normal: new Vector3(0, -1, 0));
    }

    static void BuildCubeSurfaces(InstancedMesh3D mesh, string texPath)
    {
        const float h = 0.5f;

        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, -h), u: new Vector3(+1, 0, 0), v: new Vector3(0, +1, 0), normal: new Vector3(0, 0, -1));
        AddFace(mesh, texPath,
            origin: new Vector3(+h, -h, +h), u: new Vector3(-1, 0, 0), v: new Vector3(0, +1, 0), normal: new Vector3(0, 0, +1));
        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, +h), u: new Vector3(0, 0, -1), v: new Vector3(0, +1, 0), normal: new Vector3(-1, 0, 0));
        AddFace(mesh, texPath,
            origin: new Vector3(+h, -h, -h), u: new Vector3(0, 0, +1), v: new Vector3(0, +1, 0), normal: new Vector3(+1, 0, 0));
        AddFace(mesh, texPath,
            origin: new Vector3(-h, +h, -h), u: new Vector3(+1, 0, 0), v: new Vector3(0, 0, +1), normal: new Vector3(0, +1, 0));
        AddFace(mesh, texPath,
            origin: new Vector3(-h, -h, +h), u: new Vector3(+1, 0, 0), v: new Vector3(0, 0, -1), normal: new Vector3(0, -1, 0));
    }

    static void AddFace(Mesh3D mesh, string texPath, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
    {
        // Four corners, with origin at the bottom-left of the face, u pointing right and v up,
        // while UV origin stays at the top-left:
        //   index 0 = BL, uv=(0,1)
        //   index 1 = BR, uv=(1,1)
        //   index 2 = TL, uv=(0,0)
        //   index 3 = TR, uv=(1,0)
        var verts = new Vertex[4];
        verts[0] = MakeCubeVertex(origin, new Vector2(0, 1), normal);
        verts[1] = MakeCubeVertex(origin + u, new Vector2(1, 1), normal);
        verts[2] = MakeCubeVertex(origin + v, new Vector2(0, 0), normal);
        verts[3] = MakeCubeVertex(origin + u + v, new Vector2(1, 0), normal);

        // Both triangles are clockwise when viewed from outside: TL->TR->BR and TL->BR->BL.
        var indices = new ushort[] { 2, 3, 1, 2, 1, 0 };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColorTexturePath = texPath,
            BaseColor = Vector4.One,
            Mode = SurfaceBlendMode.Mask,
        });
    }

    static void AddFace(InstancedMesh3D mesh, string texPath, Vector3 origin, Vector3 u, Vector3 v, Vector3 normal)
    {
        var verts = new Vertex[4];
        verts[0] = MakeCubeVertex(origin, new Vector2(0, 1), normal);
        verts[1] = MakeCubeVertex(origin + u, new Vector2(1, 1), normal);
        verts[2] = MakeCubeVertex(origin + v, new Vector2(0, 0), normal);
        verts[3] = MakeCubeVertex(origin + u + v, new Vector2(1, 0), normal);

        var indices = new ushort[] { 2, 3, 1, 2, 1, 0 };

        mesh.Surfaces.Add(new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColorTexturePath = texPath,
            BaseColor = Vector4.One,
            Mode = SurfaceBlendMode.Mask,
        });
    }

    static void BuildInstancedCubeField(InstancedMesh3D mesh)
    {
        mesh.Instances.Clear();

        for (int row = 0; row < FieldRows; row++)
        {
            for (int column = 0; column < FieldColumns; column++)
            {
                var pos = new Vector3(FieldStartX + column * FieldSpacing, -0.15f, FieldStartZ + row * FieldSpacing) + FieldOffset;

                mesh.Instances.Add(new MeshInstanceTransform
                {
                    // Unified placement convention: unit-cube template with local side length 1,
                    // scaled to 0.1 meters on each axis.
                    // Pos writes the grid point directly, and AnimateInstancedCubeField refines the anchor offset per frame from the current pose.
                    PosX = pos.X,
                    PosY = pos.Y,
                    PosZ = pos.Z,
                    Width = 0.10f,
                    Height = 0.10f,
                    Depth = 0.10f,
                });
            }
        }
    }

    static Vertex MakeCubeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent is fine because Mesh3D v1 renderMode=0 does not use TBN in the pixel shader.
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
        var result = base.Update(time, alpha: alpha, posX: posX, posY: posY, width: width, height: height);

        // Cube: placed far in front of the door at X=24 and still follows the original rotation animation.
        // Y=-0.8 preserves the previous indoor height.
        if (cube != null)
        {
            cube.Alpha = 1f; // 0.5f + (float)Math.Sin(Time) / 2f;
            // Unified placement convention: write rotation first, then use AnchorWorldOffset from the current pose
            // to pin the local origin, which is the cube center, to the display location.
            // Recomputing this every frame keeps the effective rotation pivot at the cube center.
            cube.Rotation = Quaternion.CreateFromYawPitchRoll(App.Instance.Time, App.Instance.Time * 0.5f, 0f);
            var cubePos = new Vector3(OffsetX, -0.8f, OffsetZ) + cube.AnchorWorldOffset;
            cube.PosX = cubePos.X;
            cube.PosY = cubePos.Y;
            cube.PosZ = cubePos.Z;
            if (cube.Update(time))
            {
                result = true;
            }
        }

        if (instancedCubeField != null)
        {
            AnimateInstancedCubeField(instancedCubeField, App.Instance.Time);
            instancedCubeField.Update(time);
        }

        return result;
    }
}
