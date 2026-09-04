// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Season.Models;

internal class GltfAsset
{
    internal Model Model;

    // Animation player, replacing the original animation fields.
    internal GLTFAnimationPlayer _animationPlayer = new();
    internal List<GLTFAnimation> _animations = new List<GLTFAnimation>();

    // Bone-matrix buffer.
    internal Dictionary<GltfNodeBase, System.Numerics.Matrix4x4> _nodeTransforms = new Dictionary<GltfNodeBase, System.Numerics.Matrix4x4>();

    internal List<GltfNodeBase> gltfNodes = new List<GltfNodeBase>();

    /// <summary>
    /// Imported KHR_lights_punctual lights for this model in model-local space.
    /// Coordinates are already converted from RH to LH, and intensity stores the original candela value.
    /// ProcessNode collects them node by node, and Load assigns the result to
    /// <see cref="Season.Controls.Model.ImportedPunctualLights"/> at the end.
    /// </summary>
    internal List<GpuLight> ImportedLights = new List<GpuLight>();

    internal Dictionary<int, GltfNodeBase> _nodeMap = new Dictionary<int, GltfNodeBase>();

    /// <summary>Injected by DXModel to convert a SharpGLTF.Node into a platform-specific GltfNodeBase-derived instance.</summary>
    internal Func<SharpGLTF.Schema2.Node, GltfNodeBase> CreateGLTFNodeCallback;

    /// <summary>Injected by DXModel to process a single MeshPrimitive and attach the generated PrimitiveData to the node.</summary>
    internal Action<MeshPrimitive, GltfNodeBase, Model, Season.Basic.Camera> ProcessPrimitiveCallback;

    // The skins set does not change after loading, so cache the first collected result
    // to avoid per-frame new List allocations and Contains scans.
    private List<GLTFSkin> _allSkinsCache;

    internal List<GLTFSkin> GetAllSkins()
    {
        if (_allSkinsCache != null)
            return _allSkinsCache;

        var skins = new List<GLTFSkin>();
        foreach (var node in gltfNodes)
        {
            if (node.Skin != null && !skins.Contains(node.Skin))
            {
                skins.Add(node.Skin);
            }
        }

        // Do not cache before nodes are populated, otherwise an empty pre-load result would be frozen in place.
        if (gltfNodes.Count > 0)
            _allSkinsCache = skins;
        return skins;
    }

    /// <summary>
    /// The starting offset of this skin inside the bone palette equals the prefix sum of GetAllSkins().
    /// Its order matches the flattened order used by GLTFAnimationPlayer.UpdateBoneMatrices exactly.
    /// Returns -1 when the skin is not found.
    /// </summary>
    internal int GetSkinPaletteOffset(GLTFSkin skin)
    {
        if (skin == null)
            return -1;

        int offset = 0;
        foreach (var s in GetAllSkins())
        {
            if (s == skin)
                return offset;
            offset += s.Joints.Count;
        }
        return -1;
    }

    /// <summary>
    /// v2 picking: per-instance shadow copy of node world matrices.
    /// The platform instancing path writes these every frame, while the shared picking layer reads them only.
    /// Indexed as [instance list index][gltfNodes node index].
    /// Static hosts without animation or skinning keep an empty array, and picking falls back to rest-pose node world transforms,
    /// which matches rendering for static content.
    /// </summary>
    internal Matrix4x4[][] InstancePickNodeWorlds = Array.Empty<Matrix4x4[]>();

    /// <summary>v2 picking: per-instance shadow copy of the bone palette, indexed as [instance][flattened joint index], including palette-offset semantics.</summary>
    internal Matrix4x4[][] InstancePickBones = Array.Empty<Matrix4x4[]>();

    public virtual void Load(Season.Controls.Model model, Season.Basic.Camera camera)
    {
        Model = model;

        if (StorageService.FileExist(StorageService.DirectoryBase, model.Name))
        {

        }
        else
        {
            StorageService.CopyToLocal(model.Name);
        }

        StorageService.TryGetStream(StorageService.DirectoryBase, model.Name, out Stream stream, out string errMsg);

        var glb = ModelRoot.ReadGLB(stream, new ReadSettings() { Validation = ValidationMode.Skip });
        //var model = ModelRoot.Load(@"C:\Docs\Engine\Models\" + Name, new ReadSettings() { Validation = ValidationMode.Skip });

        // Model-space bounding box in RH space: ComputeRestBounds uses the same source on all sides:
        // rest pose with node world matrices, skin rest replay (sum of w * v * IBM * J_rest),
        // plus initial asset morph-weight deltas, without animation sampling.
        // Animation expansion is conservatively covered by LocalBounds x AnimatedBoundsScale;
        // see the Mesh3DBase.LocalBoundsRaw contract.
        // Scene.EvaluateBoundingBox() is not used because its behavior is inconsistent across assets:
        // non-skinned models include node transforms, while skinned models can miss upper skeleton-node scaling
        // such as the Robot root scale of 0.5785 while partially evaluating skin data.
        // That disagrees with the render path, where every primitive goes through node.WorldTransform,
        // causing the bounds to drift away from the rendered model.
        // When animation tracks exist, it can also sample the full timeline at 1-second steps and union the results,
        // folding expanded morph ranges into the raw box, which makes the same Width, Height, and Depth
        // appear much smaller on Web than on DX or VK, as measured in MorphStressTest in 2026-08.
        ComputeRestBounds(glb, out var bMin, out var bMax, out _);

        model.Size = bMax - bMin;

        model.OriginalScale = 1 / new float[] { model.Size.X, model.Size.Y, model.Size.Z }.Max();

        // Build the full node tree starting from the actual root nodes of the default scene.
        // Do not use DefaultScene.VisualChildren directly as the entry point,
        // otherwise models such as skin or armature setups, where the root itself has no mesh
        // but the subtree contains meshes and animated bones, can lose upper skeleton nodes.
        // That would leave animation channels present but unable to bind Target.Node to runtime nodes.
        var sceneRootNodes = glb.LogicalNodes
            .Where(n => n.VisualParent == null && n.VisualScenes.Contains(glb.DefaultScene))
            .ToList();

        foreach (var node in sceneRootNodes)
        {
            ProcessNode(node, System.Numerics.Matrix4x4.Identity, camera, model);
        }

        BindSkins(glb);

        // v2 picking: node order stays stable along the clone path, so fill PickMesh.NodeIndex
        // at the end of template loading for per-instance shadow lookups.
        for (int i = 0; i < gltfNodes.Count; i++)
        {
            var node = gltfNodes[i];
            for (int m = 0; m < node.PickMeshes.Count; m++)
                node.PickMeshes[m].NodeIndex = i;
        }

        // Skinning rest-surface correction is already folded into ComputeRestBounds.
        // When skinned meshes exist, it recomputes using the true rendered rest pose,
        // so Size and OriginalScale stay consistent with the rendered object.

        // Load animations afterward because _nodeMap is needed to resolve nodes.
        LoadAnimations(glb);

        // Initialize the animation player.
        _animationPlayer.Initialize(_animations);

        // Control-local bounds computed once during loading.
        // See RenderQuality levels 1-3, clause 2.
        // bMin and bMax are in RH space and match the RH-to-LH rule where vertex Position.Z is negated.
        // After flipping Z, Min and Max swap on that axis.
        // Animated models are conservatively enlarged and must not rescan vertices every frame.
        var localBounds = Season.Rendering.Bounds3D.FromMinMax(
            new System.Numerics.Vector3(bMin.X, bMin.Y, -bMax.Z),
            new System.Numerics.Vector3(bMax.X, bMax.Y, -bMin.Z));
        // Unified positioning contract: the raw box before conservative animation expansion
        // is used for anchoring and per-axis scaling.
        // The setter triggers OnBoundsEstablished after the default size has been established,
        // so it must run after Size and OriginalScale.
        model.LocalBoundsRaw = localBounds;
        if (_animations.Count > 0)
            localBounds = localBounds.Scaled(RenderQuality.Current.AnimatedBoundsScale);
        model.LocalBounds = localBounds;

        // Pass imported punctual lights to Model so the App side can build SceneLights through AppendWorldLights.
        model.ImportedPunctualLights = ImportedLights;

        // Ensure all resources are in the correct state before upload executes.
        //foreach (var task in GraphicsDirectX.GraphicsDevice.textureUploadBatch.GetTasks())
        //{
        //    task.Texture.TransitionTo(GraphicsDirectX.GraphicsDevice.CopyGraphicsCommandList, ResourceStates.Common);
        //}

        Graphics.Instance.ExecuteUpload();
    }

    /// <summary>
    /// Evaluates the raw model bounds in RH space using the same source on all sides:
    /// rest-pose node world matrices, skin rest replay as sum of w * v * IBM * J_rest,
    /// plus initial asset morph-weight deltas, where POSITION deltas are multiplied by node.MorphWeights
    /// and node weights fall back to mesh.weights by SharpGLTF semantics.
    /// Animation is not sampled here. Its expansion is conservatively covered by
    /// LocalBounds x AnimatedBoundsScale, intentionally preferring bounds that are too large rather than too small.
    /// The raw box serves only anchoring and per-axis scaling under the Mesh3DBase.LocalBoundsRaw contract.
    /// If the full animated union were mixed in, LocalSize would be inflated and the same Width, Height,
    /// and Depth would scale down more than expected, which was the old Web EvaluateBoundingBox issue in MorphStressTest.
    /// For empty bounds, such as meshless assets, this falls back to SharpGLTF evaluation
    /// so Size and OriginalScale remain computable.
    /// </summary>
    protected static void ComputeRestBounds(ModelRoot glb, out System.Numerics.Vector3 bMin, out System.Numerics.Vector3 bMax, out bool hasBounds)
    {
        // Local functions cannot capture out parameters in CS1628, so accumulate into locals and assign once at the end.
        var min = System.Numerics.Vector3.Zero;
        var max = System.Numerics.Vector3.Zero;
        var has = false;

        // Union bounds using v * meshNodeWorld.
        // This is only finally correct for non-skinned meshes.
        // The true rendered rest surface of skinned meshes is sum of w * v * IBM * J_rest,
        // recomputed in AccumulateRestSurfaceBounds.
        // Some exporters, verified with Godot, bake bone-chain transforms into bone space so IBM * J_rest ~= I
        // and do not inherit armature root scaling.
        // That differs from v * meshNodeWorld by exactly that scale factor, measured as 1 / 0.5785 on Robot,
        // which pushes the head about 0.42 box heights outside the bounds.
        void AccumulateNodeBounds(SharpGLTF.Schema2.Node node)
        {
            if (node.Mesh != null)
            {
                var world = node.WorldMatrix;
                foreach (var prim in node.Mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (positions == null || positions.Count == 0) continue;

                    // Initial morph-weight deltas: decode once during loading and accumulate per vertex.
                    // Slots with zero weight stay null and are skipped.
                    var deltas = LoadMorphPositionDeltas(prim, node.MorphWeights);

                    for (int i = 0; i < positions.Count; i++)
                    {
                        var w = System.Numerics.Vector3.Transform(ApplyMorphDeltas(positions[i], i, deltas), world);
                        if (!has) { min = w; max = w; has = true; }
                        else
                        {
                            min = System.Numerics.Vector3.Min(min, w);
                            max = System.Numerics.Vector3.Max(max, w);
                        }
                    }
                }
            }

            foreach (var child in node.VisualChildren)
                AccumulateNodeBounds(child);
        }

        // Recompute from the true rendered rest surface:
        // skinned meshes use sum of w * v * IBM * J_rest, which collapses to v * meshNodeWorld on compliant assets,
        // while non-skinned meshes still use v * WorldMatrix.
        // Like AccumulateNodeBounds, this is a one-time union during loading, with the same morph-delta accumulation.
        void AccumulateRestSurfaceBounds()
        {
            foreach (var node in glb.LogicalNodes)
            {
                if (node.Mesh == null) continue;
                var world = node.WorldMatrix;
                SharpGLTF.Schema2.Node[] joints = null;
                System.Numerics.Matrix4x4[] replay = null;
                if (node.Skin != null)
                {
                    joints = node.Skin.Joints.ToArray();
                    var ibms = node.Skin.GetInverseBindMatricesAccessor()?.AsMatrix4x4Array()?.ToArray();
                    if (ibms != null)
                    {
                        replay = new System.Numerics.Matrix4x4[ibms.Length];
                        for (int j = 0; j < ibms.Length; j++)
                            replay[j] = ibms[j] * joints[j].WorldMatrix; // Joint replay matrix in RH space.
                    }
                }

                foreach (var prim in node.Mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (positions == null || positions.Count == 0) continue;
                    var jointIdx = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                    var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                    bool skinned = replay != null && jointIdx != null && weights != null;

                    var deltas = LoadMorphPositionDeltas(prim, node.MorphWeights);

                    void Acc(System.Numerics.Vector3 w)
                    {
                        if (!has) { min = w; max = w; has = true; }
                        else
                        {
                            min = System.Numerics.Vector3.Min(min, w);
                            max = System.Numerics.Vector3.Max(max, w);
                        }
                    }

                    if (!skinned)
                    {
                        for (int i = 0; i < positions.Count; i++)
                            Acc(System.Numerics.Vector3.Transform(ApplyMorphDeltas(positions[i], i, deltas), world));
                        continue;
                    }

                    for (int i = 0; i < positions.Count; i++)
                    {
                        var v = ApplyMorphDeltas(positions[i], i, deltas);
                        var ji = jointIdx[i];
                        var wt = weights[i];
                        var p = System.Numerics.Vector3.Zero;
                        if (wt.X > 0) p += wt.X * System.Numerics.Vector3.Transform(v, replay[(int)ji.X]);
                        if (wt.Y > 0) p += wt.Y * System.Numerics.Vector3.Transform(v, replay[(int)ji.Y]);
                        if (wt.Z > 0) p += wt.Z * System.Numerics.Vector3.Transform(v, replay[(int)ji.Z]);
                        if (wt.W > 0) p += wt.W * System.Numerics.Vector3.Transform(v, replay[(int)ji.W]);
                        Acc(p);
                    }
                }
            }
        }

        foreach (var root in glb.DefaultScene.VisualChildren)
            AccumulateNodeBounds(root);

        // Empty-bounds fallback for meshless assets: use SharpGLTF evaluation so Size and OriginalScale remain computable.
        if (!has)
        {
            var fallback = glb.DefaultScene.EvaluateBoundingBox();
            min = fallback.Min;
            max = fallback.Max;
        }

        // Skinning rest-surface correction: when skinned meshes exist, recompute using the true rendered rest pose.
        if (glb.LogicalNodes.Any(n => n.Mesh != null && n.Skin != null))
        {
            min = System.Numerics.Vector3.Zero;
            max = System.Numerics.Vector3.Zero;
            has = false;
            AccumulateRestSurfaceBounds();
        }

        bMin = min;
        bMax = max;
        hasBounds = has;
    }

    /// <summary>
    /// Decodes POSITION deltas for each morph target in the primitive and multiplies them
    /// by the asset's initial weights, where node weights fall back to mesh.weights.
    /// Slots with zero weight or without POSITION data remain null and are skipped during per-vertex accumulation.
    /// </summary>
    static System.Numerics.Vector3[]?[] LoadMorphPositionDeltas(MeshPrimitive prim, IReadOnlyList<float> weights)
    {
        int targetCount = Math.Min(prim.MorphTargetsCount, weights.Count);
        if (targetCount == 0)
            return Array.Empty<System.Numerics.Vector3[]?>();

        var deltas = new System.Numerics.Vector3[]?[targetCount];
        for (int t = 0; t < targetCount; t++)
        {
            float w = weights[t];
            if (w == 0f) continue;
            if (!prim.GetMorphTargetAccessors(t).TryGetValue("POSITION", out var acc))
                continue;

            var raw = acc.AsVector3Array();
            var scaled = new System.Numerics.Vector3[raw.Count];
            for (int i = 0; i < raw.Count; i++)
                scaled[i] = raw[i] * w;
            deltas[t] = scaled;
        }
        return deltas;
    }

    static System.Numerics.Vector3 ApplyMorphDeltas(System.Numerics.Vector3 pos, int vertexIndex, System.Numerics.Vector3[]?[] deltas)
    {
        for (int t = 0; t < deltas.Length; t++)
        {
            var d = deltas[t];
            if (d != null && vertexIndex < d.Length)
                pos += d[vertexIndex];
        }
        return pos;
    }

    protected List<Vertex> LoadVertices(MeshPrimitive sharpPrimitive, SharpGLTF.Schema2.Node sharpNode)
    {
        // Extract vertex attributes.
        var positions = sharpPrimitive.GetVertexAccessor("POSITION")?.AsVector3Array();
        var normals = sharpPrimitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var tangents = sharpPrimitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var texCoords = sharpPrimitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var joints = sharpPrimitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
        var weights = sharpPrimitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

        var vertices = new List<Vertex>();
        if (positions != null)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var vertex = new Vertex
                {
                    Position = positions[i],
                    Normal = normals != null ? normals[i] : Vector3.Zero,
                    Tangent = tangents != null ? tangents[i] : Vector4.Zero,
                    TexCoord = texCoords != null ? texCoords[i] : Vector2.Zero,
                    Joints = joints != null ? joints[i] : Vector4.Zero,
                    Weights = weights != null ? weights[i] : Vector4.Zero
                };
                vertices.Add(vertex);
            }
        }

        return vertices;
    }

    internal void ValidateSkinData()
    {
        foreach (var skin in GetAllSkins())
        {
            Debug.WriteLine($"=== Skin: {skin.Name} ===");

            // Check whether the joint count matches the inverse-bind-matrix count.
            if (skin.Joints.Count != skin.InverseBindMatrices.Count)
            {
                Debug.WriteLine($"Warning: joint count mismatch: Joints={skin.Joints.Count}, IBM={skin.InverseBindMatrices.Count}");
            }

            // Validate each joint.
            for (int i = 0; i < skin.Joints.Count; i++)
            {
                var joint = skin.Joints[i];
                if (joint == null)
                {
                    Debug.WriteLine($"Error: joint {i} is null");
                    continue;
                }

                // Check whether the joint index is set correctly.
                if (joint.JointIndex != i)
                {
                    Debug.WriteLine($"Warning: joint index mismatch for {joint.Name}: expected={i}, actual={joint.JointIndex}");
                }
            }

            // Check transform consistency across joints.
            for (int i = 0; i < skin.Joints.Count; i++)
            {
                var joint = skin.Joints[i];
                if (joint != null)
                {
                    // Check whether transform scale-related values are consistent.
                    var scale = joint.WorldTransform.Translation; //.GetScale();
                    Debug.WriteLine($"Joint {i} ({joint.Name}) translation: {scale}");
                }
            }
        }
    }

    internal void LoadAnimations(ModelRoot model)
    {
        _animations.Clear();

        foreach (var gltfAnimation in model.LogicalAnimations)
        {
            var animation = new GLTFAnimation
            {
                Name = gltfAnimation.Name ?? $"Animation_{gltfAnimation.LogicalIndex}"
            };

            // Process animation channels, where each channel is already associated with its sampler.
            foreach (var gltfChannel in gltfAnimation.Channels)
            {
                var channel = new Season.Models.AnimationChannel();
                GltfNodeBase? targetNode = null;

                // Set the target node.
                if (gltfChannel.TargetNode != null)
                {
                    targetNode = FindGltfNodeBySharpGltfNode(gltfChannel.TargetNode);
                    channel.Target = new AnimationChannelTarget
                    {
                        Node = targetNode,
                        Path = ConvertAnimationTargetPath(gltfChannel.TargetNodePath),
                    };
                }

                // Create and associate the sampler.
                // In SharpGLTF, AnimationChannel associates samplers through internal indexing,
                // so they must be retrieved through the typed Get methods.
                //AnimationSampler gltfSampler = null;

                // Create and associate the sampler.
                // In SharpGLTF, keyframe data is exposed through the IAnimationSampler<T> interface.
                var sampler = new AnimationSampler();

                bool samplerLoaded = false;

                // Select the proper sampler accessor according to the target path.
                switch (gltfChannel.TargetNodePath)
                {
                    case PropertyPath.translation:
                        {
                            var translationSampler = gltfChannel.GetTranslationSampler();
                            if (translationSampler != null)
                            {
                                sampler.OutputElementCount = 3;
                                sampler.Interpolation = ConvertInterpolationMode(translationSampler.InterpolationMode);
                                if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
                                {
                                    foreach (var keyframe in translationSampler.GetCubicKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendVector3Frame(sampler.InTangents ??= new List<float>(), ConvertTranslationRhToLh(keyframe.Value.Item1));
                                        AppendVector3Frame(sampler.Values, ConvertTranslationRhToLh(keyframe.Value.Item2));
                                        AppendVector3Frame(sampler.OutTangents ??= new List<float>(), ConvertTranslationRhToLh(keyframe.Value.Item3));
                                    }
                                }
                                else
                                {
                                    foreach (var keyframe in translationSampler.GetLinearKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendVector3Frame(sampler.Values, ConvertTranslationRhToLh(keyframe.Value));
                                    }
                                }
                                samplerLoaded = true;
                            }
                        }
                        break;

                    case PropertyPath.rotation:
                        {
                            var rotationSampler = gltfChannel.GetRotationSampler();
                            if (rotationSampler != null)
                            {
                                sampler.OutputElementCount = 4;
                                sampler.Interpolation = ConvertInterpolationMode(rotationSampler.InterpolationMode);
                                if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
                                {
                                    foreach (var keyframe in rotationSampler.GetCubicKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendQuaternionFrame(sampler.InTangents ??= new List<float>(), ConvertQuaternionRhToLh(keyframe.Value.Item1));
                                        AppendQuaternionFrame(sampler.Values, ConvertQuaternionRhToLh(keyframe.Value.Item2));
                                        AppendQuaternionFrame(sampler.OutTangents ??= new List<float>(), ConvertQuaternionRhToLh(keyframe.Value.Item3));
                                    }
                                }
                                else
                                {
                                    foreach (var keyframe in rotationSampler.GetLinearKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendQuaternionFrame(sampler.Values, ConvertQuaternionRhToLh(keyframe.Value));
                                    }
                                }
                                samplerLoaded = true;
                            }
                        }
                        break;

                    case PropertyPath.scale:
                        {
                            var scaleSampler = gltfChannel.GetScaleSampler();
                            if (scaleSampler != null)
                            {
                                sampler.OutputElementCount = 3;
                                sampler.Interpolation = ConvertInterpolationMode(scaleSampler.InterpolationMode);
                                if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
                                {
                                    foreach (var keyframe in scaleSampler.GetCubicKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendVector3Frame(sampler.InTangents ??= new List<float>(), keyframe.Value.Item1);
                                        AppendVector3Frame(sampler.Values, keyframe.Value.Item2);
                                        AppendVector3Frame(sampler.OutTangents ??= new List<float>(), keyframe.Value.Item3);
                                    }
                                }
                                else
                                {
                                    foreach (var keyframe in scaleSampler.GetLinearKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        AppendVector3Frame(sampler.Values, keyframe.Value);
                                    }
                                }
                                samplerLoaded = true;
                            }
                        }
                        break;

                    case PropertyPath.weights:
                        {
                            var morphSampler = gltfChannel.GetMorphSampler();
                            if (morphSampler != null)
                            {
                                sampler.OutputElementCount = 0;
                                sampler.Interpolation = ConvertInterpolationMode(morphSampler.InterpolationMode);
                                if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
                                {
                                    foreach (var keyframe in morphSampler.GetCubicKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        sampler.OutputElementCount = Math.Max(sampler.OutputElementCount, keyframe.Value.Item2?.Length ?? 0);
                                        AppendFloatFrame(sampler.InTangents ??= new List<float>(), keyframe.Value.Item1, sampler.OutputElementCount);
                                        AppendFloatFrame(sampler.Values, keyframe.Value.Item2, sampler.OutputElementCount);
                                        AppendFloatFrame(sampler.OutTangents ??= new List<float>(), keyframe.Value.Item3, sampler.OutputElementCount);
                                    }
                                }
                                else
                                {
                                    foreach (var keyframe in morphSampler.GetLinearKeys())
                                    {
                                        sampler.Inputs.Add(keyframe.Key);
                                        sampler.OutputElementCount = Math.Max(sampler.OutputElementCount, keyframe.Value?.Length ?? 0);
                                        AppendFloatFrame(sampler.Values, keyframe.Value, sampler.OutputElementCount);
                                    }
                                }

                                if (channel.Target?.Node != null && sampler.OutputElementCount > 0)
                                {
                                    EnsureNodeWeightsCapacity(channel.Target.Node, sampler.OutputElementCount);
                                }

                                samplerLoaded = true;
                            }
                        }
                        break;

                    default:
                        Debug.WriteLine($"Warning: unsupported target path: {gltfChannel.TargetNodePath}");
                        break;
                }

                if (samplerLoaded)
                {
                    channel.Sampler = sampler;
                    animation.Samplers.Add(sampler);
                }

                if (channel.Target != null && channel.Sampler != null)
                {
                    animation.Channels.Add(channel);
                }
            }

            _animations.Add(animation);
        }

        Debug.WriteLine($"Loaded {_animations.Count} animations with {_animations.Sum(a => a.Channels.Count)} total channels");
    }

    internal GLTFSkin CreateSkin(SharpGLTF.Schema2.Skin skin)
    {
        var gltfSkin = new GLTFSkin
        {
            Name = skin.Name ?? $"Skin_{skin.LogicalIndex}",
            InverseBindMatrices = new List<System.Numerics.Matrix4x4>(),
            Joints = new List<GltfNodeBase>()
        };

        // Load inverse bind matrices, converting them from RH to LH by the coordinate-system conjugation S * M * S,
        // where S = diag(1, 1, -1, 1).
        // This turns the glTF right-handed IBM into its DirectX left-handed equivalent,
        // so a vertex still gets the correct "model space -> bone bind space" transform after Z is flipped.
        // The conversion negates M13, M23, M31, M32, M34, and M43 once,
        // while M33, M11, M12, M21, M22, M44, and similar terms remain unchanged.
        if (skin.GetInverseBindMatricesAccessor() is Accessor ibmAccessor)
        {
            var matrices = ibmAccessor.AsMatrix4x4Array().ToArray();
            for (int i = 0; i < matrices.Length; i++)
            {
                matrices[i] = ConvertMatrixRhToLh(matrices[i]);
            }
            gltfSkin.InverseBindMatrices.AddRange(matrices);
        }
        else
        {
            // Fill with identity matrices.
            for (int i = 0; i < skin.Joints.Count; i++)
            {
                gltfSkin.InverseBindMatrices.Add(System.Numerics.Matrix4x4.Identity);
            }
        }

        // Attach joint nodes.
        for (int i = 0; i < skin.Joints.Count; i++)
        {
            var joint = skin.Joints[i];
            var gltfJoint = FindGltfNodeBySharpGltfNode(joint);

            if (gltfJoint != null)
            {
                gltfSkin.Joints.Add(gltfJoint);
                gltfJoint.IsJoint = true;
                gltfJoint.JointIndex = i;  // Use the index from the skin.
                gltfJoint.Skin = gltfSkin;
            }
            else
            {
                // Create a placeholder node.
                var placeholder = new GltfNodeBase
                {
                    Name = joint.Name ?? $"Joint_{joint.LogicalIndex}",
                    IsJoint = true,
                    JointIndex = i,
                    Skin = gltfSkin
                };
                gltfSkin.Joints.Add(placeholder);
            }

        }

        // Set the skeleton root node.
        if (skin.Skeleton != null)
        {
            gltfSkin.SkeletonRoot = FindGltfNodeBySharpGltfNode(skin.Skeleton);
        }

        return gltfSkin;
    }

    internal void BindSkins(ModelRoot model)
    {
        var skinCache = new Dictionary<int, GLTFSkin>();

        foreach (var sharpNode in model.LogicalNodes)
        {
            if (!_nodeMap.TryGetValue(sharpNode.LogicalIndex, out var gltfNode) || gltfNode == null)
                continue;

            gltfNode.IsJoint = sharpNode.IsSkinJoint;

            if (sharpNode.Skin == null)
                continue;

            if (!skinCache.TryGetValue(sharpNode.Skin.LogicalIndex, out var gltfSkin))
            {
                gltfSkin = CreateSkin(sharpNode.Skin);
                skinCache[sharpNode.Skin.LogicalIndex] = gltfSkin;
            }

            gltfNode.Skin = gltfSkin;
            if (gltfSkin.BindNode == null)
                gltfSkin.BindNode = gltfNode;
        }
    }

    GltfNodeBase FindGltfNodeBySharpGltfNode(SharpGLTF.Schema2.Node sharpGltfNode)
    {
        if (sharpGltfNode == null) return null;

        // Prefer lookup by LogicalIndex in the dictionary.
        if (_nodeMap.TryGetValue(sharpGltfNode.LogicalIndex, out GltfNodeBase gltfNode))
            return gltfNode;

        // A more robust fallback match is needed here.
        var expectedName = sharpGltfNode.Name ?? $"Node_{sharpGltfNode.LogicalIndex}";

        return gltfNodes.FirstOrDefault(n => n.Name == expectedName);
    }

    // Find the GLTFNode corresponding to a SharpGLTF node.
    GltfNodeBase FindGltfNode(SharpGLTF.Schema2.Node node)
    {
        // Match by scanning gltfNodes.
        foreach (var gltfNode in gltfNodes)
        {
            if (gltfNode.Name == node.Name || gltfNode.Name == $"Node_{node.LogicalIndex}")
            {
                return gltfNode;
            }
        }
        return null;
    }

    // Get the joint index.
    internal int GetJointIndex(SharpGLTF.Schema2.Node node)
    {
        // First find the matching GLTFNode.
        var gltfNode = FindGltfNodeBySharpGltfNode(node);
        if (gltfNode == null) return -1;

        // Check whether the node has skin data and a joint index.
        if (gltfNode.Skin != null && gltfNode.IsJoint)
        {
            for (int i = 0; i < gltfNode.Skin.Joints.Count; i++)
            {
                if (gltfNode.Skin.Joints[i] == gltfNode)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    internal void ProcessNode(SharpGLTF.Schema2.Node node, System.Numerics.Matrix4x4 parentWorldMatrix, Season.Basic.Camera camera, Season.Controls.Model model, GltfNodeBase parentGltfNode = null)
    {
        // Compute the current node's world transform matrix.
        var transform = node.LocalMatrix;
        var localTransform = node.LocalTransform;

        //var worldMatrix = localMatrix * parentWorldMatrix; //parentWorldMatrix * localMatrix; //

        var gltfNode = CreateGLTFNodeCallback != null ? CreateGLTFNodeCallback(node) : new GltfNodeBase();

        _nodeMap[node.LogicalIndex] = gltfNode;

        // RH-to-LH node transform conversion:
        // negate Translation.Z, map Rotation (x, y, z, w) to (-x, -y, z, w), and keep Scale unchanged.
        // Reason: LocalMatrix = S * R * T, and decomposing the conjugated matrix S * M * S
        // yields exactly this TRS form.
        System.Numerics.Vector3 translation;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 scale;

        if (localTransform.IsSRT)
        {
            translation = localTransform.Translation;
            rotation = localTransform.Rotation;
            scale = localTransform.Scale;
        }
        else
        {
            try
            {
                localTransform = localTransform.GetDecomposed();
                translation = localTransform.Translation;
                rotation = localTransform.Rotation;
                scale = localTransform.Scale;
            }
            catch (InvalidOperationException)
            {
                if (!System.Numerics.Matrix4x4.Decompose(transform, out scale, out rotation, out translation))
                {
                    translation = transform.Translation;
                    rotation = System.Numerics.Quaternion.Identity;
                    scale = System.Numerics.Vector3.One;
                }
            }
        }

        translation = new System.Numerics.Vector3(translation.X, translation.Y, -translation.Z);
        rotation = ConvertQuaternionRhToLh(rotation);

        gltfNode.Translation = translation;
        gltfNode.Rotation = rotation;
        gltfNode.Scale = scale;

        // Preserve the initial RTS values for properties without animation channels and for future animation resets.
        gltfNode.InitialTranslation = translation;
        gltfNode.InitialRotation = rotation;
        gltfNode.InitialScale = scale;
        if (node.MorphWeights != null && node.MorphWeights.Count > 0)
        {
            gltfNode.InitialWeights = node.MorphWeights.ToArray();
            gltfNode.Weights = (float[])gltfNode.InitialWeights.Clone();
        }
        else
        {
            gltfNode.InitialWeights = Array.Empty<float>();
            gltfNode.Weights = Array.Empty<float>();
        }

        // Compute the world transform.
        gltfNode.WorldTransform = gltfNode.LocalTransform * parentWorldMatrix; //parentWorldMatrix * gltfNode.LocalTransform; //

        // Capture KHR_lights_punctual lights attached to the node into model-local space
        // using the node world transform, which is already converted from RH to LH.
        if (node.PunctualLight != null)
        {
            CapturePunctualLight(node.PunctualLight, gltfNode.WorldTransform);
        }

        // Critical fix: establish parent-child relationships.
        if (parentGltfNode != null)
        {
            parentGltfNode.Children.Add(gltfNode);
        }

        // Process primitives when the node has a mesh.
        if (node.Mesh != null)
        {
            ProcessMesh(node.Mesh, gltfNode, model, camera);
        }

        // Recursively process child nodes, passing the current node as parent.
        foreach (var childNode in node.VisualChildren)
        {
            ProcessNode(childNode, gltfNode.WorldTransform, camera, model, gltfNode);
        }

        gltfNodes.Add(gltfNode);
    }

    void ProcessMesh(Mesh mesh, GltfNodeBase node, Model model, Season.Basic.Camera camera)
    {
        for (var primIndex = 0; primIndex < mesh.Primitives.Count; primIndex++)
        {
            var primitive = mesh.Primitives[primIndex];

            ProcessPrimitiveCallback?.Invoke(primitive, node, model, camera);

            // v2 picking validation: keep a compact picking mesh and reuse LoadMeshPrimitive
            // so the transform pipeline stays identical to rendering.
            var pickMesh = BuildPickMesh(primitive, node);
            if (pickMesh != null)
                node.PickMeshes.Add(pickMesh);
        }
    }

    /// <summary>
    /// Builds a compact picking mesh from a glTF primitive.
    /// Reuses <see cref="GLTFTools.LoadMeshPrimitive"/> so RH-to-LH conversion,
    /// winding reversal, and weight normalization stay bitwise aligned with rendering.
    /// For skinned primitives where JOINTS_0 and WEIGHTS_0 both exist,
    /// joints and weights are also preserved for hit-time skinning.
    /// Degenerate primitives with fewer than 3 vertices or indices return null and are skipped.
    /// </summary>
    PickMesh BuildPickMesh(MeshPrimitive primitive, GltfNodeBase ownerNode)
    {
        var (vertices, indices) = GLTFTools.LoadMeshPrimitive(primitive);
        if (vertices.Count < 3 || indices.Count < 3)
            return null;

        // Use the same skinning detection as LoadMeshPrimitive:
        // prefer the VertexAccessors dictionary and fall back to GetVertexAccessor.
        bool hasSkin = (primitive.VertexAccessors.ContainsKey("JOINTS_0") && primitive.VertexAccessors.ContainsKey("WEIGHTS_0"))
            || (primitive.GetVertexAccessor("JOINTS_0") != null && primitive.GetVertexAccessor("WEIGHTS_0") != null);

        var positions = new Vector3[vertices.Count];
        Vector4[] joints = null;
        Vector4[] weights = null;
        if (hasSkin)
        {
            joints = new Vector4[vertices.Count];
            weights = new Vector4[vertices.Count];
        }

        for (int i = 0; i < vertices.Count; i++)
        {
            positions[i] = vertices[i].Position;
            if (hasSkin)
            {
                joints[i] = vertices[i].Joints;
                weights[i] = vertices[i].Weights;
            }
        }

        return new PickMesh
        {
            Positions = positions,
            Indices = indices.ToArray(),
            Joints = joints,
            Weights = weights,
            OwnerNode = ownerNode,
        };
    }

    /// <summary>
    /// Captures KHR_lights_punctual lights parsed by SharpGLTF into engine <see cref="Season.Controls.GpuLight"/> instances in model-local space.
    /// Position and direction come from the node world transform, already converted from RH to LH.
    /// Intensity stores the original candela value, with scaling applied later in Model.AppendWorldLights.
    /// range &lt;= 0 is treated as infinite distance using pure 1 / d^2 attenuation.
    /// Directional lights are still not imported from glTF in this version and are provided by the higher-level LightSource layer.
    /// </summary>
    void CapturePunctualLight(SharpGLTF.Schema2.PunctualLight light, System.Numerics.Matrix4x4 worldMatrix)
    {
        if (light == null || ImportedLights.Count >= SceneLightParams.MaxLights)
            return;

        var position = worldMatrix.Translation;
        var color = light.Color;                 // Linear color in the 0..1 range.
        float intensity = light.Intensity;        // Original candela.
        float range = light.Range > 0f ? light.Range : 0f;

        switch (light.LightType)
        {
            case SharpGLTF.Schema2.PunctualLightType.Point:
                ImportedLights.Add(GpuLight.Point(position, color, intensity, range));
                break;

            case SharpGLTF.Schema2.PunctualLightType.Spot:
                // In KHR lights, node -Z is the emission direction.
                // Transform it as a normal by the world matrix and normalize afterward.
                var dir = System.Numerics.Vector3.Normalize(
                    System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(0f, 0f, -1f), worldMatrix));
                ImportedLights.Add(GpuLight.Spot(
                    position, dir, color, intensity, range,
                    MathF.Cos(light.InnerConeAngle), MathF.Cos(light.OuterConeAngle)));
                break;

            // PunctualLightType.Directional is not imported in this version.
            // Directional lights are provided by the higher-level LightSource layer.
        }
    }

    public void SetAnimating(bool animating)
    {
        _animationPlayer.IsAnimating = animating;
    }

    public void SetAnimationTime(float time)
    {
        _animationPlayer.AnimationTime = time;
    }

    public void PlayAnimation(string animationName = null)
    {
        if (!string.IsNullOrEmpty(animationName))
            _animationPlayer.Play(animationName);
        else
            _animationPlayer.Play();
    }

    public string? PlayNextAnimation()
    {
        int animationCount = _animationPlayer.AnimationCount;
        if (animationCount <= 0)
            return null;

        int nextIndex = (_animationPlayer.CurrentAnimationIndex + 1) % animationCount;
        _animationPlayer.Play(nextIndex);
        return _animationPlayer.GetCurrentAnimationName();
    }

    public IReadOnlyList<string> GetAnimationNames()
    {
        return _animationPlayer.GetAnimationNames();
    }

    /// <summary>Animation clip metadata such as name and duration, coming from the glTF parsing domain and independent of the graphics backend.</summary>
    public IReadOnlyList<ModelAnimationInfo> GetAnimations()
    {
        return _animationPlayer.GetAnimations();
    }

    public string? GetCurrentAnimationName()
    {
        return _animationPlayer.GetCurrentAnimationName();
    }

    public void StopAnimation()
    {
        _animationPlayer.Stop();
    }

    // ================= RH-to-LH helper conversions =================

    /// <summary>
    /// Applies the RH-to-LH conjugation M' = S * M * S to a 4x4 matrix,
    /// where S = diag(1, 1, -1, 1).
    /// This converts a glTF right-handed coordinate-space matrix, such as inverseBindMatrix,
    /// into its DirectX left-handed equivalent.
    /// Only the off-diagonal elements on row or column 3 are negated.
    /// Diagonal terms such as M33, M11, M12, M21, M22, and M44 remain unchanged.
    /// </summary>
    static System.Numerics.Matrix4x4 ConvertMatrixRhToLh(System.Numerics.Matrix4x4 m)
    {
        // S_ii * S_jj = -1 where exactly one of (i, j) is axis 2, meaning z.
        m.M13 = -m.M13;
        m.M23 = -m.M23;
        m.M43 = -m.M43;
        m.M31 = -m.M31;
        m.M32 = -m.M32;
        m.M34 = -m.M34;
        // M33 is negated twice and therefore remains unchanged.
        // M11, M12, M14, M21, M22, M24, M41, M42, and M44 also remain unchanged.
        return m;
    }

    /// <summary>
    /// Applies the RH-to-LH conjugation to a quaternion by mapping
    /// (x, y, z, w) to (-x, -y, z, w).
    /// Derivation:
    /// a right-handed quaternion for a rotation of theta around the Z axis is
    /// (0, 0, sin(theta / 2), cos(theta / 2)).
    /// In LH space the rotation direction around Z is reversed, and rotations around X and Y
    /// also reverse when Z is flipped, leading to the final mapping
    /// (x, y, z, w) -> (-x, -y, z, w).
    /// </summary>
    static System.Numerics.Quaternion ConvertQuaternionRhToLh(System.Numerics.Quaternion q)
    {
        return new System.Numerics.Quaternion(-q.X, -q.Y, q.Z, q.W);
    }

    static System.Numerics.Vector3 ConvertTranslationRhToLh(System.Numerics.Vector3 v)
    {
        return new System.Numerics.Vector3(v.X, v.Y, -v.Z);
    }

    static void AppendVector3Frame(List<float> dest, System.Numerics.Vector3 value)
    {
        dest.Add(value.X);
        dest.Add(value.Y);
        dest.Add(value.Z);
    }

    static void AppendQuaternionFrame(List<float> dest, System.Numerics.Quaternion value)
    {
        dest.Add(value.X);
        dest.Add(value.Y);
        dest.Add(value.Z);
        dest.Add(value.W);
    }

    static void AppendFloatFrame(List<float> dest, float[]? values, int expectedCount)
    {
        for (int i = 0; i < expectedCount; i++)
            dest.Add(values != null && i < values.Length ? values[i] : 0f);
    }

    static void EnsureNodeWeightsCapacity(GltfNodeBase node, int count)
    {
        if (count <= 0)
            return;

        if (node.InitialWeights.Length < count)
            Array.Resize(ref node.InitialWeights, count);

        if (node.Weights.Length < count)
            Array.Resize(ref node.Weights, count);
    }

    static AnimationTargetPath ConvertAnimationTargetPath(PropertyPath path)
    {
        return path switch
        {
            PropertyPath.translation => AnimationTargetPath.Translation,
            PropertyPath.rotation => AnimationTargetPath.Rotation,
            PropertyPath.scale => AnimationTargetPath.Scale,
            PropertyPath.weights => AnimationTargetPath.Weights,
            _ => AnimationTargetPath.Unknown,
        };
    }

    static AnimationInterpolationMode ConvertInterpolationMode(SharpGLTF.Schema2.AnimationInterpolationMode interpolation)
    {
        return interpolation switch
        {
            SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationInterpolationMode.Step,
            SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationInterpolationMode.CubicSpline,
            _ => AnimationInterpolationMode.Linear,
        };
    }
}
