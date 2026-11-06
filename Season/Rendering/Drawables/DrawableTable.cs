// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Diagnostics.CodeAnalysis;

namespace Season.Rendering.Drawables;

/// <summary>
/// The appearance layer's registry of loaded drawables. Each backend keeps one table per control family - Model,
/// InstancedModel, Mesh3D, InstancedMesh3D, Sprite3D - and reaches it from Load, Update, Draw and Dispose.
/// </summary>
/// <remarks>
/// The table owns the gate. Before this type, every call site spelled out its own <c>lock</c> around a raw dictionary,
/// which is where the four backends drifted: one file removes under the lock while another tests and removes in two
/// passes, one add re-checks for a racing load while another simply adds. The operations below are the whole vocabulary
/// the appearance layer needs, so a family behaves the same in every backend and the lock cannot be forgotten.
/// <para>
/// The gate is the map itself, so a table is its own lock: two families stay as independent as two dictionaries were,
/// and no code outside the table ever sees the map except through <see cref="Gate"/>.
/// </para>
/// </remarks>
internal sealed class DrawableTable<TKey, TNative>
    where TKey : notnull
    where TNative : class
{
    readonly Dictionary<TKey, TNative> _map = new();

    /// <summary>The lock object, for the few call sites that traverse the table; hold it around <see cref="GetEnumerator"/>
    /// and nothing else.</summary>
    public object Gate => _map;

    public int Count
    {
        get
        {
            lock (_map)
                return _map.Count;
        }
    }

    /// <summary>Whether a control's drawable is registered. Used by Load to return early on a second call.</summary>
    public bool Contains(TKey key)
    {
        lock (_map)
            return _map.ContainsKey(key);
    }

    /// <summary>Lookup that never throws: a missing entry hands back null, which is what Draw/Update/Dispose expect
    /// for a control that was never loaded.</summary>
    public bool TryGet(TKey key, [NotNullWhen(true)] out TNative? native)
    {
        lock (_map)
            return _map.TryGetValue(key, out native);
    }

    /// <summary>
    /// Registers a freshly loaded drawable. False means a racing load already registered one for this key; the caller
    /// drops the copy it just built, exactly as the previous "add if still absent" blocks did.
    /// </summary>
    public bool TryAdd(TKey key, TNative native)
    {
        lock (_map)
        {
            if (_map.ContainsKey(key))
                return false;

            _map.Add(key, native);
            return true;
        }
    }

    /// <summary>Unregisters and hands the drawable back, so the caller can queue its release behind a retire fence.</summary>
    public bool Remove(TKey key, [NotNullWhen(true)] out TNative? native)
    {
        lock (_map)
            return _map.Remove(key, out native);
    }

    /// <summary>Unregisters an entry whose release needs nothing from the drawable (the browser lets GC reclaim it).</summary>
    public bool Remove(TKey key)
    {
        lock (_map)
            return _map.Remove(key);
    }

    /// <summary>Runs <paramref name="action"/> on the entry while the table is locked, for call sites whose body has to
    /// stay inside the gate (update and draw, so a concurrent dispose cannot retire the drawable mid-call).
    /// Pass a <c>static</c> lambda and a value-tuple argument to keep the call allocation-free.</summary>
    public void With<TArg>(TKey key, TArg arg, Action<TNative, TArg> action)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var native))
                action(native, arg);
        }
    }

    /// <summary>The same call for bodies that need no state of their own.</summary>
    public void With(TKey key, Action<TNative> action)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var native))
                action(native);
        }
    }

    /// <summary>The value-returning form, for bodies that have to hand a result back out of the gate - a texture
    /// replacement that must upload only after the gate is released, for instance. Returns
    /// <paramref name="missing"/> when the key is absent.</summary>
    public TResult With<TArg, TResult>(TKey key, TArg arg, Func<TNative, TArg, TResult> func, TResult missing)
    {
        lock (_map)
            return _map.TryGetValue(key, out var native) ? func(native, arg) : missing;
    }

    /// <summary>Traversal under the gate. <paramref name="arg"/> carries the call's state so the callback can be static.</summary>
    public void ForEach<TArg>(TArg arg, Action<TKey, TNative, TArg> action)
    {
        lock (_map)
        {
            foreach (var pair in _map)
                action(pair.Key, pair.Value, arg);
        }
    }

    /// <summary>The map's enumerator. The caller must hold <see cref="Gate"/>: the appearance layer traverses the table
    /// while mutating each drawable, and a captured list would allocate every frame.</summary>
    public Dictionary<TKey, TNative>.Enumerator GetEnumerator() => _map.GetEnumerator();
}
