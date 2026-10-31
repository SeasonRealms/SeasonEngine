// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// D1 场景贴花注册表，与 <see cref="SceneFog"/> 同构：作者层只管增删，渲染层每帧按需收集。
///
/// 上限是「同屏逐个体绘制」的上限，不是显存上限：每个贴花一次 36 顶点的小 draw，索引通过每 draw 常量传入。
/// 想摆脱这个数字需要纹理图集 + 实例化，那是 D1 之后的优化，不属于第一版。
/// </summary>
public sealed class SceneDecals
{
    public const int MaxActiveDecals = 64;

    readonly List<DecalVolume> _decals = new();
    readonly ReadOnlyCollection<DecalVolume> _view;

    public SceneDecals() => _view = _decals.AsReadOnly();

    public IReadOnlyList<DecalVolume> Decals => _view;

    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _decals.Count; i++) if (_decals[i].Enabled) count++;
            return count;
        }
    }

    internal void EnsureActiveCapacity()
    {
        if (ActiveCount >= MaxActiveDecals)
            throw new InvalidOperationException($"每个场景最多支持 {MaxActiveDecals} 个活动贴花。");
    }

    public void Add(DecalVolume decal)
    {
        ArgumentNullException.ThrowIfNull(decal);
        if (ReferenceEquals(decal.Owner, this)) return;
        if (decal.Owner != null)
            throw new InvalidOperationException("同一贴花不能同时注册到多个场景。");
        if (decal.Enabled) EnsureActiveCapacity();
        _decals.Add(decal);
        decal.Owner = this;
    }

    public bool Remove(DecalVolume decal)
    {
        if (!_decals.Remove(decal)) return false;
        decal.Owner = null;
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < _decals.Count; i++) _decals[i].Owner = null;
        _decals.Clear();
    }

    /// <summary>CPU 侧投影盒包含测试，与着色器的归一化局部坐标定义一致；用于放置期的自检与测试。</summary>
    public static bool Contains(in DecalVolume decal, Vector3 worldPosition)
    {
        var local = Vector3.Transform(worldPosition, decal.WorldToLocalMatrix);
        return MathF.Abs(local.X) <= 1f && MathF.Abs(local.Y) <= 1f && MathF.Abs(local.Z) <= 1f;
    }
}
