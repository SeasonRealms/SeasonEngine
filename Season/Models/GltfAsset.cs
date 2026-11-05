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
    internal Action<MeshPrimitive, GltfNodeBase, Model> ProcessPrimitiveCallback;

    // The skins set does not change after loading, so cache the first collected result
    // to avoid per-frame new List allocations and Contains scans.
    private List<GLTFSkin> _allSkinsCache;

    /// <summary>
    /// P1: the load-time clip bake, or null when this asset stays on the CPU route. Four conditions decide it, all of them
    /// properties of the model rather than of the device: the backend cannot bind a storage buffer into the vertex stage, the model
    /// has no clips or no skin, some animated node is not a joint, or the tables would exceed the size ceiling. See
    /// <see cref="InstanceSkinBake"/> for the full rule set. Built once by the platform's LoadInstancedModel.
    /// </summary>
    internal InstanceSkinBake SkinBake;

    /// <summary>
    /// True while <see cref="gltfNodes"/>' world transforms and the bone palette describe a frame older than the one being drawn.
    /// Only the single-model GPU route sets it: that route advances the clock and lets the kernel produce the palette, so the CPU
    /// copies stay wherever the load-time bake left them, which is the rest pose.
    ///
    /// Picking is the one consumer that cannot ask the GPU for a pose - it needs node worlds and unposed vertices on the CPU - and
    /// also the one consumer where reporting the rest pose would be visibly wrong rather than merely conservative. It calls
    /// <see cref="EnsureCpuPose"/> instead of being handed a stale answer.
    /// </summary>
    internal bool CpuPoseStale;

    /// <summary>
    /// True once <see cref="EnsureCpuPose"/> has evaluated the tree while the GPU route owned it. The route multiplies each
    /// primitive by its node world, and the kernel's palette is relative to the rest world; leaving an evaluated pose there would
    /// make the two factors disagree on the next frame, which reads as the skin being applied twice. Kept apart from
    /// <see cref="CpuPoseStale"/> because the two are consumed by different parties at different times.
    /// </summary>
    internal bool CpuPoseDisturbed;

    /// <summary>
    /// Brings the node tree and the bone palette up to the frame the GPU route is drawing, for the CPU consumers that cannot read
    /// the kernel's output. Free when they are already current - the CPU route's normal state is one flag read - and one
    /// evaluation when they are not.
    /// </summary>
    internal void EnsureCpuPose()
    {
        if (!CpuPoseStale) return;

        CpuPoseStale = false;
        CpuPoseDisturbed = true;

        var player = _animationPlayer;
        player.Evaluate(player.CurrentAnimationIndex, player.AnimationTime, gltfNodes);
        player.UpdateBoneMatrices(GetAllSkins());
    }

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

    public virtual void Load(Season.Controls.Model model)
    {
        Model = model;

        // Model assets stream straight from the package: user storage is consulted first only when
        // a real file exists there (user-imported content), and packaged assets are never copied
        // into local storage any more.
        StorageService.TryGetStream(StorageService.DirectoryBase, model.Name, out Stream stream, out _);

        if (stream == null)
            stream = StorageService.LoadStream(model.Name);

        ModelRoot glb;
        using (stream)
        {
            glb = ModelRoot.ReadGLB(stream, new ReadSettings() { Validation = ValidationMode.Skip });
        }
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
            ProcessNode(node, System.Numerics.Matrix4x4.Identity, model);
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

    internal void ProcessNode(SharpGLTF.Schema2.Node node, System.Numerics.Matrix4x4 parentWorldMatrix, Season.Controls.Model model, GltfNodeBase parentGltfNode = null)
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
            ProcessMesh(node.Mesh, gltfNode, model);
        }

        // Recursively process child nodes, passing the current node as parent.
        foreach (var childNode in node.VisualChildren)
        {
            ProcessNode(childNode, gltfNode.WorldTransform, model, gltfNode);
        }

        gltfNodes.Add(gltfNode);
    }

    void ProcessMesh(Mesh mesh, GltfNodeBase node, Model model)
    {
        for (var primIndex = 0; primIndex < mesh.Primitives.Count; primIndex++)
        {
            var primitive = mesh.Primitives[primIndex];

            ProcessPrimitiveCallback?.Invoke(primitive, node, model);

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

    /// <summary>
    /// Stops playback and pins the pose to the clip's first sampled frame.
    ///
    /// <para><see cref="SetAnimating"/> alone would leave whichever pose the last frame produced on screen,
    /// because every backend's per-frame update returns before sampling while the player is stopped. A boat
    /// that has finished rowing has to rest with its oars shipped, not freeze mid-stroke, so the pose is
    /// evaluated once here in the parsing domain; the backends then upload the same node state they would
    /// have uploaded for any other frame. Sampling at time 0 clamps to the first keyframe (the viking boat's
    /// clip starts at 1/24 s), which is the clip's authored start pose.</para>
    ///
    /// <para>Safe on an asset with no clips: the player resets the nodes to their rest state instead, which
    /// is also the correct "stopped" pose for a rigid model.</para>
    /// </summary>
    public void StopAndRest()
    {
        _animationPlayer.IsAnimating = false;
        _animationPlayer.AnimationTime = 0f;
        _animationPlayer.Evaluate(_animationPlayer.CurrentAnimationIndex, 0f, gltfNodes);

        // The evaluate above writes the tree, which is what the CPU route wants and what the GPU route has to be told about: that
        // route multiplies each primitive by the node world while its palette already carries the world, so a tree left posed
        // applies the skin twice. Flagged here rather than guarded by a route check because this type does not know which route the
        // control is on, and on the CPU route the flag costs one clear on a frame that would not have read it.
        CpuPoseDisturbed = true;
    }

    public void SetAnimationTime(float time)
    {
        _animationPlayer.AnimationTime = time;
    }

    /// <summary>
    /// Pins the playback clock to an absolute time without sampling anything: the next update advances from this value
    /// exactly as it would have advanced from its own, so the phase of frame N becomes a function of N alone.
    ///
    /// Only the per-pixel A/B harness reaches this, through <see cref="Season.Controls.Model.PinAnimationTime"/>. Playback
    /// time accumulates from the frame the asset finished its asynchronous load, and that frame differs per run, so two
    /// otherwise identical runs sample the same clip at permanently offset phases - measured as a 20677 px / worst 292
    /// band in the A/B captures, produced by a morph model that has nothing to do with the route under test.
    ///
    /// Returns false when the clip is not running: a stopped or finished player does not accumulate, so nothing there can
    /// drift between runs, and writing its clock would only hand a stale value to whatever plays it next.
    /// </summary>
    public bool PinAnimationTime(float time)
    {
        if (!_animationPlayer.IsAnimating || _animationPlayer.AnimationCount == 0)
            return false;

        _animationPlayer.AnimationTime = time;
        return true;
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

    /// <summary>
    /// 1-5 clause 12: a value that changes exactly when the skinned or morphed pose changes, used by the shadow-atlas reuse
    /// digest. Zero when the asset carries no clips, in which case the geometry is rigid and its bounds already describe it.
    ///
    /// The clock is folded in rather than an is-playing flag, because a paused clip is a legitimately static pose that should
    /// remain eligible for reuse, while a seek performed while paused still changes the pose and must be caught. Both fall out
    /// of digesting the time itself. The clip index joins it because switching clips can land on the same time value while
    /// producing an entirely different pose.
    ///
    /// P1: this key describes a single host, where the shared player's clock is the host's clock. The instanced batch
    /// (<see cref="Season.Controls.InstancedModel"/>) no longer reads it: every instance replays into the same player, so after
    /// per-instance throttling the clock belongs to whichever instance was evaluated last and no longer describes the batch.
    /// That path publishes <see cref="Season.Controls.InstancedModel.AnimationPoseGeneration"/> instead.
    /// </summary>
    public int ShadowPoseKey
        => _animationPlayer.AnimationCount == 0
            ? 0
            : BitConverter.SingleToInt32Bits(_animationPlayer.AnimationTime)
              ^ (_animationPlayer.CurrentAnimationIndex * 0x51ED2701);

    /// <summary>Clip sampling rate of <see cref="ComputeClipUnionBounds"/> on top of the keyframe times themselves.</summary>
    const float ClipBoundsSampleRate = 30f;

    /// <summary>Upper bound on the poses sampled per clip, so a long baked clip cannot stall loading.</summary>
    const int ClipBoundsMaxSamples = 1024;

    /// <summary>
    /// A7: model-space (LH) box that covers <paramref name="rest"/> and every pose the asset's clips reach, for the GPU instance
    /// cull of InstancedModel. The kernel tests one box per instance transformed by the root instance world, and an instance it
    /// rejects is simply not drawn - unlike the CPU sphere test, which only decides whether a whole batch is submitted - so a
    /// box a swinging limb leaves turns into visible popping. <see cref="Season.Controls.Model.LocalBounds"/> only scales the
    /// rest box by AnimatedBoundsScale, which is a guess; this replays the clips instead.
    ///
    /// Each clip is evaluated at its keyframe times plus a fixed-rate grid (capped at <see cref="ClipBoundsMaxSamples"/>), and
    /// each pose contributes the same surface the renderer draws: a rigid PickMesh's local box through its node world, and a
    /// skinned one's per-joint boxes through IBM × joint world × inv(bind node world) × node world - the bone palette the vertex
    /// stage applies followed by the stream's node world. A skinned vertex is a weighted average of its joints' transforms, so it
    /// always lies inside the union of those transformed boxes and the result stays conservative without visiting a vertex per
    /// sample. Morph displacement is not replayed (PickMesh carries no deltas); the rest box joins the union, and it is already
    /// the AnimatedBoundsScale-expanded one, which keeps the margin morph targets had before.
    ///
    /// Loading-time only. The node pose and the player's clip/clock are restored afterwards, so the asset is left as found.
    /// </summary>
    internal Season.Rendering.Bounds3D ComputeClipUnionBounds(Season.Rendering.Bounds3D rest)
    {
        if (_animations.Count == 0 || gltfNodes.Count == 0)
            return rest;

        // Per-mesh local boxes, computed once: rigid meshes get one, skinned meshes one per referenced joint plus one for the
        // vertices with no weight at all, which the vertex stage leaves unskinned in node space.
        var rigid = new List<(GltfNodeBase Node, Season.Rendering.Bounds3D Box)>();
        var skinned = new List<(GltfNodeBase Node, GLTFSkin Skin, int Joint, Season.Rendering.Bounds3D Box)>();
        foreach (var node in gltfNodes)
        {
            foreach (var mesh in node.PickMeshes)
            {
                var positions = mesh.Positions;
                if (positions.Length == 0)
                    continue;

                var skin = node.Skin;
                if (!mesh.IsSkinned || skin == null || skin.Joints.Count == 0)
                {
                    rigid.Add((node, BoxOf(positions)));
                    continue;
                }

                int jointCount = skin.Joints.Count;
                var mins = new System.Numerics.Vector3[jointCount + 1];
                var maxs = new System.Numerics.Vector3[jointCount + 1];
                var used = new bool[jointCount + 1];
                int skinnedVertices = Math.Min(positions.Length, Math.Min(mesh.Joints.Length, mesh.Weights.Length));
                for (int v = 0; v < positions.Length; v++)
                {
                    var p = positions[v];
                    var w = v < skinnedVertices ? mesh.Weights[v] : System.Numerics.Vector4.Zero;
                    if (w.X + w.Y + w.Z + w.W <= 0f)
                    {
                        Grow(jointCount, p);
                        continue;
                    }
                    var j = mesh.Joints[v];
                    if (w.X > 0f) GrowJoint((int)j.X, p);
                    if (w.Y > 0f) GrowJoint((int)j.Y, p);
                    if (w.Z > 0f) GrowJoint((int)j.Z, p);
                    if (w.W > 0f) GrowJoint((int)j.W, p);
                }

                for (int k = 0; k <= jointCount; k++)
                {
                    if (!used[k])
                        continue;
                    var box = Season.Rendering.Bounds3D.FromMinMax(mins[k], maxs[k]);
                    if (k == jointCount)
                        rigid.Add((node, box));
                    else
                        skinned.Add((node, skin, k, box));
                }

                // A joint index past the skin's joints would read garbage on the GPU too; it cannot be placed, so it is dropped.
                void GrowJoint(int k, System.Numerics.Vector3 p)
                {
                    if ((uint)k < (uint)jointCount)
                        Grow(k, p);
                }

                void Grow(int k, System.Numerics.Vector3 p)
                {
                    if (!used[k]) { mins[k] = p; maxs[k] = p; used[k] = true; }
                    else
                    {
                        mins[k] = System.Numerics.Vector3.Min(mins[k], p);
                        maxs[k] = System.Numerics.Vector3.Max(maxs[k], p);
                    }
                }
            }
        }

        if (rigid.Count == 0 && skinned.Count == 0)
            return rest;

        // Snapshot what sampling overwrites: every node's TRS and weights, and the player's clip, clock and loop flag.
        var player = _animationPlayer;
        int savedClip = player.CurrentAnimationIndex;
        float savedTime = player.AnimationTime;
        bool savedAnimating = player.IsAnimating;
        bool savedLoop = player.Loop;
        var savedTranslation = new System.Numerics.Vector3[gltfNodes.Count];
        var savedRotation = new System.Numerics.Quaternion[gltfNodes.Count];
        var savedScale = new System.Numerics.Vector3[gltfNodes.Count];
        var savedWeights = new float[gltfNodes.Count][];
        for (int n = 0; n < gltfNodes.Count; n++)
        {
            var node = gltfNodes[n];
            savedTranslation[n] = node.Translation;
            savedRotation[n] = node.Rotation;
            savedScale[n] = node.Scale;
            savedWeights[n] = node.Weights.Length == 0 ? Array.Empty<float>() : (float[])node.Weights.Clone();
        }

        var result = rest;
        bool hasRest = rest.Extents != System.Numerics.Vector3.Zero;
        var times = new List<float>();
        try
        {
            // Clamp rather than wrap: with wrapping, the sample at exactly the clip end would evaluate the first frame instead.
            player.Loop = false;
            for (int clip = 0; clip < _animations.Count; clip++)
            {
                CollectClipSampleTimes(_animations[clip], times);
                foreach (float t in times)
                {
                    player.Evaluate(clip, t, gltfNodes);
                    foreach (var (node, box) in rigid)
                        Accumulate(box.Transform(node.WorldTransform));
                    foreach (var (node, skin, joint, box) in skinned)
                    {
                        var inverseBind = System.Numerics.Matrix4x4.Identity;
                        if (skin.BindNode != null && !System.Numerics.Matrix4x4.Invert(skin.BindNode.WorldTransform, out inverseBind))
                            inverseBind = System.Numerics.Matrix4x4.Identity;
                        var ibm = joint < skin.InverseBindMatrices.Count ? skin.InverseBindMatrices[joint] : System.Numerics.Matrix4x4.Identity;
                        var replay = ibm * skin.Joints[joint].WorldTransform * inverseBind * node.WorldTransform;
                        if (float.IsNaN(replay.M11) || float.IsInfinity(replay.M11))
                            continue;
                        Accumulate(box.Transform(replay));
                    }
                }
            }
        }
        finally
        {
            player.Loop = savedLoop;
            if (savedClip != player.CurrentAnimationIndex)
                player.Play(savedClip);
            player.AnimationTime = savedTime;
            player.IsAnimating = savedAnimating;
            for (int n = 0; n < gltfNodes.Count; n++)
            {
                var node = gltfNodes[n];
                node.Translation = savedTranslation[n];
                node.Rotation = savedRotation[n];
                node.Scale = savedScale[n];
                node.Weights = savedWeights[n];
            }
            player.UpdateAllNodeTransforms(gltfNodes);
        }

        return result;

        void Accumulate(Season.Rendering.Bounds3D box)
        {
            if (!hasRest) { result = box; hasRest = true; }
            else result = Season.Rendering.Bounds3D.Union(result, box);
        }

        static Season.Rendering.Bounds3D BoxOf(System.Numerics.Vector3[] positions)
        {
            var min = positions[0];
            var max = min;
            for (int i = 1; i < positions.Length; i++)
            {
                min = System.Numerics.Vector3.Min(min, positions[i]);
                max = System.Numerics.Vector3.Max(max, positions[i]);
            }
            return Season.Rendering.Bounds3D.FromMinMax(min, max);
        }
    }

    /// <summary>
    /// Sample times for one clip: every keyframe of every channel (where piecewise-linear tracks reach their extremes) plus a
    /// <see cref="ClipBoundsSampleRate"/> grid over the duration for the spline and slerp arcs between them, sorted, deduplicated,
    /// and thinned evenly down to <see cref="ClipBoundsMaxSamples"/>.
    /// </summary>
    static void CollectClipSampleTimes(GLTFAnimation animation, List<float> times)
    {
        times.Clear();
        float duration = 0f;
        foreach (var channel in animation.Channels)
        {
            var inputs = channel.Sampler?.Inputs;
            if (inputs == null)
                continue;
            foreach (float t in inputs)
            {
                times.Add(t);
                if (t > duration)
                    duration = t;
            }
        }

        int steps = (int)MathF.Ceiling(duration * ClipBoundsSampleRate);
        for (int s = 0; s <= steps; s++)
            times.Add(steps == 0 ? 0f : duration * s / steps);

        times.Sort();
        int write = 0;
        for (int i = 0; i < times.Count; i++)
        {
            if (write > 0 && times[i] - times[write - 1] <= 1e-5f)
                continue;
            times[write++] = times[i];
        }
        times.RemoveRange(write, times.Count - write);

        if (times.Count > ClipBoundsMaxSamples)
        {
            // Keep both ends and spread the rest evenly; the dropped samples are the dense ones, whose neighbours bound them closely.
            var thinned = new List<float>(ClipBoundsMaxSamples);
            for (int s = 0; s < ClipBoundsMaxSamples; s++)
                thinned.Add(times[(int)((long)s * (times.Count - 1) / (ClipBoundsMaxSamples - 1))]);
            times.Clear();
            times.AddRange(thinned);
        }
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
