// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using System.Reflection;

namespace Season.Models;

public static class GLTFTools
{
    static int _skinAccessorProbeLogCount;
    static readonly string[] _textureImagePropertyNames = ["PrimaryImage", "Image", "FallbackImage"];

    public static (GLTFMaterial, List<SharpGLTF.Schema2.Image>) LoadMaterial(ModelRoot model, MeshPrimitive meshPrimitive)
    {
        var images = new List<SharpGLTF.Schema2.Image>();

        GLTFMaterial gLTFMaterial1 = null;

        var gltfMaterial = meshPrimitive.Material;

        if (gltfMaterial is null)
        {

        }
        else
        {
            gLTFMaterial1 = new GLTFMaterial();
            SharpGLTF.Schema2.Image baseColorImage = null;
            SharpGLTF.Schema2.Image normalImage = null;
            SharpGLTF.Schema2.Image metallicRoughnessImage = null;
            SharpGLTF.Schema2.Image occlusionImage = null;
            SharpGLTF.Schema2.Image emissiveImage = null;

            gLTFMaterial1.AlphaMode = gltfMaterial.Alpha.ToString();
            gLTFMaterial1.AlphaCutoff = gltfMaterial.AlphaCutoff;

            gLTFMaterial1.DoubleSided = gltfMaterial.DoubleSided;

            // 1. First detect which PBR workflow is being used.
            bool isSpecularGlossiness = false;

            // Check whether a Specular-Glossiness extension is present.
            if (gltfMaterial.Extensions != null)
            {
                foreach (var extension in gltfMaterial.Extensions)
                {
                    if (extension.ToString().Contains(SharpGLTF.Materials.KnownChannel.SpecularGlossiness.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        isSpecularGlossiness = true;
                        break;
                    }
                }
            }

            // 2. Select the proper channel lookup logic according to the workflow.
            if (isSpecularGlossiness)
            {
                // Specular-Glossiness workflow.
                if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.Diffuse.ToString()) is MaterialChannel diffuse)
                {
                    gLTFMaterial1.BaseColorFactor = diffuse.Color;
                    baseColorImage = ResolveTextureImage(diffuse.Texture, out var baseColorIndex);
                    gLTFMaterial1.BaseColorIndex = baseColorIndex;
                }

                if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.SpecularGlossiness.ToString()) is MaterialChannel specularGlossiness)
                {
                    // Specular-Glossiness needs to be converted to Metallic-Roughness here,
                    // or GLTFMaterial needs to be extended to support Specular-Glossiness directly.
                    metallicRoughnessImage = ResolveTextureImage(specularGlossiness.Texture, out var metallicRoughnessIndex);
                    gLTFMaterial1.MetallicRoughnessIndex = metallicRoughnessIndex;
                }
            }
            else
            {
                // Standard Metallic-Roughness workflow.
                if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.BaseColor.ToString()) is MaterialChannel baseColor)
                {
                    gLTFMaterial1.BaseColorFactor = baseColor.Color;
                    baseColorImage = ResolveTextureImage(baseColor.Texture, out var baseColorIndex);
                    gLTFMaterial1.BaseColorIndex = baseColorIndex;
                }

                if (gltfMaterial.FindChannel("MetallicRoughness") is MaterialChannel metallicRoughness)
                {
                    metallicRoughnessImage = ResolveTextureImage(metallicRoughness.Texture, out var metallicRoughnessIndex);
                    gLTFMaterial1.MetallicRoughnessIndex = metallicRoughnessIndex;
                }
            }

            if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.Normal.ToString()) is MaterialChannel normal)
            {
                normalImage = ResolveTextureImage(normal.Texture, out var normalIndex);
                gLTFMaterial1.NormalIndex = normalIndex;
            }

            if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.Occlusion.ToString()) is MaterialChannel occlusion)
            {
                occlusionImage = ResolveTextureImage(occlusion.Texture, out var occlusionIndex);
                gLTFMaterial1.OcclusionIndex = occlusionIndex;
            }

            if (gltfMaterial.FindChannel(SharpGLTF.Materials.KnownChannel.Emissive.ToString()) is MaterialChannel emissive)
            {
                // Convert Vector4 to Vector3 and ignore the alpha channel.
                gLTFMaterial1.EmissiveFactor = new System.Numerics.Vector3(
                    emissive.Color.X,
                    emissive.Color.Y,
                    emissive.Color.Z
                );

                emissiveImage = ResolveTextureImage(emissive.Texture, out var emissiveIndex);
                gLTFMaterial1.EmissiveIndex = emissiveIndex;
            }

            images.AddRange(baseColorImage, normalImage, metallicRoughnessImage, occlusionImage, emissiveImage);
        }

        return (gLTFMaterial1, images);
    }

    static SharpGLTF.Schema2.Image ResolveTextureImage(SharpGLTF.Schema2.Texture texture, out int imageIndex)
    {
        imageIndex = -1;

        if (texture is null) return null;

        var textureType = texture.GetType();

        foreach (var propertyName in _textureImagePropertyNames)
        {
            var property = textureType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.CanRead != true) continue;
            if (!typeof(SharpGLTF.Schema2.Image).IsAssignableFrom(property.PropertyType)) continue;

            if (property.GetValue(texture) is SharpGLTF.Schema2.Image image)
            {
                imageIndex = image.LogicalIndex;
                return image;
            }
        }

        return null;
    }

    public static (List<Vertex> vertices, List<uint> indices) LoadMeshPrimitive(MeshPrimitive meshPrimitive)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        //uint firsetIndex = (uint)indices.Count;
        uint vertexOffset = (uint)vertices.Count;
        int indexCount = 0;

        IList<System.Numerics.Vector3>? positionBuffer = null;
        IList<System.Numerics.Vector3>? normalBuffer = null;
        IList<System.Numerics.Vector2>? texCoordBuffer = null;
        IList<System.Numerics.Vector4>? colorBuffer = null;
        IList<System.Numerics.Vector4>? tangentBuffer = null;

        // Skinning data.
        //IList<System.Numerics.Vector4>? jointsBuffer = null;
        //IList<System.Numerics.Vector4>? weightsBuffer = null;

        uint vertexCount = 0;

        if (meshPrimitive.VertexAccessors.TryGetValue("POSITION", out Accessor? positionAccessor))
        {
            positionBuffer = positionAccessor.AsVector3Array();
            vertexCount = (uint)positionAccessor.Count;
        }

        if (meshPrimitive.VertexAccessors.TryGetValue("NORMAL", out Accessor? normalAccessor))
        {
            normalBuffer = normalAccessor.AsVector3Array();
        }

        if (meshPrimitive.VertexAccessors.TryGetValue("TEXCOORD_0", out Accessor? texCoordAccessor))
        {
            texCoordBuffer = texCoordAccessor.AsVector2Array();
        }

        if (meshPrimitive.VertexAccessors.TryGetValue("COLOR_0", out Accessor? colorAccessor))
        {
            // Check accessor dimensions.
            if (colorAccessor.Dimensions == DimensionType.VEC3)
            {
                var vec3Colors = colorAccessor.AsVector3Array();
                colorBuffer = vec3Colors.Select(c => new System.Numerics.Vector4(c.X, c.Y, c.Z, 1.0f)).ToArray(); // Default opaque alpha.
            }
            else if (colorAccessor.Dimensions == DimensionType.VEC4)
            {
                colorBuffer = colorAccessor.AsVector4Array().ToArray();
            }
            else
            {
                Debug.WriteLine($"Unsupported COLOR_0 dimensions: {colorAccessor.Dimensions}");
            }
        }

        if (meshPrimitive.VertexAccessors.TryGetValue("TANGENT", out Accessor? tangentAccessor))
        {
            tangentBuffer = tangentAccessor.AsVector4Array();
        }

        // Load skinning data.
        //if (meshPrimitive.VertexAccessors.TryGetValue("JOINTS_0", out Accessor? jointsAccessor))
        //{
        //    jointsBuffer = jointsAccessor.AsVector4Array();
        //}

        //if (meshPrimitive.VertexAccessors.TryGetValue("WEIGHTS_0", out Accessor? weightsAccessor))
        //{
        //    weightsBuffer = weightsAccessor.AsVector4Array();
        //}

        // On Web or browser paths, GetVertexAccessor("JOINTS_0"/"WEIGHTS_0") may fail to retrieve data.
        // Prefer direct lookup in the VertexAccessors dictionary and fall back to GetVertexAccessor afterward.
        meshPrimitive.VertexAccessors.TryGetValue("JOINTS_0", out Accessor? jointIndicesAccessor);
        meshPrimitive.VertexAccessors.TryGetValue("WEIGHTS_0", out Accessor? weightsAccessor);
        jointIndicesAccessor ??= meshPrimitive.GetVertexAccessor("JOINTS_0");
        weightsAccessor ??= meshPrimitive.GetVertexAccessor("WEIGHTS_0");

        System.Numerics.Vector4[] jointIndices = null;
        System.Numerics.Vector4[] weights = null;

        if (jointIndicesAccessor != null && weightsAccessor != null)
        {
            jointIndices = LoadJointIndices(jointIndicesAccessor);
            weights = weightsAccessor.AsVector4Array().ToArray();

        }

        for (uint i = 0; i < vertexCount; i++)
        {
            // RH-to-LH conversion: negate Position.Z.
            // glTF uses right-handed -Z forward, while DirectX uses left-handed +Z forward.
            var position = positionBuffer != null ? positionBuffer[(int)i] : System.Numerics.Vector3.Zero;

            // RH-to-LH conversion: negate Normal.Z so the normal follows the mirrored Z axis.
            var normal = normalBuffer != null ? normalBuffer[(int)i] : System.Numerics.Vector3.Zero;

            var texCoord = texCoordBuffer != null ? texCoordBuffer[(int)i] : System.Numerics.Vector2.Zero;

            // RH-to-LH conversion: negate the Z component of Tangent.xyz, but keep W,
            // which stores the bitangent sign, unchanged.
            // Reason: in the shader, bitangent = cross(normal, tangent.xyz) * tangent.w.
            // Once both normal and tangent are mirrored on Z, the cross-product result already has the correct Z sign,
            // so W does not need further adjustment.
            var tangent = tangentBuffer != null ? tangentBuffer[(int)i] : System.Numerics.Vector4.Zero;

            // Skinning data.
            var jointIndicesVec = jointIndices != null ? jointIndices[(int)i] : System.Numerics.Vector4.Zero;
            var weightsVec = weights != null ? weights[(int)i] : System.Numerics.Vector4.Zero;

            // Normalize weights so the sum stays at 1.
            float weightSum = weightsVec.X + weightsVec.Y + weightsVec.Z + weightsVec.W;
            if (weightSum > 0.0f && Math.Abs(weightSum - 1.0f) > 0.0001f)
            {
                weightsVec = new System.Numerics.Vector4(
                    weightsVec.X / weightSum,
                    weightsVec.Y / weightSum,
                    weightsVec.Z / weightSum,
                    weightsVec.W / weightSum
                );
            }
            var vertex = new Vertex
            {
                Position = new System.Numerics.Vector3(position.X, position.Y, -position.Z),
                TexCoord = new System.Numerics.Vector2(texCoord.X, texCoord.Y),
                Normal = new System.Numerics.Vector3(normal.X, normal.Y, -normal.Z),
                Tangent = new System.Numerics.Vector4(tangent.X, tangent.Y, -tangent.Z, tangent.W),
                Joints = jointIndicesVec,
                Weights = weightsVec
            };

            //var vertex = new Vertex()
            //{
            //    Position = new System.Numerics.Vector3(position.X, position.Y, position.Z),
            //    TexCoord = new System.Numerics.Vector2(texCoord.X, texCoord.Y),
            //    Normal = normalBuffer != null ? new System.Numerics.Vector3(normal0.X, normal0.Y, normal0.Z) : System.Numerics.Vector3.UnitZ,
            //    Tangent = tangentBuffer != null ? new System.Numerics.Vector4(tangent.X, tangent.Y, tangent.Z, tangent.W) : System.Numerics.Vector4.UnitX,
            //    JointIndices = jointIndicesVec,
            //    Weights = weightsVec
            //    //Color = new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
            //    //Bitangent = Vector3.Cross(normal0, new Vector3(tangent.X, tangent.Y, -tangent.Z)) * tangent.W // Use W to determine handedness.
            //};
            //(vertices.Min(ve => ve.Normal.X) / 2 + 0.5) + ":" + (vertices.Max(ve => ve.Normal.X) / 2 + 0.5)
            //vertex.Normal = new Vector3(0.5f, 0.5f, 1.0f);

            vertices.Add(vertex);
        }

        if (meshPrimitive.IndexAccessor != null)
        {
            indexCount = meshPrimitive.IndexAccessor.Count;

            var indexBuffer = meshPrimitive.IndexAccessor.AsIndicesArray();

            for (int i = 0; i < indexCount; i++)
            {
                indices.Add((uint)indexBuffer[i] + vertexOffset);
            }
        }
        else
        {
            // Generate sequential indices when none are provided.
            for (uint i = 0; i < vertices.Count; i++)
            {
                indices.Add(i);
            }
        }

        // Reverse index order to match DirectX winding in left-handed space.
        for (var i = 0; i < indices.Count; i = i + 3)
        {
            var tmp = indices[i];
            indices[i] = indices[i + 2];
            indices[i + 2] = tmp;

            //var three = indices[i + 2];
            //indices[i + 2] = indices[0];
            //indices[i] = three;
        }

        return (vertices, indices);
    }

    public static List<GLTFMorphTarget> LoadMorphTargets(MeshPrimitive meshPrimitive, int vertexCount)
    {
        var result = new List<GLTFMorphTarget>();
        if (meshPrimitive == null || meshPrimitive.MorphTargetsCount <= 0 || vertexCount <= 0)
            return result;

        for (int targetIndex = 0; targetIndex < meshPrimitive.MorphTargetsCount; targetIndex++)
        {
            var accessors = meshPrimitive.GetMorphTargetAccessors(targetIndex);
            var target = new GLTFMorphTarget();

            if (accessors.TryGetValue("POSITION", out var positionAccessor) && positionAccessor != null)
                target.PositionDeltas = ConvertMorphVector3Accessor(positionAccessor, vertexCount, flipZ: true);

            if (accessors.TryGetValue("NORMAL", out var normalAccessor) && normalAccessor != null)
                target.NormalDeltas = ConvertMorphVector3Accessor(normalAccessor, vertexCount, flipZ: true);

            if (accessors.TryGetValue("TANGENT", out var tangentAccessor) && tangentAccessor != null)
                target.TangentDeltas = ConvertMorphVector3Accessor(tangentAccessor, vertexCount, flipZ: true);

            result.Add(target);
        }

        return result;
    }

    public static void ApplyMorphTargets(
        IReadOnlyList<Vertex> baseVertices,
        IReadOnlyList<GLTFMorphTarget>? morphTargets,
        float[]? weights,
        List<Vertex> destination)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        destination.Clear();
        if (baseVertices == null || baseVertices.Count == 0)
            return;

        if (morphTargets == null || morphTargets.Count == 0 || weights == null || weights.Length == 0)
        {
            for (int i = 0; i < baseVertices.Count; i++)
                destination.Add(baseVertices[i]);
            return;
        }

        for (int vertexIndex = 0; vertexIndex < baseVertices.Count; vertexIndex++)
        {
            var vertex = baseVertices[vertexIndex];
            var position = vertex.Position;
            var normal = vertex.Normal;
            var tangentXYZ = new System.Numerics.Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);

            for (int targetIndex = 0; targetIndex < morphTargets.Count; targetIndex++)
            {
                float weight = targetIndex < weights.Length ? weights[targetIndex] : 0f;
                if (Math.Abs(weight) <= 1e-6f)
                    continue;

                var target = morphTargets[targetIndex];
                if (target.PositionDeltas.Length > vertexIndex)
                    position += target.PositionDeltas[vertexIndex] * weight;
                if (target.NormalDeltas.Length > vertexIndex)
                    normal += target.NormalDeltas[vertexIndex] * weight;
                if (target.TangentDeltas.Length > vertexIndex)
                    tangentXYZ += target.TangentDeltas[vertexIndex] * weight;
            }

            if (normal.LengthSquared() > 1e-10f)
                normal = System.Numerics.Vector3.Normalize(normal);
            else
                normal = vertex.Normal;

            if (tangentXYZ.LengthSquared() > 1e-10f)
                tangentXYZ = System.Numerics.Vector3.Normalize(tangentXYZ);
            else
                tangentXYZ = new System.Numerics.Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);

            vertex.Position = position;
            vertex.Normal = normal;
            vertex.Tangent = new System.Numerics.Vector4(tangentXYZ, vertex.Tangent.W);
            destination.Add(vertex);
        }
    }

    //public static (List<Vertex> vertices, List<ushort> indices) LoadMeshPrimitive(MeshPrimitive meshPrimitive, float scale)
    //{
    //    var vertices = new List<Vertex>();
    //    var indices = new List<ushort>();

    //    //uint firsetIndex = (uint)indices.Count;
    //    uint vertexOffset = (uint)vertices.Count;
    //    int indexCount = 0;

    //    IList<System.Numerics.Vector3>? positionBuffer = null;
    //    IList<System.Numerics.Vector3>? normalBuffer = null;
    //    IList<System.Numerics.Vector2>? texCoordBuffer = null;
    //    IList<System.Numerics.Vector3>? colorBuffer = null;
    //    IList<System.Numerics.Vector4>? tangentBuffer = null;

    //    // Skinning data.
    //    //IList<System.Numerics.Vector4>? jointsBuffer = null;
    //    //IList<System.Numerics.Vector4>? weightsBuffer = null;

    //    uint vertexCount = 0;

    //    if (meshPrimitive.VertexAccessors.TryGetValue("POSITION", out Accessor? positionAccessor))
    //    {
    //        positionBuffer = positionAccessor.AsVector3Array();
    //        vertexCount = (uint)positionAccessor.Count;
    //    }

    //    if (meshPrimitive.VertexAccessors.TryGetValue("NORMAL", out Accessor? normalAccessor))
    //    {
    //        normalBuffer = normalAccessor.AsVector3Array();
    //    }

    //    if (meshPrimitive.VertexAccessors.TryGetValue("TEXCOORD_0", out Accessor? texCoordAccessor))
    //    {
    //        texCoordBuffer = texCoordAccessor.AsVector2Array();
    //    }

    //    if (meshPrimitive.VertexAccessors.TryGetValue("COLOR_0", out Accessor? colorAccessor))
    //    {
    //        colorBuffer = colorAccessor.AsVector3Array();
    //    }

    //    if (meshPrimitive.VertexAccessors.TryGetValue("TANGENT", out Accessor? tangentAccessor))
    //    {
    //        tangentBuffer = tangentAccessor.AsVector4Array();
    //    }

    //    // Load skinning data.
    //    //if (meshPrimitive.VertexAccessors.TryGetValue("JOINTS_0", out Accessor? jointsAccessor))
    //    //{
    //    //    jointsBuffer = jointsAccessor.AsVector4Array();
    //    //}

    //    //if (meshPrimitive.VertexAccessors.TryGetValue("WEIGHTS_0", out Accessor? weightsAccessor))
    //    //{
    //    //    weightsBuffer = weightsAccessor.AsVector4Array();
    //    //}

    //    // Load skinning data.
    //    var jointIndicesAccessor = meshPrimitive.GetVertexAccessor("JOINTS_0");
    //    var weightsAccessor = meshPrimitive.GetVertexAccessor("WEIGHTS_0");

    //    System.Numerics.Vector4[] jointIndices = null;
    //    System.Numerics.Vector4[] weights = null;

    //    //if (jointIndicesAccessor != null && weightsAccessor != null)
    //    //{
    //    //    jointIndices = LoadJointIndices(jointIndicesAccessor);
    //    //    weights = weightsAccessor.AsVector4Array().ToArray();
    //    //}

    //    for (uint i = 0; i < vertexCount; i++)
    //    {
    //        var position = positionBuffer != null ? positionBuffer[(int)i] : System.Numerics.Vector3.Zero;
    //        var normal = normalBuffer != null ? normalBuffer[(int)i] : System.Numerics.Vector3.Zero;
    //        var texCoord = texCoordBuffer != null ? texCoordBuffer[(int)i] : System.Numerics.Vector2.Zero;
    //        var tangent = tangentBuffer != null ? tangentBuffer[(int)i] : System.Numerics.Vector4.Zero;
    //        //var color = colorBuffer != null ? colorBuffer[(int)i] : Vector3.One;
    //        //int colorMapIndex = (int)_materials[primitive.Material.LogicalIndex].BaseColorTextureIndex;
    //        //int normalMapIndex = (int)_materials[primitive.Material.LogicalIndex].NormalTextureIndex;

    //        // Skinning data.
    //        var jointIndicesVec = jointIndices != null ? jointIndices[(int)i] : System.Numerics.Vector4.Zero;
    //        var weightsVec = weights != null ? weights[(int)i] : System.Numerics.Vector4.Zero;

    //        var vertex = new Vertex
    //        {
    //            Position = new System.Numerics.Vector3(position.X * scale, position.Y * scale, position.Z * scale),
    //            TexCoord = new System.Numerics.Vector2(texCoord.X, texCoord.Y),
    //            Normal = new System.Numerics.Vector3(normal.X, normal.Y, normal.Z),
    //            Tangent = new System.Numerics.Vector4(tangent.X, tangent.Y, tangent.Z, tangent.W),
    //            JointIndices = jointIndicesVec,
    //            Weights = weightsVec
    //        };

    //        //var vertex = new Vertex()
    //        //{
    //        //    Position = new System.Numerics.Vector3(position.X, position.Y, position.Z),
    //        //    TexCoord = new System.Numerics.Vector2(texCoord.X, texCoord.Y),
    //        //    Normal = normalBuffer != null ? new System.Numerics.Vector3(normal0.X, normal0.Y, normal0.Z) : System.Numerics.Vector3.UnitZ,
    //        //    Tangent = tangentBuffer != null ? new System.Numerics.Vector4(tangent.X, tangent.Y, tangent.Z, tangent.W) : System.Numerics.Vector4.UnitX,
    //        //    JointIndices = jointIndicesVec,
    //        //    Weights = weightsVec
    //        //    //Color = new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
    //        //    //Bitangent = Vector3.Cross(normal0, new Vector3(tangent.X, tangent.Y, -tangent.Z)) * tangent.W // Use W to determine handedness.
    //        //};
    //        //(vertices.Min(ve => ve.Normal.X) / 2 + 0.5) + ":" + (vertices.Max(ve => ve.Normal.X) / 2 + 0.5)
    //        //vertex.Normal = new Vector3(0.5f, 0.5f, 1.0f);

    //        vertices.Add(vertex);
    //    }

    //    if (meshPrimitive.IndexAccessor != null)
    //    {
    //        indexCount = meshPrimitive.IndexAccessor.Count;

    //        var indexBuffer = meshPrimitive.IndexAccessor.AsIndicesArray();

    //        for (int i = 0; i < indexCount; i++)
    //        {
    //            indices.Add((ushort)(indexBuffer[i] + vertexOffset));
    //        }
    //    }
    //    else
    //    {
    //        // Generate sequential indices when none are provided.
    //        for (ushort i = 0; i < vertices.Count; i++)
    //        {
    //            indices.Add(i);
    //        }
    //    }

    //    for (var i = 0; i < indices.Count; i = i + 3)
    //    {
    //        var tmp = indices[i];
    //        indices[i] = indices[i + 2];
    //        indices[i + 2] = tmp;

    //        //var three = indices[i + 2];
    //        //indices[i + 2] = indices[0];
    //        //indices[i] = three;
    //    }

    //    var doScale = false;

    //    if (doScale)
    //    {
    //        var minX = Math.Abs(vertices.Min(vert => vert.Position.X));
    //        var maxX = Math.Abs(vertices.Max(vert => vert.Position.X));

    //        var minY = Math.Abs(vertices.Min(vert => vert.Position.Y));
    //        var maxY = Math.Abs(vertices.Max(vert => vert.Position.Y));

    //        var minZ = Math.Abs(vertices.Min(vert => vert.Position.Z));
    //        var maxZ = Math.Abs(vertices.Max(vert => vert.Position.Z));

    //        var maxXYZ = new float[] { minX, maxY, minY, maxY, minZ, maxZ }.Max(); // Math.Max(Math.Max(maxX, maxY), maxZ);

    //        for (var i = 0; i < vertices.Count; i++)
    //        {
    //            var vert = vertices[i];

    //            vert.Position = new System.Numerics.Vector3(vert.Position.X / maxXYZ, vert.Position.Y / maxXYZ, vert.Position.Z / maxXYZ) * scale; // + movePos;

    //            vertices[i] = vert;
    //        }

    //        minX = vertices.Min(vert => vert.Position.X);
    //        maxX = vertices.Max(vert => vert.Position.X);

    //        minY = vertices.Min(vert => vert.Position.Y);
    //        maxY = vertices.Max(vert => vert.Position.Y);

    //        minZ = vertices.Min(vert => vert.Position.Z);
    //        maxZ = vertices.Max(vert => vert.Position.Z);
    //    }

    //    return (vertices, indices);
    //}

    private static System.Numerics.Vector4[] LoadJointIndices(Accessor jointIndicesAccessor)
    {
        if (jointIndicesAccessor == null)
            return new System.Numerics.Vector4[0];

        try
        {
            // SharpGLTF.AsVector4Array should handle all supported formats correctly.
            var vec4Array = jointIndicesAccessor.AsVector4Array();
            var result = vec4Array.Select(v => new System.Numerics.Vector4(
                (float)v.X, (float)v.Y, (float)v.Z, (float)v.W)).ToArray();
            return result;
        }
        catch (Exception ex)
        {
            return new System.Numerics.Vector4[0];
        }
    }

    static System.Numerics.Vector3[] ConvertMorphVector3Accessor(Accessor accessor, int vertexCount, bool flipZ)
    {
        var values = accessor.AsVector3Array();
        var result = new System.Numerics.Vector3[vertexCount];
        int count = Math.Min(vertexCount, accessor.Count);
        for (int i = 0; i < count; i++)
        {
            var value = values[i];
            result[i] = flipZ
                ? new System.Numerics.Vector3(value.X, value.Y, -value.Z)
                : value;
        }

        return result;
    }

    // Dedicated joint-index loading path because glTF may use different underlying data types.
    //private static System.Numerics.Vector4[] LoadJointIndices(Accessor jointIndicesAccessor)
    //{
    //    if (jointIndicesAccessor == null)
    //        return new System.Numerics.Vector4[0];

    //    try
    //    {
    //        // SharpGLTF handles type conversion automatically, so AsVector4Array can be used directly.
    //        var vec4Array = jointIndicesAccessor.AsVector4Array();
    //        return vec4Array.Select(v => new System.Numerics.Vector4(
    //            (float)v.X, (float)v.Y, (float)v.Z, (float)v.W)).ToArray();
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.WriteLine($"Error while loading joint indices (format: {jointIndicesAccessor.Format}): {ex.Message}");
    //        return new System.Numerics.Vector4[0];
    //    }
    //}

    //private static System.Numerics.Vector4[] LoadJointIndices(Accessor jointIndicesAccessor)
    //{
    //    if (jointIndicesAccessor == null)
    //        return new System.Numerics.Vector4[0];

    //    // Use AttributeFormat instead of EncodingType.
    //    var format = jointIndicesAccessor.Format;

    //    switch (format)
    //    {
    //        case AttributeFormat.VEC4:
    //            // For VEC4 format, read directly as a Vector4 array.
    //            var vec4Array = jointIndicesAccessor.AsVector4Array();
    //            return vec4Array.Select(v => new System.Numerics.Vector4(v.X, v.Y, v.Z, v.W)).ToArray();

    //        case AttributeFormat.VEC4_USHORT:
    //            // VEC4_USHORT needs specialized handling.
    //            var ushortArray = jointIndicesAccessor.AsVector4Array();
    //            return ushortArray.Select(v => new System.Numerics.Vector4(v.X, v.Y, v.Z, v.W)).ToArray();

    //        case AttributeFormat.VEC4_UBYTE:
    //            // VEC4_UBYTE needs specialized handling.
    //            var ubyteArray = jointIndicesAccessor.AsVector4Array();
    //            return ubyteArray.Select(v => new System.Numerics.Vector4(v.X, v.Y, v.Z, v.W)).ToArray();

    //        default:
    //            throw new NotSupportedException($"Joint indices format {format} is not supported.");
    //    }
    //}
}

public sealed class GLTFMaterial
{
    public System.Numerics.Vector4 BaseColorFactor { get; set; } = System.Numerics.Vector4.One;

    public int BaseColorIndex { get; set; } = -1;

    public int NormalIndex { get; set; } = -1;

    public int MetallicRoughnessIndex { get; set; } = -1; // Note: metallic and roughness are usually packed into the G and B channels of the same texture.

    public int OcclusionIndex { get; set; } = -1; // Optional.

    public int EmissiveIndex { get; set; } = -1;

    public string AlphaMode { get; set; } = "OPAQUE";

    public float AlphaCutoff { get; set; } = 0.5f;

    public bool DoubleSided { get; set; }

    public float MetallicFactor { get; set; } = 1.0f;

    public float RoughnessFactor { get; set; } = 1.0f;

    public System.Numerics.Vector3 EmissiveFactor { get; set; } = System.Numerics.Vector3.Zero;
}

//[StructLayout(LayoutKind.Sequential, Pack = 1)]
//public struct Vertex
//{
//    public System.Numerics.Vector3 Position;
//    public System.Numerics.Vector2 TexCoord;
//    public System.Numerics.Vector3 Normal;   // Added normal.
//    public System.Numerics.Vector4 Tangent;  // Added tangent.

//    // Added skinning data.
//    public System.Numerics.Vector4 Joints;  // Joint indices.
//    public System.Numerics.Vector4 Weights; // Weights.

//    // Add a Size property for buffer creation.
//    public static uint Size => (uint)Marshal.SizeOf<Vertex>();
//}
