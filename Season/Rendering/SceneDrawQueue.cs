// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>A single draw item already captured by the backend; Draw must not re-read the Control or consume its one-shot override.</summary>
public interface ISceneDrawCommand
{
    void Draw();

    /// <summary>Hands back the resource references held at capture time; native resources must still follow the backend's fence-deferred release rule.</summary>
    void Release();
}

/// <summary>Hierarchical priority fixed at collection time. The default container adds no sort level; an outer explicit priority takes precedence over an inner one.</summary>
public readonly struct SceneDrawPriority : IComparable<SceneDrawPriority>
{
    readonly SceneDrawOrderContext? _owner;
    readonly ulong _generation;
    readonly int _offset, _count, _layer, _order;

    public SceneDrawPriority(int layer, int order)
    {
        _layer = layer; _order = order;
        _count = layer == 0 && order == 0 ? 0 : 1;
    }

    internal SceneDrawPriority(SceneDrawOrderContext owner, ulong generation, int offset, int count)
    {
        _owner = owner; _generation = generation; _offset = offset; _count = count;
    }

    internal int Count => _count;
    internal (int Layer, int Order) Component(int index)
        => index >= _count ? default : _owner == null ? (_layer, _order) : _owner.Component(_offset + index);

    internal void Validate()
    {
        if (_owner != null && _owner.Generation != _generation)
            throw new InvalidOperationException("Draw priority has expired and cannot be used across frames.");
    }

    public int CompareTo(SceneDrawPriority other)
    {
        Validate(); other.Validate();
        for (int i = 0; i < Math.Max(_count, other._count); i++)
        {
            var a = Component(i); var b = other.Component(i);
            int result = a.Layer.CompareTo(b.Layer);
            if (result == 0) result = a.Order.CompareTo(b.Order);
            if (result != 0) return result;
        }
        return 0;
    }
}

/// <summary>Priority storage shared across one frame. Each explicit container/leaf copies the short path, and primitives capture only a value handle; capacity is reused across frames.</summary>
public sealed class SceneDrawOrderContext
{
    readonly List<(int Layer, int Order)> _components = new();
    SceneDrawPriority _current;
    int _depth;
    ulong _scopeSequence, _scopeToken;
    bool _live;
    internal ulong Generation { get; private set; }
    public bool IsCollecting { get; private set; }
    public SceneDrawPriority Current => IsCollecting ? _current
        : throw new InvalidOperationException("Draw priority can only be captured during the collection phase.");
    internal (int Layer, int Order) Component(int index) => _components[index];

    public void BeginCollection()
    {
        if (_live) throw new InvalidOperationException("The previous frame's sort context has not been released yet.");
        _live = IsCollecting = true;
    }

    public Scope Push(int layer, int order)
    {
        if (!IsCollecting) throw new InvalidOperationException("Not currently in the draw collection phase.");
        var previous = _current;
        if (layer != 0 || order != 0)
        {
            int offset = _components.Count;
            for (int i = 0; i < previous.Count; i++) _components.Add(previous.Component(i));
            _components.Add((layer, order));
            _current = new SceneDrawPriority(this, Generation, offset, previous.Count + 1);
        }
        ulong parentToken = _scopeToken;
        _scopeToken = ++_scopeSequence;
        return new Scope(this, Generation, ++_depth, previous, _scopeToken, parentToken);
    }

    public void Seal()
    {
        if (!IsCollecting || _depth != 0)
            throw new InvalidOperationException("The draw scope has not ended or the collection phase has already ended.");
        IsCollecting = false;
    }

    /// <summary>Call after the queue is released; the exception path must reset too, invalidating every old handle.</summary>
    public void Reset()
    {
        Generation++;
        _current = default; _depth = 0;
        _scopeToken = _scopeSequence = 0;
        IsCollecting = _live = false;
        _components.Clear();
    }

    public readonly struct Scope : IDisposable
    {
        readonly SceneDrawOrderContext? _owner;
        readonly ulong _generation;
        readonly int _depth;
        readonly SceneDrawPriority _previous;
        readonly ulong _token, _parentToken;
        internal Scope(SceneDrawOrderContext owner, ulong generation, int depth, SceneDrawPriority previous,
            ulong token, ulong parentToken)
        {
            _owner = owner; _generation = generation; _depth = depth; _previous = previous;
            _token = token; _parentToken = parentToken;
        }
        public void Dispose()
        {
            if (_owner == null) return;
            if (!_owner.IsCollecting || _owner.Generation != _generation || _owner._depth != _depth || _owner._scopeToken != _token)
                throw new InvalidOperationException("A draw scope must end exactly once in nesting order.");
            _owner._current = _previous;
            _owner._scopeToken = _parentToken;
            _owner._depth--;
        }
    }
}

/// <summary>Reuse queue classified by primitive. T stores this frame's bindings, material, instance and skinning snapshot, but not a delegate that defers a Control.Draw call.</summary>
public sealed class SceneDrawQueue<T> where T : struct, ISceneDrawCommand
{
    readonly List<Entry> _opaque = new();
    readonly List<Entry> _transparent = new();
    int _sequence;
    bool _collecting, _sealed, _opaqueDrawn, _transparentDrawn;

    readonly record struct Entry(T Command, SceneDrawPriority Priority, float Depth, int Sequence);

    sealed class EntryComparer(bool transparent) : IComparer<Entry>
    {
        public int Compare(Entry a, Entry b)
        {
            int value = a.Priority.CompareTo(b.Priority);
            if (value == 0 && transparent) value = b.Depth.CompareTo(a.Depth);
            return value != 0 ? value : a.Sequence.CompareTo(b.Sequence);
        }
    }

    static readonly EntryComparer OpaqueComparer = new(false), TransparentComparer = new(true);
    public int OpaqueCount => _opaque.Count;
    public int TransparentCount => _transparent.Count;

    public void BeginCollection()
    {
        if (_collecting || _sealed || _opaque.Count != 0 || _transparent.Count != 0)
            throw new InvalidOperationException("The previous frame's draw items have not been released yet.");
        _sequence = 0;
        _opaqueDrawn = _transparentDrawn = false;
        _collecting = true;
    }

    /// <summary>After a successful add, the queue holds the command's resource references; on failure the caller releases them.</summary>
    public void Add(in T command, bool transparent, int layer, int order, float depth)
        => Add(command, transparent, new SceneDrawPriority(layer, order), depth);

    public void Add(in T command, bool transparent, SceneDrawPriority priority, float depth)
    {
        if (!_collecting) throw new InvalidOperationException("Draw items can only be added during the scene collection phase.");
        if (!float.IsFinite(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        priority.Validate();
        var item = new Entry(command, priority, depth, _sequence++);
        (transparent ? _transparent : _opaque).Add(item);
    }

    public void Seal()
    {
        if (!_collecting) throw new InvalidOperationException("Not currently in the scene collection phase.");
        for (int i = 0; i < _opaque.Count; i++) _opaque[i].Priority.Validate();
        for (int i = 0; i < _transparent.Count; i++) _transparent[i].Priority.Validate();
        _collecting = false;
        _sealed = true;
        _opaque.Sort(OpaqueComparer);
        _transparent.Sort(TransparentComparer);
    }

    public void DrawOpaque()
    {
        ConsumeOpaque();
        for (int i = 0; i < _opaque.Count; i++) _opaque[i].Command.Draw();
    }

    /// <summary>
    /// Consumes the opaque bucket without submitting a single draw. The cost-table scenarios that switch the geometry stage
    /// off still collect (the transparent bucket and the pass structure must stay identical), so the queue has to hand out its
    /// one opaque consumption without replaying; the transparent stage still follows it exactly once.
    /// </summary>
    public void DiscardOpaque() => ConsumeOpaque();

    void ConsumeOpaque()
    {
        if (!_sealed || _opaqueDrawn) throw new InvalidOperationException("The opaque queue is not sealed or has already been consumed.");
        _opaqueDrawn = true;
    }

    public void DrawTransparent()
    {
        if (!_sealed || !_opaqueDrawn || _transparentDrawn)
            throw new InvalidOperationException("The transparent queue must be consumed exactly once after the opaque queue.");
        _transparentDrawn = true;
        for (int i = 0; i < _transparent.Count; i++) _transparent[i].Command.Draw();
    }

    /// <summary>
    /// Reads one item from the transparent queue in final draw order, without drawing it.
    /// The backend needs to fill in each item's pass-specific resource bindings (for example the fog sampling descriptor) inside
    /// the transparent pass before it can submit the draw, so there must be a traversal entry that does not trigger Draw.
    /// </summary>
    public T TransparentAt(int index)
    {
        if (!_sealed) throw new InvalidOperationException("The transparent queue is not sealed yet.");
        if ((uint)index >= (uint)_transparent.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _transparent[index].Command;
    }

    /// <summary>Must be called in a finally block for both normal and exceptional frames; list capacity is kept while every resource reference is cleared.</summary>
    public void Release()
    {
        Exception? failure = null;
        ReleaseEntries(_opaque, ref failure);
        ReleaseEntries(_transparent, ref failure);
        _collecting = _sealed = false;
        _opaqueDrawn = _transparentDrawn = false;
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static void ReleaseEntries(List<Entry> items, ref Exception? failure)
    {
        for (int i = 0; i < items.Count; i++)
        {
            try { items[i].Command.Release(); }
            catch (Exception ex) { failure ??= ex; }
        }
        items.Clear();
    }
}

/// <summary>Only a backend that fully implements all scene draw paths implements this interface. No GPU draw may be submitted during collection.</summary>
public interface ISegmentedSceneGraphics
{
    void BeginSceneCollection();
    void SealSceneCollection();
    void DrawCollectedOpaque();
    /// <summary>Consumes the collected opaque bucket without replaying it (the geometry-off measurement row). Must still be issued in the SceneOpaque pass and exactly once, before the transparent stage.</summary>
    void DiscardCollectedOpaque();
    void DrawCollectedTransparent(bool sampleFog);
    void ReleaseSceneCollection();
}

/// <summary>The color source and AO-consumption flag are published only after a successful composite; on failure the transparent stage keeps using the original scene attachment.</summary>
public readonly record struct FogCompositeResult(bool Success, bool AoApplied);

/// <summary>Independent composite binding that does not overwrite the surface lighting buffer; ApplyAo can only come from this frame's ready and unconsumed AO.</summary>
public readonly record struct FogCompositeInputs(RenderTarget Color, RenderTarget Depth, StorageBuffer FrameBuffer,
    string IntegratedVolume, string LocalVolume, ComputeResourceRef Ao, float AoIntensity, bool ApplyAo);

/// <summary>Prepare resolves this frame's resources outside the pass; Draw consumes them only once inside the independent HDR FogComposite pass.</summary>
public abstract class FogCompositePipeline : IDisposable
{
    public abstract bool Prepare(in FogCompositeInputs inputs, RenderTarget destination);
    public abstract FogCompositeResult Draw();
    public abstract void Dispose();
}

/// <summary>P0 SSR composite binding: the reflection delta texture name + which scene color it is added into.</summary>
public readonly record struct SsrCompositeInputs(RenderTarget Color, string ReflectionTexture);

/// <summary>
/// P0: the backend entry that adds the reflection delta into the scene color. Unlike the fog composite, it opens no new target and
/// publishes no color source; the delta is computed directly into its final value by the <see cref="Effects.SsrEffect"/> kernel
/// (including (1-r)^2, the reflection weight and the confidence), so all that is left here is a single full-screen additive draw -
/// the weight is not recomputed here, otherwise the Fresnel and roughness conventions in the two places would inevitably diverge.
/// </summary>
public abstract class SsrCompositePipeline : IDisposable
{
    /// <summary>Resolves this frame's resources outside the pass; false means this frame does not composite and the scene stays as is.</summary>
    public abstract bool Prepare(in SsrCompositeInputs inputs);

    /// <summary>Called only once inside <see cref="RenderPassId.SsrComposite"/>.</summary>
    public abstract bool Draw();

    public abstract void Dispose();
}
