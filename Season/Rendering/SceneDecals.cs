// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Collections.ObjectModel;
using System.Numerics;

namespace Season.Rendering;

/// <summary>
/// D1 scene decal registry, isomorphic to <see cref="SceneFog"/>: the authoring layer only adds and removes, and the render layer collects on demand each frame.
///
/// The cap is the limit for "drawing each one individually on screen", not a memory limit: each decal is a small draw of 36 vertices, with the index passed via a per-draw constant.
/// Escaping this number would require a texture atlas + instancing, which is an optimization after D1 and not part of the first version.
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
            throw new InvalidOperationException($"Each scene supports at most {MaxActiveDecals} active decals.");
    }

    public void Add(DecalVolume decal)
    {
        ArgumentNullException.ThrowIfNull(decal);
        if (ReferenceEquals(decal.Owner, this)) return;
        if (decal.Owner != null)
            throw new InvalidOperationException("The same decal cannot be registered to multiple scenes at once.");
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

    /// <summary>CPU-side projection-box containment test, consistent with the shader's normalized local coordinate definition; used for self-checks during placement and for tests.</summary>
    public static bool Contains(in DecalVolume decal, Vector3 worldPosition)
    {
        var local = Vector3.Transform(worldPosition, decal.WorldToLocalMatrix);
        return MathF.Abs(local.X) <= 1f && MathF.Abs(local.Y) <= 1f && MathF.Abs(local.Z) <= 1f;
    }
}
