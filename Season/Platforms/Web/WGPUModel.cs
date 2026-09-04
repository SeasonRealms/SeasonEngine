// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Platforms.Web;

internal class WGPUModel
{
    public string Name { get; }
    internal WebGltfAsset Asset => _asset;

    readonly WebGltfAsset _asset = new();

    bool _transformInitialized;
    // 2-3 Step C (mirroring VK's SetPrevBonesReady / SetPrevMorphReady): history for the bone palette and morph weights
    // has been ready for two consecutive frames. It stays false after the first Update and becomes true starting on the second frame,
    // at which point the JS-side previous-bone shadow copy must already exist
    // (uploadSkinnedBones advances it every frame), and PrevMorphWeights already contains the real values from the previous frame.
    bool _prevDeformReady;
    int _nextPrimitiveCacheIndex;
    bool _hasSkinning;
    string? _skinCacheKey;
    // Bone-matrix byte buffer (persistently reused and overwritten in place every frame).
    byte[] _boneMatricesBytes = Array.Empty<byte>();

    // Unified highlight: host bounds-box state (built lazily; extended by Wireframe shell in Phase 3).
    bool _boundsActive;
    WebBoundsBox? _boundsBox;

    // Unified highlight: host wireframe-shell state
    // (per-primitive shell boxes are built lazily on the first enabled frame; synchronized during Update and drawn at the end of Draw).
    bool _wireframeEnabled;
    List<WebShellBox?>? _wireframeBoxes;
    float _builtShellEdgeWidth;
    List<WGPUPrimitiveData>? _allPrimitives;

    // Unified highlight: host Outline2D state
    // (synchronized during Update and dispatched by RenderOutlineMask, mirroring DX/VK SetOutline2DState).
    bool _outline2DActive;
    Vector4 _outline2DColor;
    float _outline2DWidth;

    internal bool Outline2DActive => _outline2DActive;
    internal Vector4 Outline2DMaskColor => _outline2DColor;
    internal float Outline2DMaskWidth => _outline2DWidth;

    public WGPUModel(string name)
    {
        Name = name;
        _asset.CreateGLTFNodeCallback = CreateGLTFNode;
        _asset.ProcessPrimitiveCallback = ProcessPrimitive;
    }

    public void SetGlbBytes(byte[] bytes) { _asset.SetGlbBytes(bytes); }

    public void Load(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        // Animation queries and switching belong to the glTF parsing domain rather than IGraphics,
        // so the direct-load path injects the asset reference into the control.
        model.Asset = _asset;

        _asset.Load(model, camera);
    }

    /// <summary>Shared-template instancing: clones the node tree.
    /// PickMesh is immutable after loading, so it is shared by reference and keeps the picking-data chain
    /// consistent with the direct-load path, see <see cref="EnsureClonedNode"/>.
    /// No per-instance picking shadow is built (InstancePickNodeWorlds / Bones remain empty):
    /// static hosts are picked exactly through the <c>hasShadow</c> fallback path in InstancedModel.TryPickInstanceSurface,
    /// while animated hosts remain approximate, which is a documented boundary already described in the shared-layer comments.</summary>
    public WGPUModel CreateInstance(Season.Controls.Model model)
    {
        if (_asset.Model != null)
        {
            model.Size = _asset.Model.Size;
            model.OriginalScale = _asset.Model.OriginalScale;
            // 1-3: on the shared-template path, WebGltfAsset.Load fills LocalBounds only on the temporary template Model.
            // It must be copied back to the user control, otherwise control-level culling will never activate because of the empty-box guard.
            model.LocalBounds = _asset.Model.LocalBounds;
            // Unified transform/bounds pattern: copy the raw bounds back as well
            // (the setter triggers OnBoundsEstablished to finalize the default size, so this must happen after Size/OriginalScale).
            model.LocalBoundsRaw = _asset.Model.LocalBoundsRaw;
            // 1-2: likewise copy back the imported KHR punctual lights
            // (they are local-space read-only data, so sharing the reference is fine),
            // otherwise AppendWorldLights would see an empty list on the shared-template path.
            model.ImportedPunctualLights = _asset.Model.ImportedPunctualLights;
        }

        var instance = new WGPUModel(Name);
        instance._transformInitialized = false;
        instance._prevDeformReady = false;
        instance._nextPrimitiveCacheIndex = _nextPrimitiveCacheIndex;
        instance._hasSkinning = _hasSkinning;
        instance._skinCacheKey = $"SKIN:{Name}:{model.ID}";
        instance._asset.Model = model;
        instance._asset._nodeTransforms = new Dictionary<GltfNodeBase, Matrix4x4>();

        var nodeMap = new Dictionary<GltfNodeBase, GltfNodeBase>();
        foreach (var nodeBase in _asset.gltfNodes)
            EnsureClonedNode(nodeMap, nodeBase, model);

        var skinMap = new Dictionary<GLTFSkin, GLTFSkin>();
        foreach (var sourceSkin in _asset.GetAllSkins())
        {
            skinMap[sourceSkin] = new GLTFSkin
            {
                Name = sourceSkin.Name,
                InverseBindMatrices = new List<Matrix4x4>(sourceSkin.InverseBindMatrices),
                Joints = new List<GltfNodeBase>(),
            };

            foreach (var joint in sourceSkin.Joints)
                EnsureClonedNode(nodeMap, joint, model);

            if (sourceSkin.SkeletonRoot != null)
                EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model);

            if (sourceSkin.BindNode != null)
                EnsureClonedNode(nodeMap, sourceSkin.BindNode, model);
        }

        foreach (var animation in _asset._animations)
        {
            foreach (var channel in animation.Channels)
            {
                if (channel.Target?.Node != null)
                    EnsureClonedNode(nodeMap, channel.Target.Node, model);
            }
        }

        foreach (var nodeBase in _asset.gltfNodes)
        {
            var sourceNode = nodeBase;
            var clonedNode = nodeMap[sourceNode];
            clonedNode.Children = sourceNode.Children.Select(child => EnsureClonedNode(nodeMap, child, model)).ToList();

            if (sourceNode.Skin != null && skinMap.TryGetValue(sourceNode.Skin, out var clonedSkin))
                clonedNode.Skin = clonedSkin;
        }

        foreach (var pair in skinMap)
        {
            var sourceSkin = pair.Key;
            var clonedSkin = pair.Value;
            clonedSkin.Joints = sourceSkin.Joints.Select(joint => EnsureClonedNode(nodeMap, joint, model)).ToList();
            clonedSkin.SkeletonRoot = sourceSkin.SkeletonRoot != null ? EnsureClonedNode(nodeMap, sourceSkin.SkeletonRoot, model) : null;
            clonedSkin.BindNode = sourceSkin.BindNode != null ? EnsureClonedNode(nodeMap, sourceSkin.BindNode, model) : null;
        }

        instance._asset.gltfNodes = _asset.gltfNodes.Select(node => nodeMap[node]).ToList();
        instance._asset._nodeMap = _asset._nodeMap.ToDictionary(kvp => kvp.Key, kvp => nodeMap[kvp.Value]);
        instance._asset._animations = CloneAnimations(_asset._animations, nodeMap);
        instance._asset._animationPlayer = new GLTFAnimationPlayer();
        instance._asset._animationPlayer.Initialize(instance._asset._animations);
        // Shared-template instancing path: inject the instance asset into the control
        // with the same semantics used by the direct-load path in Load.
        model.Asset = instance._asset;

        return instance;
    }

    static GltfNodeBase EnsureClonedNode(Dictionary<GltfNodeBase, GltfNodeBase> nodeMap, GltfNodeBase sourceNode, Season.Controls.Model model)
    {
        if (nodeMap.TryGetValue(sourceNode, out var existing))
            return existing;

        GltfNodeBase clonedNode;
        if (sourceNode is WGPUGLTFNode sourceWebNode)
        {
            var webNode = new WGPUGLTFNode
            {
                Name = sourceWebNode.Name,
                LogicalIndex = sourceWebNode.LogicalIndex,
                Mesh = sourceWebNode.Mesh,
                IsJoint = sourceWebNode.IsJoint,
                JointIndex = sourceWebNode.JointIndex,
                Translation = sourceWebNode.InitialTranslation,
                Rotation = sourceWebNode.InitialRotation,
                Scale = sourceWebNode.InitialScale,
                InitialTranslation = sourceWebNode.InitialTranslation,
                InitialRotation = sourceWebNode.InitialRotation,
                InitialScale = sourceWebNode.InitialScale,
                InitialWeights = sourceWebNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceWebNode.InitialWeights.Clone(),
                Weights = sourceWebNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceWebNode.Weights.Clone(),
                WeightsVersion = sourceWebNode.WeightsVersion,
                WorldTransform = sourceWebNode.WorldTransform,
                // v2 picking: PickMesh is immutable after loading, so it is shared by reference
                // (no deep copy; NodeIndex stays consistent on both sides).
                PickMeshes = sourceNode.PickMeshes,
            };

            foreach (var primitive in sourceWebNode.Primitives)
                webNode.Primitives.Add(ClonePrimitiveData(primitive, webNode, model));

            clonedNode = webNode;
        }
        else
        {
            clonedNode = new GltfNodeBase
            {
                Name = sourceNode.Name,
                LogicalIndex = sourceNode.LogicalIndex,
                Mesh = sourceNode.Mesh,
                IsJoint = sourceNode.IsJoint,
                JointIndex = sourceNode.JointIndex,
                Translation = sourceNode.InitialTranslation,
                Rotation = sourceNode.InitialRotation,
                Scale = sourceNode.InitialScale,
                InitialTranslation = sourceNode.InitialTranslation,
                InitialRotation = sourceNode.InitialRotation,
                InitialScale = sourceNode.InitialScale,
                InitialWeights = sourceNode.InitialWeights.Length == 0 ? Array.Empty<float>() : (float[])sourceNode.InitialWeights.Clone(),
                Weights = sourceNode.Weights.Length == 0 ? Array.Empty<float>() : (float[])sourceNode.Weights.Clone(),
                WeightsVersion = sourceNode.WeightsVersion,
                WorldTransform = sourceNode.WorldTransform,
                // v2 picking: PickMesh is immutable after loading, so it is shared by reference
                // (no deep copy; NodeIndex stays consistent on both sides).
                PickMeshes = sourceNode.PickMeshes,
            };
        }

        nodeMap[sourceNode] = clonedNode;
        return clonedNode;
    }

    static WGPUPrimitiveData ClonePrimitiveData(WGPUPrimitiveData source, WGPUGLTFNode ownerNode, Season.Controls.Model model)
    {
        var clone = new WGPUPrimitiveData
        {
            BaseVertices = source.BaseVertices != null ? (Vertex[])source.BaseVertices.Clone() : null,
            MorphTargets = source.MorphTargets != null ? new List<GLTFMorphTarget>(source.MorphTargets) : null,
            MorphDeltasBytes = source.MorphDeltasBytes.Length == 0 ? Array.Empty<byte>() : (byte[])source.MorphDeltasBytes.Clone(),
            MorphTargetCount = source.MorphTargetCount,
            MorphVertexCount = source.MorphVertexCount,
            OwnerNode = ownerNode,
            LastAppliedWeightsVersion = ownerNode.WeightsVersion,
            LocalBoundsCenter = source.LocalBoundsCenter,
            LocalBoundsExtents = source.LocalBoundsExtents,
            VertexData = (float[])source.VertexData.Clone(),
            VertexBytes = (byte[])source.VertexBytes.Clone(),
            IndexData = (uint[])source.IndexData.Clone(),
            IndexBytes = (byte[])source.IndexBytes.Clone(),
            Use32BitIndices = source.Use32BitIndices,
            VertexStrideFloats = source.VertexStrideFloats,
            HasSkinning = source.HasSkinning,
            SourceBaseColor = source.SourceBaseColor,
            BaseColor = source.BaseColor,
            OriginalBaseColorAlpha = source.OriginalBaseColorAlpha,
            AlphaCutoff = source.AlphaCutoff,
            AlphaMode = source.AlphaMode,
            IsTransparent = source.IsTransparent,
            RenderMode = source.RenderMode,
            DoubleSided = source.DoubleSided,
            MetallicFactor = source.MetallicFactor,
            RoughnessFactor = source.RoughnessFactor,
            EmissiveFactor = source.EmissiveFactor,
            BaseColorTextureName = source.BaseColorTextureName,
            NormalTextureName = source.NormalTextureName,
            MetallicRoughnessTextureName = source.MetallicRoughnessTextureName,
            OcclusionTextureName = source.OcclusionTextureName,
            EmissiveTextureName = source.EmissiveTextureName,
            BaseColorTexture = source.BaseColorTexture,
            NormalTexture = source.NormalTexture,
            MetallicRoughnessTexture = source.MetallicRoughnessTexture,
            OcclusionTexture = source.OcclusionTexture,
            EmissiveTexture = source.EmissiveTexture,
            // Morph weights have moved into uniforms for GPU blending, so the vertex buffer is no longer rewritten on the CPU.
            // Clones therefore share the same source-geometry cache as non-morph primitives.
            CacheKey = source.CacheKey,
            Uploaded = source.Uploaded,
            LastTextureName = source.LastTextureName,
            LastNormalTextureName = source.LastNormalTextureName,
            LastMRTextureName = source.LastMRTextureName,
            LastAOTextureName = source.LastAOTextureName,
            LastEmissiveTextureName = source.LastEmissiveTextureName,
            CurrentAlphaCutoff = source.AlphaCutoff,
            CurrentAlpha = source.CurrentAlpha,
            GeometryDirty = false,
        };

        ApplyInstanceMaterialOverrides(clone, model);
        return clone;
    }

    static List<GLTFAnimation> CloneAnimations(List<GLTFAnimation> sourceAnimations, Dictionary<GltfNodeBase, GltfNodeBase> nodeMap)
    {
        var result = new List<GLTFAnimation>(sourceAnimations.Count);
        foreach (var sourceAnimation in sourceAnimations)
        {
            var clonedAnimation = new GLTFAnimation
            {
                Name = sourceAnimation.Name,
            };

            foreach (var sourceChannel in sourceAnimation.Channels)
            {
                var clonedSampler = new AnimationSampler
                {
                    Inputs = new List<float>(sourceChannel.Sampler?.Inputs ?? new List<float>()),
                    Values = new List<float>(sourceChannel.Sampler?.Values ?? new List<float>()),
                    InTangents = sourceChannel.Sampler?.InTangents != null ? new List<float>(sourceChannel.Sampler.InTangents) : null,
                    OutTangents = sourceChannel.Sampler?.OutTangents != null ? new List<float>(sourceChannel.Sampler.OutTangents) : null,
                    OutputElementCount = sourceChannel.Sampler?.OutputElementCount ?? 4,
                    Interpolation = sourceChannel.Sampler?.Interpolation ?? AnimationInterpolationMode.Linear,
                };

                var clonedChannel = new AnimationChannel
                {
                    Sampler = clonedSampler,
                    Target = sourceChannel.Target == null
                        ? null
                        : new AnimationChannelTarget
                        {
                            Node = sourceChannel.Target.Node != null ? nodeMap[sourceChannel.Target.Node] : null,
                            Path = sourceChannel.Target.Path,
                        }
                };

                clonedAnimation.Samplers.Add(clonedSampler);
                if (clonedChannel.Target != null)
                    clonedAnimation.Channels.Add(clonedChannel);
            }

            result.Add(clonedAnimation);
        }

        return result;
    }

    static void ApplyInstanceMaterialOverrides(WGPUPrimitiveData primData, Season.Controls.Model model)
    {
        var colorTint = model.MaterialColor ?? Vector4.One;
        primData.BaseColor = primData.SourceBaseColor * colorTint;
        primData.OriginalBaseColorAlpha = primData.BaseColor.W;
        primData.RenderMode = model.Unlit ? 0u : 1u;
        primData.CurrentAlpha = primData.OriginalBaseColorAlpha * model.Alpha;
        primData.CurrentAlphaCutoff = primData.AlphaMode == 1u
            ? primData.AlphaCutoff * model.Alpha
            : primData.AlphaCutoff;
    }

    WGPUGLTFNode CreateGLTFNode(SharpGLTF.Schema2.Node node)
    {
        return new WGPUGLTFNode
        {
            Name = node.Name ?? $"Node_{node.LogicalIndex}",
            LogicalIndex = node.LogicalIndex,
            Mesh = node.Mesh,
            Skin = node.Skin != null ? _asset.CreateSkin(node.Skin) : null,
            IsJoint = node.IsSkinJoint,
            JointIndex = node.IsSkinJoint ? _asset.GetJointIndex(node) : -1
        };
    }


    void ProcessPrimitive(SharpGLTF.Schema2.MeshPrimitive meshPrimitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var primData = CreatePrimitiveData(meshPrimitive, node, model, camera);
        var gltfNode = (WGPUGLTFNode)node;
        gltfNode.Primitives.Add(primData);
    }

    WGPUPrimitiveData CreatePrimitiveData(SharpGLTF.Schema2.MeshPrimitive primitive, GltfNodeBase node, Season.Controls.Model model, Season.Basic.Camera camera)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var primData = new WGPUPrimitiveData();

        var (vertices, indices) = GLTFTools.LoadMeshPrimitive(primitive);
        var morphTargets = GLTFTools.LoadMorphTargets(primitive, vertices.Count);
        primData.OwnerNode = node;
        primData.LastAppliedWeightsVersion = node.WeightsVersion;
        var localBounds = Season.Rendering.Bounds3D.FromVertices(vertices);
        primData.LocalBoundsCenter = localBounds.Center;
        primData.LocalBoundsExtents = localBounds.Extents;
        if (morphTargets.Count > 0)
        {
            primData.BaseVertices = vertices.ToArray();
            primData.MorphTargets = morphTargets;
            primData.MorphTargetCount = (uint)Math.Min(morphTargets.Count, 4);
            primData.MorphVertexCount = (uint)vertices.Count;
            primData.MorphDeltasBytes = CreateMorphDeltaBytes(primData.BaseVertices, morphTargets);
        }

        bool hasSkinning = false;
        int nonZeroWeightVertices = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            if (vertices[i].Weights != Vector4.Zero)
            {
                nonZeroWeightVertices++;
                hasSkinning = true;
            }
        }

        primData.HasSkinning = hasSkinning;
        primData.VertexStrideFloats = 20;
        if (hasSkinning)
        {
            _hasSkinning = true;
        }

        // The vertex buffer always uploads the base pose: morph deltas are accumulated in the VS using uniform weights (bit 64),
        // so baking initial weights here would make the GPU apply them twice.
        UpdatePrimitiveVertexPayload(primData, vertices);

        var indexData = indices.ToArray();
        primData.IndexData = indexData;
        primData.Use32BitIndices = indexData.Any(i => i > ushort.MaxValue);
        if (primData.Use32BitIndices)
        {
            primData.IndexBytes = Graphics.ToByteArray(indexData);
        }
        else
        {
            var indexData16 = new ushort[indexData.Length];
            for (int i = 0; i < indexData.Length; i++)
                indexData16[i] = (ushort)indexData[i];
            primData.IndexBytes = Graphics.ToByteArray(indexData16);
        }
        primData.CacheKey = $"MDL:{Name}:{_nextPrimitiveCacheIndex++}";

        ProcessMaterial(primitive, primData);

        return primData;
    }

    internal static void UpdatePrimitiveVertexPayload(WGPUPrimitiveData primData, IReadOnlyList<Vertex> vertices)
    {
        var vertexData = new float[vertices.Count * primData.VertexStrideFloats];
        for (int i = 0; i < vertices.Count; i++)
        {
            int off = i * primData.VertexStrideFloats;
            vertexData[off + 0] = vertices[i].Position.X;
            vertexData[off + 1] = vertices[i].Position.Y;
            vertexData[off + 2] = vertices[i].Position.Z;
            vertexData[off + 3] = vertices[i].TexCoord.X;
            vertexData[off + 4] = vertices[i].TexCoord.Y;
            vertexData[off + 5] = vertices[i].Normal.X;
            vertexData[off + 6] = vertices[i].Normal.Y;
            vertexData[off + 7] = vertices[i].Normal.Z;
            vertexData[off + 8] = vertices[i].Tangent.X;
            vertexData[off + 9] = vertices[i].Tangent.Y;
            vertexData[off + 10] = vertices[i].Tangent.Z;
            vertexData[off + 11] = vertices[i].Tangent.W;
            vertexData[off + 12] = vertices[i].Joints.X;
            vertexData[off + 13] = vertices[i].Joints.Y;
            vertexData[off + 14] = vertices[i].Joints.Z;
            vertexData[off + 15] = vertices[i].Joints.W;
            vertexData[off + 16] = vertices[i].Weights.X;
            vertexData[off + 17] = vertices[i].Weights.Y;
            vertexData[off + 18] = vertices[i].Weights.Z;
            vertexData[off + 19] = vertices[i].Weights.W;
        }

        primData.VertexData = vertexData;
        primData.VertexBytes = Graphics.ToByteArray(vertexData);
    }

    /// <summary>Unified highlight (wireframe shell): reconstructs the source vertex list from the 20-float vertex payload.
    /// Morph primitives use BaseVertices directly. Non-morph primitives do not keep a resident CPU-side <c>Vertex[]</c>,
    /// so the method rebuilds it field by field from the payload, with a lossless one-to-one mapping to the payload layout.
    /// This is used only by the lazy shell-build path and therefore runs once on the first frame where wireframe is enabled.</summary>
    internal static List<Vertex> ReconstructVertices(WGPUPrimitiveData prim)
    {
        if (prim.BaseVertices != null)
            return new List<Vertex>(prim.BaseVertices);

        var data = prim.VertexData;
        int count = data.Length / 20;
        var vertices = new List<Vertex>(count);
        for (int i = 0; i < count; i++)
        {
            int off = i * 20;
            vertices.Add(new Vertex
            {
                Position = new Vector3(data[off], data[off + 1], data[off + 2]),
                TexCoord = new Vector2(data[off + 3], data[off + 4]),
                Normal = new Vector3(data[off + 5], data[off + 6], data[off + 7]),
                Tangent = new Vector4(data[off + 8], data[off + 9], data[off + 10], data[off + 11]),
                Joints = new Vector4(data[off + 12], data[off + 13], data[off + 14], data[off + 15]),
                Weights = new Vector4(data[off + 16], data[off + 17], data[off + 18], data[off + 19]),
            });
        }
        return vertices;
    }

    static byte[] CreateMorphDeltaBytes(Vertex[] baseVertices, List<GLTFMorphTarget> morphTargets)
    {
        int targetCount = Math.Min(morphTargets.Count, 4);
        int vertexCount = baseVertices.Length;
        var deltaData = new float[targetCount * vertexCount * 9];

        for (int t = 0; t < targetCount; t++)
        {
            var target = morphTargets[t];
            for (int v = 0; v < vertexCount; v++)
            {
                int baseIdx = (t * vertexCount + v) * 9;
                if (v < target.PositionDeltas.Length)
                {
                    deltaData[baseIdx] = target.PositionDeltas[v].X;
                    deltaData[baseIdx + 1] = target.PositionDeltas[v].Y;
                    deltaData[baseIdx + 2] = target.PositionDeltas[v].Z;
                }

                if (v < target.NormalDeltas.Length)
                {
                    deltaData[baseIdx + 3] = target.NormalDeltas[v].X;
                    deltaData[baseIdx + 4] = target.NormalDeltas[v].Y;
                    deltaData[baseIdx + 5] = target.NormalDeltas[v].Z;
                }

                if (v < target.TangentDeltas.Length)
                {
                    deltaData[baseIdx + 6] = target.TangentDeltas[v].X;
                    deltaData[baseIdx + 7] = target.TangentDeltas[v].Y;
                    deltaData[baseIdx + 8] = target.TangentDeltas[v].Z;
                }
            }
        }

        return Graphics.ToByteArray(deltaData);
    }

    // 1-3: bounding-box computation is consolidated into the shared Season.Rendering.Bounds3D.FromVertices path (common across all four backends).

    // Morph vertex blending has been moved entirely into the GPU vertex shader
    // (uniform morphWeights + bit 64), and the CPU-side reverse blending path
    // (ApplyMorphTargetsToPrimitive / IfNeeded) has been removed.

    void ProcessMaterial(SharpGLTF.Schema2.MeshPrimitive primitive, WGPUPrimitiveData primData)
    {
        var modelRoot = primitive.LogicalParent.LogicalParent;
        var (gltfMaterial, images) = GLTFTools.LoadMaterial(modelRoot, primitive);

        if (gltfMaterial != null)
        {
            primData.IsTransparent = gltfMaterial.AlphaMode == "BLEND";
            primData.AlphaMode = gltfMaterial.AlphaMode switch
            {
                "MASK" => 1u,
                "BLEND" => 2u,
                _ => 0u
            };
            primData.AlphaCutoff = gltfMaterial.AlphaCutoff;
        }
        else
        {
            primData.IsTransparent = false;
            primData.AlphaMode = 0u;
            primData.AlphaCutoff = 0.5f;
        }

        primData.SourceBaseColor = gltfMaterial?.BaseColorFactor ?? Vector4.One;
        ApplyInstanceMaterialOverrides(primData, _asset.Model);
        primData.DoubleSided = gltfMaterial?.DoubleSided ?? false;

        if (gltfMaterial != null)
        {
            primData.MetallicFactor = gltfMaterial.MetallicFactor;
            primData.RoughnessFactor = gltfMaterial.RoughnessFactor;
            primData.EmissiveFactor = gltfMaterial.EmissiveFactor;
        }

        if (images.Count > 0)
        {
            var baseColorImage = images[0];
            if (baseColorImage != null)
            {
                primData.BaseColorTextureName = $"{_asset.Model.Name}-baseColor-{baseColorImage.LogicalIndex}";
                primData.BaseColorTexture = baseColorImage;
            }

            var normalImage = images[1];
            if (normalImage != null)
            {
                primData.NormalTextureName = $"{_asset.Model.Name}-normal-{normalImage.LogicalIndex}";
                primData.NormalTexture = normalImage;
            }

            var metallicRoughnessImage = images[2];
            if (metallicRoughnessImage != null)
            {
                primData.MetallicRoughnessTextureName = $"{_asset.Model.Name}-metallicRoughness-{metallicRoughnessImage.LogicalIndex}";
                primData.MetallicRoughnessTexture = metallicRoughnessImage;
            }

            var occlusionImage = images[3];
            if (occlusionImage != null)
            {
                primData.OcclusionTextureName = $"{_asset.Model.Name}-occlusion-{occlusionImage.LogicalIndex}";
                primData.OcclusionTexture = occlusionImage;
            }

            var emissiveImage = images[4];
            if (emissiveImage != null)
            {
                primData.EmissiveTextureName = $"{_asset.Model.Name}-emissive-{emissiveImage.LogicalIndex}";
                primData.EmissiveTexture = emissiveImage;
            }
        }

    }

    public List<WGPUPrimitiveData> GetAllPrimitives()
    {
        var result = new List<WGPUPrimitiveData>();
        CollectPrimitivesRecursive(_asset.gltfNodes, result);
        return result;
    }

    void CollectPrimitivesRecursive(List<GltfNodeBase> nodes, List<WGPUPrimitiveData> result)
    {
        foreach (var nodeBase in nodes)
        {
            if (nodeBase is WGPUGLTFNode node)
                result.AddRange(node.Primitives);
            CollectPrimitivesRecursive(nodeBase.Children, result);
        }
    }


    public bool TransformInitialized => _transformInitialized;

    /// <summary>2-3 Step C: deformation history (bone palette + morph weights) has been ready for two consecutive frames.</summary>
    public bool PrevDeformReady => _prevDeformReady;

    public void Update(Season.Controls.Model model, float time, Season.Basic.Camera camera)
    {
        // Mirroring VK: being initialized at the start of this Update means the previous frame already produced
        // a complete set of bone and morph data.
        bool wasInitialized = _transformInitialized;
        _asset._animationPlayer.Update(time, _asset.gltfNodes);
        if (_hasSkinning)
        {
            _asset._animationPlayer.UpdateBoneMatrices(_asset.GetAllSkins());
            // Reinterpret the shared buffer directly as bytes and write it into the persistent buffer
            // to avoid per-frame ToArray/ToByteArray allocations.
            var boneMatrices = _asset._animationPlayer.GetBoneMatricesArray();
            int byteLength = boneMatrices.Length * Unsafe.SizeOf<Matrix4x4>();
            if (_boneMatricesBytes.Length != byteLength)
                _boneMatricesBytes = new byte[byteLength];
            MemoryMarshal.AsBytes(boneMatrices.AsSpan()).CopyTo(_boneMatricesBytes);
        }

        // Unified transform pattern: converge on BuildWorldMatrix (anchor-pivot semantics, see Mesh3DBase).
        var userTransform = model.BuildWorldMatrix();

        // The player caches root nodes by list reference to avoid O(N^2) lookups every frame.
        var rootNodes = _asset._animationPlayer.GetRootNodes(_asset.gltfNodes);

        foreach (var rootNode in rootNodes)
            ApplyUserTransformToNodeTree(rootNode, userTransform, camera);

        _transformInitialized = true;
        _prevDeformReady = wasInitialized;

        SyncAlpha(model.Alpha);

        // Unified highlight: synchronize the bounds box
        // (box geometry is built lazily on the first enabled frame; face/edge colors are independent from the model alpha chain and are written every frame;
        // highlighting stays off when extents are near zero, meaning unloaded or degenerate bounds).
        _boundsActive = model.Highlight.Bounds;
        if (_boundsActive)
        {
            var bounds = model.GetWorldBoundsRaw();
            if (bounds.Extents.LengthSquared() >= 1e-12f)
            {
                _boundsBox ??= WebBoundsBox.Create($"{Name}:{model.ID}:HOST");
                _boundsBox.PrevWorld = _boundsBox.World;
                _boundsBox.World = Matrix4x4.CreateScale(bounds.Extents * 2f) * Matrix4x4.CreateTranslation(bounds.Center);
                _boundsBox.FaceColor = model.Highlight.SurfaceColor;
                _boundsBox.FaceAlpha = model.Highlight.SurfaceColor.W;
                _boundsBox.EdgeColor = model.Highlight.EdgeColor;
            }
        }

        // Unified highlight: synchronize wireframe shells
        // (per-primitive shell boxes are built lazily on the first enabled frame, and rebuilt fully when edge width changes;
        // shell world = node world x user transform, sharing the same source as per-primitive World and matching native WriteHighlightBox's
        // nodeWorld * userTransform; morph shell weights are synchronized every frame; face/edge colors are independent from the model alpha chain
        // and written every frame).
        _wireframeEnabled = model.Highlight.Wireframe;
        if (_wireframeEnabled)
        {
            EnsureWireframeHighlights(model.Highlight.EdgeWidth,
                MathF.Max(model.LocalSize.X, MathF.Max(model.LocalSize.Y, model.LocalSize.Z)));
            if (_wireframeBoxes != null)
            {
                for (int i = 0; i < _wireframeBoxes.Count; i++)
                {
                    var shell = _wireframeBoxes[i];
                    if (shell == null)
                        continue;
                    shell.PrevWorld = shell.World;
                    shell.World = _allPrimitives![i].World;
                    shell.FaceColor = model.Highlight.SurfaceColor;
                    shell.FaceAlpha = model.Highlight.SurfaceColor.W;
                    shell.EdgeColor = model.Highlight.EdgeColor;
                }
            }
        }

        // Unified highlight (Outline2D): pass host-level state straight through
        // (color/width are frozen during Update; mask rendering goes through RenderOutlineMask, mirroring the Update hook on DX/VK/Metal).
        _outline2DActive = model.Highlight.Outline;
        _outline2DColor = model.Highlight.OutlineColor;
        _outline2DWidth = model.Highlight.OutlineWidth;
    }

    void ApplyUserTransformToNodeTree(GltfNodeBase nodeBase, Matrix4x4 userTransform, Season.Basic.Camera camera)
    {
        var finalWorldMatrix = nodeBase.WorldTransform * userTransform;

        var node = nodeBase as WGPUGLTFNode;
        if (node != null)
        {
            foreach (var primitive in node.Primitives)
            {
                // 2-3 contract clause 6: roll the shadow copy forward before overwriting the current-frame world matrix
                // (exactly once per primitive per frame, in the same order as ApplyUserTransformToNodeTree on DX/VK/Metal).
                // On the first frame, _transformInitialized is still false, so PrevWorldMatrix remains the all-zero sentinel
                // instead of accidentally treating the default Identity matrix as history.
                // 2-3 Step C: the morph-weight shadow copy advances at the same point and in the same order.
                // Draw runs twice per frame (main pass + shadow pass), so advancing it there would move history twice per frame.
                if (_transformInitialized)
                {
                    primitive.PrevWorldMatrix = primitive.World;
                    primitive.PrevMorphWeights = primitive.MorphWeights;
                }
                primitive.World = finalWorldMatrix;
                primitive.MorphWeights = ExtractMorphWeights(primitive.OwnerNode);
                primitive.View = camera.View;
                primitive.Projection = camera.Projection;
            }

        }

        foreach (var child in nodeBase.Children)
            ApplyUserTransformToNodeTree(child, userTransform, camera);
    }

    // Non-instanced morph path: take the first 4 node weights
    // (the VS accumulates them under bit 64, matching native MaterialParams.MorphWeights).
    // 2-3 Step C: this helper is shared by shadow-copy advancement and both draw paths so that all three sites use identical values.
    static Vector4 ExtractMorphWeights(GltfNodeBase? node)
    {
        if (node == null || node.Weights.Length == 0)
            return Vector4.Zero;

        var weights = node.Weights;
        return new Vector4(
            weights.Length > 0 ? weights[0] : 0f,
            weights.Length > 1 ? weights[1] : 0f,
            weights.Length > 2 ? weights[2] : 0f,
            weights.Length > 3 ? weights[3] : 0f);
    }

    void SyncAlpha(float modelAlpha)
    {
        foreach (var prim in GetAllPrimitives())
        {
            float finalAlpha = prim.OriginalBaseColorAlpha * modelAlpha;
            prim.CurrentAlpha = finalAlpha;

            if (prim.AlphaMode == 1u)
                prim.CurrentAlphaCutoff = prim.AlphaCutoff * modelAlpha;
        }
    }

    /// <summary>Unified highlight (wireframe shell): lazily builds runtime per-primitive shell boxes for the non-instanced path.
    /// On the first frame where wireframe is enabled, every primitive gets its own shell in primitive order.
    /// Invalid-triangle or degenerate primitives are represented by null placeholders.
    /// When wireframe is fully disabled, memory usage stays at zero; once built, the shells stay resident and are neither rebuilt nor released
    /// just because wireframe is toggled on and off at runtime.
    /// Skinned primitives are still built so shell vertices carry joints and weights and follow animation exactly through the skinning path.
    /// Morph primitives also build shells and carry deltas expanded to the shell-vertex layout, with weights synchronized from the source every frame.
    /// <c>edgeWidth</c> comes from host Highlight.EdgeWidth (scaled relative to model size), and <c>localSizeMax</c> is the largest local dimension
    /// of the host model (the scaling baseline), so the per-primitive baked local thickness is
    /// <c>h = edgeWidth x localSizeMax / nodeScale</c> (see HighlightGeometry.NodeScaleOf).
    /// When these values no longer match the host state, the shells are released and rebuilt immediately in the same frame.</summary>
    void EnsureWireframeHighlights(float edgeWidth, float localSizeMax)
    {
        if (_wireframeBoxes != null)
        {
            if (_builtShellEdgeWidth == edgeWidth)
                return;
            // Edge width changed: invalidate the old shell geometry
            // (JS-side GPU resources are reclaimed by GC) and rebuild with the new width immediately in this frame.
            _wireframeBoxes = null;
        }
        _allPrimitives ??= GetAllPrimitives();
        if (_allPrimitives.Count == 0)
            return;
        _wireframeBoxes = new List<WebShellBox?>(_allPrimitives.Count);
        for (int i = 0; i < _allPrimitives.Count; i++)
        {
            var prim = _allPrimitives[i];
            _wireframeBoxes.Add(prim.VertexData != null && prim.IndexData != null && prim.VertexData.Length > 0 && prim.IndexData.Length >= 3
                ? WebShellBox.Create($"{Name}:HOST:{i}",
                    ReconstructVertices(prim), prim.IndexData,
                    HighlightGeometry.ComputeShellThickness(edgeWidth, localSizeMax, prim.OwnerNode),
                    prim.BaseVertices, prim.MorphTargets, prim.HasSkinning)
                : null);
        }
        _builtShellEdgeWidth = edgeWidth;
    }


    public void Draw(Graphics graphics)
    {
        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.BeginSkinnedModelDraw(_skinCacheKey, _boneMatricesBytes);

        DrawNodeTreeRecursive(_asset.gltfNodes, graphics);

        // Unified highlight (host bounds box): faces use translucent BLEND and edges use OPAQUE with depth writes.
        // It is finalized after all surfaces. The box uses the non-skinned path, so skinned batches are flushed first to preserve submission order,
        // matching the non-skinned branch in DrawPrimitive.
        if (_boundsActive && _boundsBox != null)
        {
            graphics.FlushDrawSkinnedMeshBatch();
            graphics.DrawBoundsBox(_boundsBox, Graphics.Camera3D.View, Graphics.Camera3D.Projection);
        }

        // Unified highlight (wireframe shell): per-primitive shell boxes with translucent BLEND faces and OPAQUE depth-writing edges.
        // They are finalized after all surfaces; skinned shells go through the skinned batch, while rigid shells go through the normal batch
        // (DrawShellBox flushes the opposite batch internally depending on the path).
        if (_wireframeEnabled && _wireframeBoxes != null)
        {
            for (int i = 0; i < _wireframeBoxes.Count; i++)
            {
                var shell = _wireframeBoxes[i];
                if (shell != null)
                {
                    Vector4? morphWeights = shell.MorphTargetCount > 0 ? _allPrimitives![i].MorphWeights : null;
                    graphics.DrawShellBox(shell, Graphics.Camera3D.View, Graphics.Camera3D.Projection, morphWeights);
                }
            }
        }

        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.EndSkinnedModelDraw();
    }

    void DrawNodeTreeRecursive(List<GltfNodeBase> nodes, Graphics graphics)
    {
        foreach (var nodeBase in nodes)
        {
            var node = nodeBase as WGPUGLTFNode;
            if (node != null)
            {
                foreach (var prim in node.Primitives)
                    DrawPrimitive(prim, graphics);
            }
            DrawNodeTreeRecursive(nodeBase.Children, graphics);
        }
    }

    void DrawPrimitive(WGPUPrimitiveData prim, Graphics graphics)
    {
        if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0) return;
        if (prim.CurrentAlpha <= 0 && prim.AlphaMode != 1u) return;

        var matrixData = Graphics._scratchMatrix48;
        Graphics.CopyMatrixTransposed(prim.World, matrixData, 0);
        Graphics.CopyMatrixTransposed(prim.View, matrixData, 16);
        Graphics.CopyMatrixTransposed(prim.Projection, matrixData, 32);

        var uniformData = Graphics._scratchUniform;
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);

        Array.Copy(matrixData, 0, uniformData, 0, 48);

        // 2-3 contract clause 6: previous-data slots must be written after Clear,
        // because Clear overwrites the full history range starting at float 48.
        Graphics.WritePrevMatrices(uniformData, prim.PrevWorldMatrix);

        uniformData[84] = prim.BaseColor.X;
        uniformData[85] = prim.BaseColor.Y;
        uniformData[86] = prim.BaseColor.Z;
        uniformData[87] = prim.BaseColor.W;

        int textureFlags = 0;
        if (!string.IsNullOrEmpty(prim.MetallicRoughnessTextureName)) textureFlags |= 1;
        if (!string.IsNullOrEmpty(prim.NormalTextureName)) textureFlags |= 2;
        if (!string.IsNullOrEmpty(prim.OcclusionTextureName)) textureFlags |= 4;
        if (!string.IsNullOrEmpty(prim.EmissiveTextureName)) textureFlags |= 8;

        // Non-instanced morph path: upload the first 4 node weights through uniforms,
        // and let the VS accumulate them under bit 64, matching native MaterialParams.MorphWeights.
        bool hasMorph = prim.MorphTargetCount > 0;
        Vector4 morphWeights = hasMorph ? ExtractMorphWeights(prim.OwnerNode) : Vector4.Zero;

        // 2-3 Step C: deformation-history sentinel bits.
        // The previous bone palette is populated automatically by the JS-side shadow copy, with no extra upload.
        // Previous morph weights are written to floats 80-83.
        // Both require deformation data to have been ready for two consecutive frames; otherwise the bit stays 0,
        // the VS falls back to current-frame source data, and that deformation path contributes no velocity (contract clause 8).
        int prevDataFlags = 0;
        if (_prevDeformReady)
        {
            if (prim.HasSkinning)
                prevDataFlags |= WebGPUPrevDataFlags.PrevBones;
            if (hasMorph)
            {
                prevDataFlags |= WebGPUPrevDataFlags.PrevMorph;
                new WebGPUUniformWriter(uniformData).SetPrevMorphWeights(prim.PrevMorphWeights);
            }
        }

        Graphics.WriteLightUniform(
            uniformData,
            renderMode: (int)prim.RenderMode,
            metallic: prim.MetallicFactor,
            roughness: prim.RoughnessFactor,
            alpha: prim.CurrentAlpha,
            emissive: prim.EmissiveFactor,
            ao: 1f,
            alphaMode: prim.AlphaMode,
            alphaCutoff: prim.CurrentAlphaCutoff,
            textureFlags: textureFlags,
            isSkinned: prim.HasSkinning,
            isMorph: hasMorph,
            morphWeights: morphWeights,
            prevDataFlags: prevDataFlags);

        string textureName = !string.IsNullOrEmpty(prim.BaseColorTextureName)
            ? prim.BaseColorTextureName
            : "White";
        string normalName = prim.NormalTextureName ?? "White";
        string mrName = prim.MetallicRoughnessTextureName ?? "White";
        string aoName = prim.OcclusionTextureName ?? "White";
        string emissiveName = prim.EmissiveTextureName ?? "White";

        if (!prim.Uploaded)
        {
            graphics.EnqueueStaticMeshUpload(Name, prim, textureName, normalName, mrName, aoName, emissiveName);
            return;
        }
        else if (prim.LastTextureName != textureName
            || prim.LastNormalTextureName != normalName
            || prim.LastMRTextureName != mrName
            || prim.LastAOTextureName != aoName
            || prim.LastEmissiveTextureName != emissiveName)
        {
            var rebindStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Rebind-only path: pass empty byte spans so the JS side takes the early return and updates only texture bindings.
            WebGPUInterop.UploadStaticMesh(
                prim.CacheKey, Span<byte>.Empty, Span<byte>.Empty,
                textureName, normalName, mrName, aoName, emissiveName, prim.VertexStrideFloats,
                prim.Use32BitIndices ? "uint32" : "uint16", prim.DoubleSided, prim.HasSkinning,
                Span<byte>.Empty, 0, 0);

            prim.LastTextureName = textureName;
            prim.LastNormalTextureName = normalName;
            prim.LastMRTextureName = mrName;
            prim.LastAOTextureName = aoName;
            prim.LastEmissiveTextureName = emissiveName;
        }

        if (prim.Uploaded && prim.GeometryDirty && prim.VertexBytes.Length > 0)
        {
            // Reserved path: morph no longer marks geometry dirty, so this remains only for future geometry-update paths.
            graphics.UpdateStaticMeshVertices(prim);
            prim.GeometryDirty = false;
        }

        if (prim.HasSkinning)
        {
            graphics.FlushDrawMesh3DBatch();
            graphics.EnqueueDrawSkinnedMesh(prim.CacheKey, uniformData);
        }
        else
        {
            graphics.FlushDrawSkinnedMeshBatch();
            graphics.EnqueueDrawMesh3D(prim.CacheKey, uniformData);
        }
    }

    // 1-5 Shadow pass: replay the node-tree traversal with the matrix chain world / Identity / light-space VP (Projection slot).
    // Skip transparent objects (contract 7) and primitives that have not been uploaded
    // (the shadow pass does not trigger uploads). Draw routing to the shadow pipeline is completed implicitly by the JS-side _passDepthOnly,
    // and the batching paths are fully reused from the main pass.

    public void DrawShadow(Graphics graphics)
    {
        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.BeginSkinnedModelDraw(_skinCacheKey, _boneMatricesBytes);

        DrawShadowNodeTreeRecursive(_asset.gltfNodes, graphics);

        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.EndSkinnedModelDraw();
    }

    void DrawShadowNodeTreeRecursive(List<GltfNodeBase> nodes, Graphics graphics)
    {
        foreach (var nodeBase in nodes)
        {
            var node = nodeBase as WGPUGLTFNode;
            if (node != null)
            {
                foreach (var prim in node.Primitives)
                    DrawShadowPrimitive(prim, graphics);
            }
            DrawShadowNodeTreeRecursive(nodeBase.Children, graphics);
        }
    }

    void DrawShadowPrimitive(WGPUPrimitiveData prim, Graphics graphics)
    {
        if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0) return;
        if (prim.CurrentAlpha <= 0 && prim.AlphaMode != 1u) return;
        // Contract 7: true BLEND-transparent objects do not cast shadows,
        // and uploads are not triggered from the shadow pass (missing a shadow on the first frame is acceptable).
        if (prim.IsTransparent || !prim.Uploaded) return;

        var matrixData = Graphics._scratchMatrix48;
        Graphics.CopyMatrixTransposed(prim.World, matrixData, 0);
        Graphics.CopyMatrixTransposed(Matrix4x4.Identity, matrixData, 16);
        Graphics.CopyMatrixTransposed(graphics._shadowViewProj, matrixData, 32);

        var uniformData = Graphics._scratchUniform;
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
        Array.Copy(matrixData, 0, uniformData, 0, 48);

        uniformData[84] = prim.BaseColor.X;
        uniformData[85] = prim.BaseColor.Y;
        uniformData[86] = prim.BaseColor.Z;
        uniformData[87] = prim.BaseColor.W;

        // Non-instanced morph path: use the same weights as the main pass so animated shadows stay consistent with the visible model (contract 3).
        bool hasMorph = prim.MorphTargetCount > 0;
        Vector4 morphWeights = hasMorph ? ExtractMorphWeights(prim.OwnerNode) : Vector4.Zero;

        // 2-3 Step C: the shadow pass injects no previous-frame data at all (including prevViewProjection),
        // so sentinel bits always remain 0. WriteLightUniform always writes flags.x, which prevents scratch-buffer reuse
        // from leaking bits from the main pass.

        Graphics.WriteLightUniform(
            uniformData,
            renderMode: (int)prim.RenderMode,
            metallic: prim.MetallicFactor,
            roughness: prim.RoughnessFactor,
            alpha: prim.CurrentAlpha,
            emissive: prim.EmissiveFactor,
            ao: 1f,
            alphaMode: prim.AlphaMode,
            alphaCutoff: prim.CurrentAlphaCutoff,
            textureFlags: 0,
            isSkinned: prim.HasSkinning,
            isMorph: hasMorph,
            morphWeights: morphWeights);

        if (prim.HasSkinning)
        {
            graphics.FlushDrawMesh3DBatch();
            graphics.EnqueueDrawSkinnedMesh(prim.CacheKey, uniformData);
        }
        else
        {
            graphics.FlushDrawSkinnedMeshBatch();
            graphics.EnqueueDrawMesh3D(prim.CacheKey, uniformData);
        }
    }

    // Phase 4 Outline pass: replay the node-tree traversal with the matrix chain world / view / projection
    // (using the same camera as the main pass).
    // Skip transparent objects (contract 7) and primitives that have not been uploaded.
    // Draw routing to the mask pipeline is completed implicitly by the JS-side _passOutlineMask,
    // and the outline color is written into the hdrParams slot, mirroring VK Pipeline.SetOutlineMaskColor's FS push constant.

    public void DrawOutlineMask(Graphics graphics)
    {
        if (!_transformInitialized || !_outline2DActive)
            return;

        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.BeginSkinnedModelDraw(_skinCacheKey, _boneMatricesBytes);

        DrawOutlineMaskNodeTreeRecursive(_asset.gltfNodes, graphics);

        if (_hasSkinning && !string.IsNullOrEmpty(_skinCacheKey))
            graphics.EndSkinnedModelDraw();
    }

    void DrawOutlineMaskNodeTreeRecursive(List<GltfNodeBase> nodes, Graphics graphics)
    {
        foreach (var nodeBase in nodes)
        {
            var node = nodeBase as WGPUGLTFNode;
            if (node != null)
            {
                foreach (var prim in node.Primitives)
                    DrawOutlineMaskPrimitive(prim, graphics);
            }
            DrawOutlineMaskNodeTreeRecursive(nodeBase.Children, graphics);
        }
    }

    void DrawOutlineMaskPrimitive(WGPUPrimitiveData prim, Graphics graphics)
    {
        if (prim.VertexData == null || prim.IndexData == null || prim.VertexData.Length == 0) return;
        if (prim.CurrentAlpha <= 0 && prim.AlphaMode != 1u) return;
        // Contract 7 mirror: true BLEND-transparent objects do not receive outlines,
        // and uploads are not triggered from the mask pass (missing outlines on the first frame is acceptable).
        if (prim.IsTransparent || !prim.Uploaded) return;

        var matrixData = Graphics._scratchMatrix48;
        Graphics.CopyMatrixTransposed(prim.World, matrixData, 0);
        Graphics.CopyMatrixTransposed(prim.View, matrixData, 16);
        Graphics.CopyMatrixTransposed(prim.Projection, matrixData, 32);

        var uniformData = Graphics._scratchUniform;
        Array.Clear(uniformData, 48, WebGPUUniformLayout.TotalFloats - 48);
        Array.Copy(matrixData, 0, uniformData, 0, 48);

        uniformData[84] = prim.BaseColor.X;
        uniformData[85] = prim.BaseColor.Y;
        uniformData[86] = prim.BaseColor.Z;
        uniformData[87] = prim.BaseColor.W;

        // Non-instanced morph path: use the same weights as the main pass so the outline mask matches the animated model exactly.
        bool hasMorph = prim.MorphTargetCount > 0;
        Vector4 morphWeights = hasMorph ? ExtractMorphWeights(prim.OwnerNode) : Vector4.Zero;

        // The mask pass injects no previous-frame data, so sentinel bits stay 0 here as well, matching the shadow path.
        Graphics.WriteLightUniform(
            uniformData,
            renderMode: (int)prim.RenderMode,
            metallic: prim.MetallicFactor,
            roughness: prim.RoughnessFactor,
            alpha: prim.CurrentAlpha,
            emissive: prim.EmissiveFactor,
            ao: 1f,
            alphaMode: prim.AlphaMode,
            alphaCutoff: prim.CurrentAlphaCutoff,
            textureFlags: 0,
            isSkinned: prim.HasSkinning,
            isMorph: hasMorph,
            morphWeights: morphWeights);

        // The outline color is stored in the hdrParams slot (floats 104-107) for the mask FS to read.
        // This must happen after WriteLightUniform. That method does not touch this slot,
        // but Array.Clear above has already zeroed it, so the group color is written explicitly here.
        new WebGPUUniformWriter(uniformData).SetOutlineMaskColor(_outline2DColor);

        if (prim.HasSkinning)
        {
            graphics.FlushDrawMesh3DBatch();
            graphics.EnqueueDrawSkinnedMesh(prim.CacheKey, uniformData);
        }
        else
        {
            graphics.FlushDrawSkinnedMeshBatch();
            graphics.EnqueueDrawMesh3D(prim.CacheKey, uniformData);
        }
    }

    public void Dispose()
    {
        _asset._nodeMap.Clear();
    }
}

internal class WGPUSprite3D
{
    public string TextureName { get; set; }

    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 View { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// 2-3 contract clause 6: CPU shadow copy of the previous-frame world matrix
    /// (not transposed, semantically identical to DXSprite3D._lastWorldMatrix).
    /// The uniform buffer is a scratch array overwritten in place every frame, so previous data must never be read back from it
    /// and must instead be provided by this field.
    /// All zeros means no history yet (first frame), so the shader falls back to the current-frame world matrix and produces zero velocity.
    /// </summary>
    public Matrix4x4 PrevWorldMatrix;

    public bool TransformInitialized { get; set; }
}

internal class WGPUMesh3D
{
    public string Name { get; }
    public Mesh3D Mesh { get; }

    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 View { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// 2-3 contract clause 6: CPU shadow copy of the previous-frame world matrix
    /// (not transposed, semantically identical to the per-primitive copy used by DXMesh3D).
    /// All zeros means no history yet (first frame), so the shader falls back to the current-frame world matrix and produces zero velocity.
    /// </summary>
    public Matrix4x4 PrevWorldMatrix;

    public bool TransformInitialized { get; set; }

    public float MeshAlpha { get; set; } = 1f;

    /// <summary>Mesh-level color multiplier (mirrors Mesh3D.ColorTint, synchronized every frame by UpdateMesh3D; RGB multiplies into Surface.BaseColor).</summary>
    public Vector4 ColorTint { get; set; } = Vector4.One;

    /// <summary>2-2 contract clause 7: GTAO exemption (mirrors Mesh3D.ExcludeFromAo and is synchronized every frame by UpdateMesh3D;
    /// BuildSurfaceUniform uses it to set the NoDepthWrite bit in flags.w, which routes JS to the Nd pipeline variant).</summary>
    public bool ExcludeFromAo { get; set; }

    // Unified highlight: host bounds-box state
    // (box geometry is built lazily on the first enabled frame, synchronized by UpdateMesh3D, and drawn at the end of DrawMesh3D).
    internal bool BoundsActive { get; set; }
    internal WebBoundsBox? BoundsBox { get; set; }

    // Unified highlight: wireframe-shell state
    // (per-surface shell boxes are built lazily on the first enabled frame, synchronized by UpdateMesh3D, and drawn at the end of DrawMesh3D).
    internal bool WireframeActive { get; set; }
    internal List<WebShellBox?>? ShellBoxes { get; set; }
    internal float BuiltShellEdgeWidth;

    // Unified highlight: Outline2D state
    // (synchronized by UpdateMesh3D and drawn by RenderOutlineMask per surface cache).
    internal bool Outline2DActive { get; set; }
    internal Vector4 Outline2DMaskColor { get; set; }
    internal float Outline2DMaskWidth { get; set; }

    public class SurfaceCacheEntry
    {
        public float[] VertexData;
        public byte[] VertexBytes;
        public ushort[] IndexData;
        public byte[] IndexBytes;
        public Vector3 LocalBoundsCenter;
        /// <summary>1-3: local AABB half extents paired with LocalBoundsCenter.</summary>
        public Vector3 LocalBoundsExtents;
        public string CacheKey;
        public bool Uploaded;
        public string LastTextureName;
        public string LastNormalTextureName;
        public string LastMetallicRoughnessTextureName;
        public string LastOcclusionTextureName;
        public string LastEmissiveTextureName;
        public bool LastDoubleSided;
    }
    public readonly Dictionary<object, SurfaceCacheEntry> SurfaceCaches = new();

    /// <summary>
    /// Snapshot of resolved five-slot texture names captured during Load.
    /// On Web, Draw rebuilds uniforms from Surface every frame, while TextureOverride is consumed only once and cleared after Load completes.
    /// Because of that, Draw needs these resolved texture names (procedural synthesized name / path name / "White")
    /// plus the base textureFlags ("declared means enabled", aligned with the native HasTexture criteria) to be retained here.
    /// </summary>
    public class ResolvedTextureSet
    {
        public string BaseColor = "White";
        public string Normal = "White";
        public string MetallicRoughness = "White";
        public string Occlusion = "White";
        public string Emissive = "White";
        public int TextureFlags;
    }
    public readonly Dictionary<Surface, ResolvedTextureSet> ResolvedTextures = new();

    public WGPUMesh3D(string name, Mesh3D mesh)
    {
        Name = name;
        Mesh = mesh;
    }
}
