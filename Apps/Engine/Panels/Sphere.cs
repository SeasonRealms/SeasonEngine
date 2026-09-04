// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Engine.Panels;

internal class Sphere : Panel
{
    // 1-7 first validation target for environment specular: a row of procedurally generated UV spheres
    // with metallic=1 and roughness increasing linearly from 0 to 1.
    // Each sphere owns its own Surface, and each Surface carries its own MetallicFactor and
    // RoughnessFactor plus one MaterialParams constant buffer, so a single Mesh3D can present
    // the material gradient without needing N separate Models and overrides.
    internal Mesh3D sphereRow;

    /// <summary>Number of spheres in the 1-7 validation row. Roughness is computed as i/(N-1), so N must be at least 2.</summary>
    const int SphereRowCount = 7;

    internal Sphere()
    {
        // 1-7 first validation: a roughness-gradient sphere row. It was moved outside the door
        // to X=24, with the door facing +X, and placed at Z=2.5 to keep even spacing from the other groups.
        // Y=1.55 preserves the previous indoor height.
        // All spheres use metallic=1, so F0=albedo and the envSpecular*F0*(1-rough)^2 term is maximized.
        // Roughness runs linearly from 0 for a perfect mirror to 1 for no reflection.
        // In Step A there is only intensity attenuation and no mip blur, so the validation criterion
        // is a monotonic left-bright to right-dark gradient. Without the hook-up, the whole row stays uniformly dark.
        // Unified placement convention: vertices are generated in local space, with the anchor at the
        // geometric center of the bounding box. The row spans x in [0,4.16], y in [-0.56,0], and z in [0,0.56].
        // World placement is set once by Pos as the anchor world position, while Width, Height, and Depth
        // provide the target size and are applied through ComputedScale.
        const float radius = 0.28f;
        const float spacing = 0.6f;
        sphereRow = new Mesh3D()
        {
            Name = "SphereRow",
            PosX = -15, // OffsetX - 1.8f - radius,
            PosY = 1.5f, // 1.55f + radius,
            PosZ = 20,  // OffsetZ - radius,
            Width = 21,
            Height = 3,
            Depth = 3
        };
        float radians = NormalizeDegrees(270) * MathF.PI / 180f;
        sphereRow.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);

        for (int i = 0; i < SphereRowCount; i++)
        {
            sphereRow.Surfaces.Add(MakeSphereSurface(
                center: new Vector3(radius + i * spacing, -radius, radius),
                radius: radius,
                metallic: 1f,
                roughness: i / (float)(SphereRowCount - 1),
                color: new Vector4(0.95f, 0.93f, 0.88f, 1f)));
        }
        AddControl(sphereRow);
    }

    static float NormalizeDegrees(float degrees) => ((degrees % 360f) + 360f) % 360f;

    /// <summary>
    /// 1-7 validation sphere built as a single procedural UV-sphere surface with solid-color PBR material.
    /// Metallic and roughness are provided by the caller.
    /// The vertex grid has (segV+1)*(segU+1)=561 vertices and segU*segV*6=3072 indices,
    /// far below the ushort limit of 65535 for Surface.Indices.
    /// Winding follows the same {2,3,1,2,1,0} order as the room and cube code, whose front face
    /// lies on the -cross(u,v) side in the engine's LH + FrontCounterClockwise=0 convention,
    /// where clockwise triangles seen from outside are front-facing.
    /// For the sphere, u is the latitude direction and v is the longitude direction, so -cross(u,v)
    /// points outward naturally and DoubleSided is unnecessary.
    /// Normals are simply the unit sphere direction, with N = (P-center)/radius.
    /// </summary>
    static Surface MakeSphereSurface(Vector3 center, float radius, float metallic, float roughness, Vector4 color)
    {
        const int segU = 32;   // Longitude segments around Y
        const int segV = 16;   // Latitude segments from the +Y pole to the -Y pole

        var verts = new Vertex[(segV + 1) * (segU + 1)];
        for (int i = 0; i <= segV; i++)
        {
            float theta = MathF.PI * i / segV;          // Polar angle: 0 = +Y pole, pi = -Y pole
            float st = MathF.Sin(theta), ct = MathF.Cos(theta);
            for (int j = 0; j <= segU; j++)
            {
                float phi = MathF.Tau * j / segU;
                var n = new Vector3(st * MathF.Cos(phi), ct, st * MathF.Sin(phi));
                verts[i * (segU + 1) + j] = MakeCubeVertex(
                    center + n * radius,
                    new Vector2(j / (float)segU, i / (float)segV),
                    n);
            }
        }

        var indices = new ushort[segU * segV * 6];
        int k = 0;
        for (int i = 0; i < segV; i++)
        {
            for (int j = 0; j < segU; j++)
            {
                // Matches AddWallFace vertex order exactly:
                // 0=(i,j), 1=(i+1,j), 2=(i,j+1), 3=(i+1,j+1),
                // so u follows i (latitude, downward) and v follows j (longitude).
                ushort v0 = (ushort)(i * (segU + 1) + j);
                ushort v1 = (ushort)((i + 1) * (segU + 1) + j);
                ushort v2 = (ushort)(i * (segU + 1) + j + 1);
                ushort v3 = (ushort)((i + 1) * (segU + 1) + j + 1);

                indices[k++] = v2; indices[k++] = v3; indices[k++] = v1;
                indices[k++] = v2; indices[k++] = v1; indices[k++] = v0;
            }
        }

        return new Surface
        {
            Vertices = verts,
            Indices = indices,
            BaseColor = color,                                // Solid-color mode with no texture
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            Unlit = false,                                    // Enable PBR lighting
            Mode = SurfaceBlendMode.Opaque,
        };
    }

    static Vertex MakeCubeVertex(Vector3 pos, Vector2 uv, Vector3 normal)
    {
        // Any reasonable tangent is fine because Mesh3D v1 renderMode=0 does not sample TBN in the pixel shader.
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

        // 1-7 sphere row stays static; Update only triggers first-frame geometry upload and draw submission.
        sphereRow?.Update(time);

        return result;
    }
}
