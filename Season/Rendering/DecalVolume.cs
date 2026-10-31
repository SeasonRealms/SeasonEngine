// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// D1 屏幕空间贴花的混合语义。
///
/// 前向渲染下 SceneColor 里已经是「反照率 × 光照 + 高光」的结果，所以这里能提供的正确性各不相同，
/// 选择语义时按这张表判断，不要按「看起来像 UI」判断：
/// - <see cref="Modulate"/> 把贴花当成反照率的调制量。漫反射与 SH 间接光对反照率是线性的，所以这一步在物理上是成立的；
///   高光也会被一起压暗，这是接受的近似（湿痕、污渍、裂缝、苔藓变暗都靠它）。
/// - <see cref="Additive"/> 完全不受光照影响，只在「这块东西本身在发光」时正确（符文、魔法阵、池底焦散）。
///
/// 「写回反照率并按场景光照重新求值」被有意排除在 v1 之外：它需要直射光、CSM 阴影与 SH 环境光的完整求值副本，
/// 那是四个后端各一份的新着色器，收益只对涂鸦类贴花成立。真要做也是 D1 之后单独的一步，不是这里的第三个模式。
/// </summary>
public enum DecalMode
{
    Modulate,
    Additive,
}

/// <summary>
/// D1 贴花体积：一个带旋转的长方体投影盒，尺寸为各轴完整边长（米），旋转为单位四元数。
///
/// 与 <see cref="FogVolume"/> 同构：属性访问器负责有限值与范围校验，<see cref="SceneDecals.Add"/> 负责数量上限。
/// 贴花不在盒内做任何几何测试以外的判断，也不写速度目标 —— 它投影到的是本帧已经画完的几何上，
/// 那块几何自己带着正确的运动矢量，所以 TAA 不需要贴花额外提供历史信息。
/// </summary>
public sealed class DecalVolume
{
    internal SceneDecals? Owner;
    bool _enabled = true;
    Vector3 _position, _size = Vector3.One, _tint = Vector3.One;
    Quaternion _rotation = Quaternion.Identity;
    float _opacity = 1f, _angleFade = 0.35f, _edgeFade = 0.15f, _intensity = 1f, _roughness = 0.2f;
    DecalMode _mode;
    int _sortOrder;
    string? _texture;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (value) Owner?.EnsureActiveCapacity();
            _enabled = value;
        }
    }

    public DecalMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _mode = value;
        }
    }

    public Vector3 Position { get => _position; set => _position = FogMath.Finite(value); }

    public Vector3 Size
    {
        get => _size;
        set => _size = Vector3.Max(FogMath.Finite(value), new Vector3(0.001f));
    }

    public Quaternion Rotation
    {
        get => _rotation;
        set
        {
            FogMath.Finite(new Vector3(value.X, value.Y, value.Z));
            FogMath.Finite(value.W);
            float length = value.Length();
            if (!float.IsFinite(length) || length < 1e-6f)
                throw new ArgumentOutOfRangeException(nameof(value), "贴花旋转必须是有限的非零四元数。");
            _rotation = Quaternion.Normalize(value);
        }
    }

    /// <summary>线性 RGB 乘色；Modulate 下是「调制到的目标色」，Additive 下是自发光颜色。</summary>
    public Vector3 Tint { get => _tint; set => _tint = Vector3.Clamp(FogMath.Finite(value), Vector3.Zero, Vector3.One); }

    /// <summary>整体不透明度，与纹理 alpha 相乘。</summary>
    public float Opacity { get => _opacity; set => _opacity = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>掠射角淡出阈值（法线与盒轴夹角的余弦）。0 为硬边投影，用来消除侧壁上被拉长的贴花条纹。</summary>
    public float AngleFade { get => _angleFade; set => _angleFade = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>盒内边界向内的羽化比例，0 为硬切，1 为整盒羽化。</summary>
    public float EdgeFade { get => _edgeFade; set => _edgeFade = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>Additive 的强度倍数；Modulate 忽略。</summary>
    public float Intensity { get => _intensity; set => _intensity = Math.Max(0f, FogMath.Finite(value)); }

    /// <summary>s9：写进 SceneNormal.z 的目标粗糙度。只在 <see cref="AffectRoughness"/> 且本帧存在法线目标时生效。</summary>
    public float Roughness { get => _roughness; set => _roughness = Math.Clamp(FogMath.Finite(value), 0f, 1f); }

    /// <summary>是否为这块区域改写粗糙度（湿痕、水洼）。需要 SceneNormal，关闭时贴花只改颜色。</summary>
    public bool AffectRoughness { get; set; }

    /// <summary>同屏多个贴花重叠时的绘制次序，小值先画。相同值按注册顺序。</summary>
    public int SortOrder { get => _sortOrder; set => _sortOrder = value; }

    /// <summary>贴图名（后端纹理字典的注册名）。null 表示程序化贴花，用于不依赖资源的冒烟验证。</summary>
    public string? Texture
    {
        get => _texture;
        set => _texture = value is { Length: > 0 } ? value : null;
    }

    /// <summary>局部→世界矩阵（含缩放）；每帧打包时按当前 Position/Rotation/Size 重算，不缓存。</summary>
    internal Matrix4x4 WorldMatrix =>
        Matrix4x4.CreateScale(Size) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>世界→局部的归一化变换，贴花着色器用它把世界点折到 [-1,1]³ 里取 UV。</summary>
    internal Matrix4x4 WorldToLocalMatrix =>
        Matrix4x4.CreateScale(new Vector3(2f) / Size)
        * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(Rotation))
        * Matrix4x4.CreateTranslation(-Position);

    /// <summary>保守的世界 AABB（旋转盒的轴对齐包围），用于视锥剔除；比 OBB 精确测试多留一点误收，对剔除无害。</summary>
    internal void GetBounds(out Vector3 center, out Vector3 extents)
    {
        var half = Size * 0.5f;
        var rotation = Matrix4x4.CreateFromQuaternion(Rotation);
        extents = new Vector3(
            MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y + MathF.Abs(rotation.M31) * half.Z,
            MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y + MathF.Abs(rotation.M32) * half.Z,
            MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y + MathF.Abs(rotation.M33) * half.Z);
        center = Position;
    }
}
