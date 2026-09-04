// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Models;

/// <summary>
/// GLTF animation playback engine responsible for time updates, keyframe lookup,
/// and TRS interpolation.
/// </summary>
internal class GLTFAnimationPlayer
{
    #region Fields

    private List<GLTFAnimation> _animations = new();
    private float _animationTime = 0f;
    private bool _isAnimating = true;
    private float _animationDuration = 0f;
    private int _currentAnimationIndex = 0;
    private bool _loop = true;

    // Bone-matrix cache as a flat array with fixed length equal to the total joint count.
    // It is overwritten in place every frame with zero allocations.
    private Matrix4x4[] _boneMatrices = Array.Empty<Matrix4x4>();

    #endregion

    #region Properties

    public bool IsAnimating { get => _isAnimating; set => _isAnimating = value; }
    public float AnimationTime { get => _animationTime; set => _animationTime = value; }
    public float Duration => _animationDuration;
    public bool Loop { get => _loop; set => _loop = value; }
    public int AnimationCount => _animations.Count;
    public int CurrentAnimationIndex => _currentAnimationIndex;
    public IReadOnlyList<Matrix4x4> BoneMatrices => _boneMatrices;

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the animation player.
    /// </summary>
    public void Initialize(List<GLTFAnimation> animations)
    {
        _animations = animations ?? new List<GLTFAnimation>();
        _animationTime = 0f;
        _currentAnimationIndex = 0;
        CalculateAnimationDuration();
    }

    /// <summary>
    /// Calculates the duration of the current animation.
    /// </summary>
    private void CalculateAnimationDuration()
    {
        _animationDuration = 0f;

        if (_animations.Count == 0 || _currentAnimationIndex >= _animations.Count)
            return;

        var animation = _animations[_currentAnimationIndex];
        foreach (var channel in animation.Channels)
        {
            if (channel.Sampler?.Inputs != null && channel.Sampler.Inputs.Count > 0)
            {
                float maxTime = channel.Sampler.Inputs[^1];
                if (maxTime > _animationDuration)
                    _animationDuration = maxTime;
            }
        }

        // Fall back to the default value if no valid time was found.
        if (_animationDuration <= 0f)
            _animationDuration = 1f;
    }

    #endregion

    #region Playback Control

    /// <summary>
    /// Plays an animation.
    /// Time is reset to zero only when switching to a different animation.
    /// Repeated requests to play the current animation do not reset time, otherwise callers that invoke Play every frame,
    /// such as PlayAnimation while a movement key is held down, would freeze the animation forever on the first frame pose.
    /// </summary>
    public void Play(int animationIndex = 0)
    {
        if (animationIndex >= 0 && animationIndex < _animations.Count)
        {
            if (animationIndex != _currentAnimationIndex)
                _animationTime = 0f;
            _currentAnimationIndex = animationIndex;
            CalculateAnimationDuration();
        }
        _isAnimating = true;
    }

    /// <summary>
    /// Plays an animation by name.
    /// </summary>
    public void Play(string animationName)
    {
        var index = _animations.FindIndex(a => a.Name == animationName);
        if (index >= 0)
            Play(index);
    }

    /// <summary>
    /// Stops animation playback.
    /// </summary>
    public void Stop()
    {
        _isAnimating = false;
    }

    /// <summary>
    /// Pauses animation playback.
    /// </summary>
    public void Pause()
    {
        _isAnimating = false;
    }

    /// <summary>
    /// Resumes animation playback.
    /// </summary>
    public void Resume()
    {
        _isAnimating = true;
    }

    /// <summary>
    /// Resets animation to its initial state.
    /// </summary>
    public void Reset(List<GltfNodeBase> allNodes)
    {
        _animationTime = 0f;
        ResetNodesToInitialState(allNodes);
    }

    #endregion

    #region Update

    /// <summary>
    /// Updates animation state.
    /// </summary>
    public void Update(float deltaTime, List<GltfNodeBase> allNodes)
    {
        if (!_isAnimating || _animations.Count == 0 || allNodes == null || allNodes.Count == 0)
            return;

        // Advance time.
        _animationTime += deltaTime;

        // Handle looping.
        if (_animationTime > _animationDuration)
        {
            if (_loop)
                _animationTime %= _animationDuration;
            else
            {
                _animationTime = _animationDuration;
                _isAnimating = false;
            }
        }

        // Apply the current animation.
        if (_currentAnimationIndex < _animations.Count)
        {
            ApplyAnimation(_animations[_currentAnimationIndex], _animationTime, allNodes);
        }

        // Update world transforms for all nodes.
        UpdateAllNodeTransforms(allNodes);
    }

    /// <summary>
    /// Evaluates the specified animation clip at an absolute time directly,
    /// without depending on the player's internally accumulated time.
    /// Used for per-instance animation sampling in InstancedModel.
    /// </summary>
    public void Evaluate(int animationIndex, float time, List<GltfNodeBase> allNodes)
    {
        if (allNodes == null || allNodes.Count == 0)
            return;

        if (_animations.Count == 0 || animationIndex < 0 || animationIndex >= _animations.Count)
        {
            ResetNodesToInitialState(allNodes);
            UpdateAllNodeTransforms(allNodes);
            return;
        }

        if (_currentAnimationIndex != animationIndex)
        {
            _currentAnimationIndex = animationIndex;
            CalculateAnimationDuration();
        }

        float evalTime = time;
        if (_animationDuration > 0f)
        {
            if (_loop)
            {
                evalTime %= _animationDuration;
                if (evalTime < 0f)
                    evalTime += _animationDuration;
            }
            else
            {
                evalTime = Math.Clamp(evalTime, 0f, _animationDuration);
            }
        }

        ApplyAnimation(_animations[_currentAnimationIndex], evalTime, allNodes);
        UpdateAllNodeTransforms(allNodes);
    }

    #endregion

    #region Animation Application

    /// <summary>
    /// Applies animation to nodes.
    /// </summary>
    private void ApplyAnimation(GLTFAnimation animation, float time, List<GltfNodeBase> allNodes)
    {
        // Reset all nodes to their initial state.
        ResetNodesToInitialState(allNodes);

        // Apply each animation channel.
        foreach (var channel in animation.Channels)
        {
            if (channel.Target?.Node == null || channel.Sampler == null)
            {
                continue;
            }

            var sampler = channel.Sampler;
            if (sampler.Inputs.Count == 0 || sampler.Values.Count == 0)
            {
                continue;
            }

            // Find the keyframe.
            int keyFrameIndex = FindKeyFrameIndex(sampler, time, out float factor);
            if (keyFrameIndex < 0 || keyFrameIndex >= sampler.KeyframeCount)
            {
                continue;
            }

            // Apply animation according to the target path type.
            switch (channel.Target.Path)
            {
                case AnimationTargetPath.Translation:
                    ApplyTranslation(channel.Target.Node, sampler, keyFrameIndex, factor);
                    break;
                case AnimationTargetPath.Rotation:
                    ApplyRotation(channel.Target.Node, sampler, keyFrameIndex, factor);
                    break;
                case AnimationTargetPath.Scale:
                    ApplyScale(channel.Target.Node, sampler, keyFrameIndex, factor);
                    break;
                case AnimationTargetPath.Weights:
                    ApplyWeights(channel.Target.Node, sampler, keyFrameIndex, factor);
                    break;
            }
        }
    }

    /// <summary>
    /// Resets all nodes to their initial TRS state.
    /// </summary>
    private void ResetNodesToInitialState(List<GltfNodeBase> allNodes)
    {
        foreach (var node in allNodes)
        {
            node.Translation = node.InitialTranslation;
            node.Rotation = node.InitialRotation;
            node.Scale = node.InitialScale;

            if (node.InitialWeights.Length == 0)
            {
                node.Weights = Array.Empty<float>();
            }
            else if (node.Weights.Length != node.InitialWeights.Length)
            {
                node.Weights = (float[])node.InitialWeights.Clone();
            }
            else
            {
                Array.Copy(node.InitialWeights, node.Weights, node.InitialWeights.Length);
            }
        }
    }

    #endregion

    #region TRS Interpolation

    /// <summary>
    /// Applies translation animation.
    /// </summary>
    private void ApplyTranslation(GltfNodeBase node, AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        node.Translation = SampleVector3(sampler, keyFrameIndex, factor);
    }

    /// <summary>
    /// Applies rotation animation.
    /// </summary>
    private void ApplyRotation(GltfNodeBase node, AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        node.Rotation = SampleQuaternion(sampler, keyFrameIndex, factor);
    }

    /// <summary>
    /// Applies scale animation.
    /// </summary>
    private void ApplyScale(GltfNodeBase node, AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        node.Scale = SampleVector3(sampler, keyFrameIndex, factor);
    }

    private void ApplyWeights(GltfNodeBase node, AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        var sampledWeights = SampleFloatArray(sampler, keyFrameIndex, factor);
        if (sampledWeights.Length == 0)
            return;

        if (node.Weights.Length != sampledWeights.Length)
            node.Weights = new float[sampledWeights.Length];

        Array.Copy(sampledWeights, node.Weights, sampledWeights.Length);
        node.WeightsVersion++;
    }

    /// <summary>
    /// Gets the next output value with safe boundary checks.
    /// </summary>
    private Vector4 GetNextOutput(AnimationSampler sampler, int keyFrameIndex)
    {
        return keyFrameIndex < sampler.KeyframeCount - 1
            ? sampler.GetValueVector4(keyFrameIndex + 1)
            : sampler.GetValueVector4(keyFrameIndex);
    }

    private int GetNextKeyFrameIndex(AnimationSampler sampler, int keyFrameIndex)
    {
        return keyFrameIndex < sampler.KeyframeCount - 1
            ? keyFrameIndex + 1
            : keyFrameIndex;
    }

    private Vector3 SampleVector3(AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        var current = sampler.GetValueVector4(keyFrameIndex);
        if (sampler.Interpolation == AnimationInterpolationMode.Step || keyFrameIndex >= sampler.KeyframeCount - 1)
            return new Vector3(current.X, current.Y, current.Z);

        var next = GetNextOutput(sampler, keyFrameIndex);
        if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
        {
            int nextIndex = GetNextKeyFrameIndex(sampler, keyFrameIndex);
            float dt = sampler.Inputs[nextIndex] - sampler.Inputs[keyFrameIndex];
            var outTangent = sampler.GetOutTangentArray(keyFrameIndex);
            var inTangent = sampler.GetInTangentArray(nextIndex);
            return EvaluateCubicVector3(
                new Vector3(current.X, current.Y, current.Z),
                new Vector3(next.X, next.Y, next.Z),
                ToVector3(outTangent),
                ToVector3(inTangent),
                dt,
                factor);
        }

        return Vector3.Lerp(new Vector3(current.X, current.Y, current.Z), new Vector3(next.X, next.Y, next.Z), factor);
    }

    private Quaternion SampleQuaternion(AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        var current = sampler.GetValueVector4(keyFrameIndex);
        var currentQuat = new Quaternion(current.X, current.Y, current.Z, current.W);
        if (sampler.Interpolation == AnimationInterpolationMode.Step || keyFrameIndex >= sampler.KeyframeCount - 1)
            return currentQuat;

        var next = GetNextOutput(sampler, keyFrameIndex);
        var nextQuat = new Quaternion(next.X, next.Y, next.Z, next.W);
        if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
        {
            int nextIndex = GetNextKeyFrameIndex(sampler, keyFrameIndex);
            float dt = sampler.Inputs[nextIndex] - sampler.Inputs[keyFrameIndex];
            var outTangent = ToVector4(sampler.GetOutTangentArray(keyFrameIndex));
            var inTangent = ToVector4(sampler.GetInTangentArray(nextIndex));
            var cubic = EvaluateCubicVector4(new Vector4(currentQuat.X, currentQuat.Y, currentQuat.Z, currentQuat.W),
                new Vector4(nextQuat.X, nextQuat.Y, nextQuat.Z, nextQuat.W),
                outTangent,
                inTangent,
                dt,
                factor);
            return Quaternion.Normalize(new Quaternion(cubic.X, cubic.Y, cubic.Z, cubic.W));
        }

        return Quaternion.Slerp(currentQuat, nextQuat, factor);
    }

    private float[] SampleFloatArray(AnimationSampler sampler, int keyFrameIndex, float factor)
    {
        var current = sampler.GetValueArray(keyFrameIndex);
        if (sampler.Interpolation == AnimationInterpolationMode.Step || keyFrameIndex >= sampler.KeyframeCount - 1)
            return current;

        var next = sampler.GetValueArray(GetNextKeyFrameIndex(sampler, keyFrameIndex));
        if (sampler.Interpolation == AnimationInterpolationMode.CubicSpline)
        {
            int nextIndex = GetNextKeyFrameIndex(sampler, keyFrameIndex);
            float dt = sampler.Inputs[nextIndex] - sampler.Inputs[keyFrameIndex];
            var outTangent = sampler.GetOutTangentArray(keyFrameIndex);
            var inTangent = sampler.GetInTangentArray(nextIndex);
            return EvaluateCubicFloatArray(current, next, outTangent, inTangent, dt, factor);
        }

        var result = new float[current.Length];
        for (int i = 0; i < current.Length; i++)
        {
            float nextValue = i < next.Length ? next[i] : 0f;
            result[i] = current[i] + (nextValue - current[i]) * factor;
        }

        return result;
    }

    private static Vector3 EvaluateCubicVector3(Vector3 p0, Vector3 p1, Vector3 m0, Vector3 m1, float dt, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return h00 * p0 + h10 * dt * m0 + h01 * p1 + h11 * dt * m1;
    }

    private static Vector4 EvaluateCubicVector4(Vector4 p0, Vector4 p1, Vector4 m0, Vector4 m1, float dt, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return h00 * p0 + h10 * dt * m0 + h01 * p1 + h11 * dt * m1;
    }

    private static float[] EvaluateCubicFloatArray(float[] p0, float[] p1, float[] m0, float[] m1, float dt, float t)
    {
        int count = Math.Max(Math.Max(p0.Length, p1.Length), Math.Max(m0.Length, m1.Length));
        var result = new float[count];
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        for (int i = 0; i < count; i++)
        {
            float v0 = i < p0.Length ? p0[i] : 0f;
            float v1 = i < p1.Length ? p1[i] : 0f;
            float tan0 = i < m0.Length ? m0[i] : 0f;
            float tan1 = i < m1.Length ? m1[i] : 0f;
            result[i] = h00 * v0 + h10 * dt * tan0 + h01 * v1 + h11 * dt * tan1;
        }

        return result;
    }

    private static Vector3 ToVector3(float[] values)
    {
        float x = values.Length > 0 ? values[0] : 0f;
        float y = values.Length > 1 ? values[1] : 0f;
        float z = values.Length > 2 ? values[2] : 0f;
        return new Vector3(x, y, z);
    }

    private static Vector4 ToVector4(float[] values)
    {
        float x = values.Length > 0 ? values[0] : 0f;
        float y = values.Length > 1 ? values[1] : 0f;
        float z = values.Length > 2 ? values[2] : 0f;
        float w = values.Length > 3 ? values[3] : 0f;
        return new Vector4(x, y, z, w);
    }

    #endregion

    #region Keyframe Search

    /// <summary>
    /// Finds the keyframe index and interpolation factor.
    /// It first tries the sampler's interval hint for O(1) lookup during monotonic playback,
    /// and falls back to binary search with O(log K) when the hint misses.
    /// When instanced multi-object sampling alternates on the same sampler and time is non-monotonic,
    /// the binary-search fallback handles it correctly.
    /// </summary>
    private int FindKeyFrameIndex(AnimationSampler sampler, float time, out float factor)
    {
        var inputs = sampler.Inputs;
        factor = 0f;

        // Boundary checks.
        if (inputs.Count == 0)
            return -1;

        if (time <= inputs[0])
            return 0;

        if (time >= inputs[^1])
            return inputs.Count - 1;

        // Interval test matches the original linear scan:
        // time >= inputs[i] && time < inputs[i + 1].
        int i = sampler.LastKeyIndexHint;
        if (i < 0 || i >= inputs.Count - 1 || time < inputs[i] || time >= inputs[i + 1])
        {
            if (i >= 0 && i + 2 <= inputs.Count - 1 && time >= inputs[i + 1] &&
                (i + 2 == inputs.Count - 1 || time < inputs[i + 2]))
            {
                // Monotonic forward step by one interval: jump directly into the next interval.
                i = i + 1;
            }
            else
            {
                // Binary search: find the largest i such that inputs[i] <= time,
                // with time already guaranteed to lie within (inputs[0], inputs[^1]).
                int lo = 0, hi = inputs.Count - 2;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (inputs[mid] <= time)
                        lo = mid;
                    else
                        hi = mid - 1;
                }
                i = lo;
            }
        }

        sampler.LastKeyIndexHint = i;

        // STEP interpolation does not need a factor.
        if (sampler.Interpolation == AnimationInterpolationMode.Step)
            return i;

        // Compute the interpolation factor for LINEAR interpolation.
        float t0 = inputs[i];
        float t1 = inputs[i + 1];
        factor = (time - t0) / (t1 - t0);
        return i;
    }

    #endregion

    #region Node Transform Update

    // Root-node cache: node hierarchy does not change after loading, so cache once per node-list reference
    // to remove the O(N^2) lookup cost each frame.
    // The same player may be alternately used with Model(gltfNodes) and InstancedModel(_workNodes),
    // so cache entries are keyed by list reference.
    private readonly Dictionary<List<GltfNodeBase>, List<GltfNodeBase>> _rootNodesCache = new();

    /// <summary>
    /// Gets the list of root nodes, meaning nodes without parents.
    /// Results are cached by node-list reference.
    /// </summary>
    public List<GltfNodeBase> GetRootNodes(List<GltfNodeBase> allNodes)
    {
        if (_rootNodesCache.TryGetValue(allNodes, out var roots))
            return roots;

        var childSet = new HashSet<GltfNodeBase>();
        foreach (var node in allNodes)
            foreach (var child in node.Children)
                childSet.Add(child);

        roots = new List<GltfNodeBase>();
        foreach (var node in allNodes)
        {
            if (!childSet.Contains(node))
                roots.Add(node);
        }

        _rootNodesCache[allNodes] = roots;
        return roots;
    }

    /// <summary>
    /// Updates world transform matrices for all nodes.
    /// </summary>
    public void UpdateAllNodeTransforms(List<GltfNodeBase> allNodes)
    {
        foreach (var rootNode in GetRootNodes(allNodes))
        {
            UpdateNodeWorldTransform(rootNode, null);
        }
    }

    /// <summary>
    /// Recursively updates a node's world transform.
    /// </summary>
    private void UpdateNodeWorldTransform(GltfNodeBase node, GltfNodeBase parent)
    {
        // Compute the world transform.
        // LocalTransform has already been derived from node properties.
        node.WorldTransform = parent != null
            ? node.LocalTransform * parent.WorldTransform
            : node.LocalTransform;

        // Recursively update child nodes.
        foreach (var child in node.Children)
        {
            UpdateNodeWorldTransform(child, node);
        }
    }

    #endregion

    #region Bone Matrix Calculation

    /// <summary>
    /// Updates bone matrices.
    /// Writes into the internal flat array and reallocates only when the total joint count changes.
    /// </summary>
    public void UpdateBoneMatrices(List<GLTFSkin> skins)
    {
        int total = 0;
        foreach (var skin in skins)
            total += skin.Joints.Count;

        if (_boneMatrices.Length != total)
            _boneMatrices = new Matrix4x4[total];

        int index = 0;
        foreach (var skin in skins)
        {
            var inverseMeshWorld = Matrix4x4.Identity;
            if (skin.BindNode != null && !Matrix4x4.Invert(skin.BindNode.WorldTransform, out inverseMeshWorld))
                inverseMeshWorld = Matrix4x4.Identity;

            for (int i = 0; i < skin.Joints.Count; i++)
            {
                var joint = skin.Joints[i];
                var inverseBindMatrix = skin.InverseBindMatrices[i];

                // Under the row-vector convention, the mesh world matrix is still multiplied later in the vertex shader.
                // So this first brings joint world space back into the current mesh-node space,
                // avoiding repeated application of shared ancestor transforms.
                var boneMatrix = inverseBindMatrix * joint.WorldTransform * inverseMeshWorld;

                // Validate matrix correctness.
                if (float.IsNaN(boneMatrix.M11) || float.IsInfinity(boneMatrix.M11))
                {
                    boneMatrix = Matrix4x4.Identity;
                }

                boneMatrix = Matrix4x4.Transpose(boneMatrix);
                _boneMatrices[index++] = boneMatrix;
            }
        }
    }

    /// <summary>
    /// Gets bone-matrix data for GPU use.
    /// Returns the internal shared buffer without copying.
    /// The next UpdateBoneMatrices call overwrites it in place, so callers must copy it synchronously
    /// within the current frame and must not hold the reference long-term.
    /// </summary>
    public Matrix4x4[] GetBoneMatricesArray()
    {
        return _boneMatrices;
    }

    #endregion

    #region Animation Info

    /// <summary>
    /// Gets the list of animation names.
    /// </summary>
    public List<string> GetAnimationNames()
    {
        return _animations.Select(a => a.Name).ToList();
    }

    /// <summary>
    /// Gets the list of animation-clip metadata, including name and duration.
    /// See <see cref="ModelAnimationInfo"/>.
    /// </summary>
    public List<ModelAnimationInfo> GetAnimations()
    {
        var result = new List<ModelAnimationInfo>(_animations.Count);
        foreach (var animation in _animations)
            result.Add(new ModelAnimationInfo(animation.Name, GetAnimationDuration(animation)));
        return result;
    }

    /// <summary>
    /// Clip duration equals the maximum final-keyframe time across all channel samplers.
    /// This uses the same basis as <see cref="CalculateAnimationDuration"/>,
    /// but does not apply its playback fallback of 1 second when no valid keyframes exist,
    /// so metadata returns 0 as-is.
    /// </summary>
    static float GetAnimationDuration(GLTFAnimation animation)
    {
        float duration = 0f;

        foreach (var channel in animation.Channels)
        {
            if (channel.Sampler?.Inputs != null && channel.Sampler.Inputs.Count > 0)
            {
                float maxTime = channel.Sampler.Inputs[^1];
                if (maxTime > duration)
                    duration = maxTime;
            }
        }

        return duration;
    }

    /// <summary>
    /// Gets the name of the current animation.
    /// </summary>
    public string GetCurrentAnimationName()
    {
        if (_currentAnimationIndex >= 0 && _currentAnimationIndex < _animations.Count)
            return _animations[_currentAnimationIndex].Name;
        return null;
    }

    #endregion
}
