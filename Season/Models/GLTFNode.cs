// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Season.Models;

public class GltfNodeBase
{
    public string Name;
    public int LogicalIndex = -1; // Stores the original SharpGLTF node LogicalIndex.
    //public System.Numerics.Matrix4x4 LocalTransform = System.Numerics.Matrix4x4.Identity;

    // Animation-related data.
    // The original matrix field was removed in favor of storing TRS components.
    public System.Numerics.Vector3 Translation = System.Numerics.Vector3.Zero;
    public System.Numerics.Quaternion Rotation = System.Numerics.Quaternion.Identity;
    public System.Numerics.Vector3 Scale = System.Numerics.Vector3.One;

    // Preserve the initial TRS values loaded from the GLTF file.
    // These values are used so that nodes without animation channels keep their initial state,
    // and they also support potential animation blending or reset in the future.
    public System.Numerics.Vector3 InitialTranslation = System.Numerics.Vector3.Zero;
    public System.Numerics.Quaternion InitialRotation = System.Numerics.Quaternion.Identity;
    public System.Numerics.Vector3 InitialScale = System.Numerics.Vector3.One;
    public float[] InitialWeights = Array.Empty<float>();
    public float[] Weights = Array.Empty<float>();
    public uint WeightsVersion;

    // Computed transform matrix.
    public System.Numerics.Matrix4x4 LocalTransform
    {
        get
        {
            // Standard DirectX matrix order: S x R x T, meaning scale, then rotate, then translate.
            // Note that System.Numerics uses row-major order and matrix multiplication runs left to right.
            return System.Numerics.Matrix4x4.CreateScale(Scale) *
                   System.Numerics.Matrix4x4.CreateFromQuaternion(Rotation) *
                   System.Numerics.Matrix4x4.CreateTranslation(Translation);
        }
    }

    public System.Numerics.Matrix4x4 WorldTransform = System.Numerics.Matrix4x4.Identity;

    public List<GltfNodeBase> Children = new List<GltfNodeBase>();

    public Mesh Mesh; // SharpGLTF Mesh object.

    /// <summary>
    /// Compact mesh list for picking, used by the v2 picking validation path.
    /// See <see cref="PickMesh"/>.
    /// These meshes are built by GltfAsset.ProcessMesh during loading and stay immutable afterward.
    /// Platform code shares them by reference when cloning node trees, so no deep copy is needed.
    /// Nodes without meshes keep an empty list.
    /// </summary>
    public List<PickMesh> PickMeshes = new List<PickMesh>();

    // Skin-related data.
    public GLTFSkin Skin;
    public bool IsJoint;
    public int JointIndex = -1;
}

public class GLTFSkin
{
    public string Name;
    public List<GltfNodeBase> Joints = new List<GltfNodeBase>();
    public List<System.Numerics.Matrix4x4> InverseBindMatrices = new List<System.Numerics.Matrix4x4>();
    public GltfNodeBase SkeletonRoot;
    public GltfNodeBase BindNode;
}

public sealed class GLTFMorphTarget
{
    public System.Numerics.Vector3[] PositionDeltas = Array.Empty<System.Numerics.Vector3>();
    public System.Numerics.Vector3[] NormalDeltas = Array.Empty<System.Numerics.Vector3>();
    public System.Numerics.Vector3[] TangentDeltas = Array.Empty<System.Numerics.Vector3>();
}

internal class GLTFAnimation
{
    public string Name;
    public List<AnimationChannel> Channels = new List<AnimationChannel>();
    public List<AnimationSampler> Samplers = new List<AnimationSampler>();
}

internal enum AnimationTargetPath
{
    Unknown = 0,
    Translation,
    Rotation,
    Scale,
    Weights,
}

internal enum AnimationInterpolationMode
{
    Step = 0,
    Linear,
    CubicSpline,
}

internal class AnimationChannel
{
    public AnimationChannelTarget Target;
    public AnimationSampler Sampler;
}

internal class AnimationChannelTarget
{
    public GltfNodeBase Node;
    public AnimationTargetPath Path;
}

internal class AnimationSampler
{
    public List<float> Inputs = new List<float>();
    public List<float> Values = new List<float>();
    public List<float>? InTangents { get; set; }
    public List<float>? OutTangents { get; set; }
    public int OutputElementCount { get; set; } = 4;
    public AnimationInterpolationMode Interpolation { get; set; } = AnimationInterpolationMode.Linear;

    // Keyframe lookup hint: stores the interval index hit last time so monotonic playback
    // can hit in O(1). See FindKeyFrameIndex.
    public int LastKeyIndexHint;

    public int KeyframeCount => Inputs.Count;

    public void AddValue(params float[] values)
    {
        if (values == null || values.Length == 0)
            return;

        Values.AddRange(values);
    }

    public System.Numerics.Vector4 GetValueVector4(int keyFrameIndex)
    {
        if (OutputElementCount <= 0)
            return System.Numerics.Vector4.Zero;

        int offset = keyFrameIndex * OutputElementCount;
        if (offset < 0 || offset >= Values.Count)
            return System.Numerics.Vector4.Zero;

        float x = Values[offset];
        float y = OutputElementCount > 1 && offset + 1 < Values.Count ? Values[offset + 1] : 0f;
        float z = OutputElementCount > 2 && offset + 2 < Values.Count ? Values[offset + 2] : 0f;
        float w = OutputElementCount > 3 && offset + 3 < Values.Count ? Values[offset + 3] : 0f;
        return new System.Numerics.Vector4(x, y, z, w);
    }

    public float[] GetValueArray(int keyFrameIndex)
    {
        return GetFrameArray(Values, keyFrameIndex);
    }

    public float[] GetInTangentArray(int keyFrameIndex)
    {
        return GetFrameArray(InTangents, keyFrameIndex);
    }

    public float[] GetOutTangentArray(int keyFrameIndex)
    {
        return GetFrameArray(OutTangents, keyFrameIndex);
    }

    float[] GetFrameArray(List<float>? source, int keyFrameIndex)
    {
        if (OutputElementCount <= 0)
            return Array.Empty<float>();

        var result = new float[OutputElementCount];
        if (source == null)
            return result;

        int offset = keyFrameIndex * OutputElementCount;
        if (offset < 0 || offset >= source.Count)
            return result;

        int count = Math.Min(OutputElementCount, source.Count - offset);
        if (count > 0)
            source.CopyTo(offset, result, 0, count);
        return result;
    }
}

