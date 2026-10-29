// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>后端已经捕获的单个绘制项；Draw 不得重新读取 Control 或消费其一次性覆盖。</summary>
public interface ISceneDrawCommand
{
    void Draw();

    /// <summary>交还捕获时持有的资源引用；原生资源仍须遵循后端 fence 延迟释放规则。</summary>
    void Release();
}

/// <summary>收集时固定的层级优先级。默认容器不新增排序层级；外层显式优先级优先于内层。</summary>
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
            throw new InvalidOperationException("绘制优先级已过期，不能跨帧使用。");
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

/// <summary>一帧共用的优先级存储。每个显式容器/叶子复制短路径，图元只捕获值句柄；容量跨帧复用。</summary>
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
        : throw new InvalidOperationException("只能在收集阶段捕获绘制优先级。");
    internal (int Layer, int Order) Component(int index) => _components[index];

    public void BeginCollection()
    {
        if (_live) throw new InvalidOperationException("上一帧排序上下文尚未释放。");
        _live = IsCollecting = true;
    }

    public Scope Push(int layer, int order)
    {
        if (!IsCollecting) throw new InvalidOperationException("当前不在绘制收集阶段。");
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
            throw new InvalidOperationException("绘制作用域尚未结束或收集阶段已经结束。");
        IsCollecting = false;
    }

    /// <summary>在队列释放之后调用，异常路径也必须重置；使所有旧句柄失效。</summary>
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
                throw new InvalidOperationException("绘制作用域必须按嵌套顺序恰好结束一次。");
            _owner._current = _previous;
            _owner._scopeToken = _parentToken;
            _owner._depth--;
        }
    }
}

/// <summary>按图元分类的复用队列。T 保存本帧绑定、材质、实例和蒙皮快照，不保存延迟调用 Control.Draw 的委托。</summary>
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
            throw new InvalidOperationException("上一帧绘制项尚未释放。");
        _sequence = 0;
        _opaqueDrawn = _transparentDrawn = false;
        _collecting = true;
    }

    /// <summary>成功加入后，队列持有命令的资源引用；失败时由调用者释放。</summary>
    public void Add(in T command, bool transparent, int layer, int order, float depth)
        => Add(command, transparent, new SceneDrawPriority(layer, order), depth);

    public void Add(in T command, bool transparent, SceneDrawPriority priority, float depth)
    {
        if (!_collecting) throw new InvalidOperationException("只能在场景收集阶段添加绘制项。");
        if (!float.IsFinite(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        priority.Validate();
        var item = new Entry(command, priority, depth, _sequence++);
        (transparent ? _transparent : _opaque).Add(item);
    }

    public void Seal()
    {
        if (!_collecting) throw new InvalidOperationException("当前不在场景收集阶段。");
        for (int i = 0; i < _opaque.Count; i++) _opaque[i].Priority.Validate();
        for (int i = 0; i < _transparent.Count; i++) _transparent[i].Priority.Validate();
        _collecting = false;
        _sealed = true;
        _opaque.Sort(OpaqueComparer);
        _transparent.Sort(TransparentComparer);
    }

    public void DrawOpaque()
    {
        if (!_sealed || _opaqueDrawn) throw new InvalidOperationException("不透明队列未封存或已经消费。");
        _opaqueDrawn = true;
        for (int i = 0; i < _opaque.Count; i++) _opaque[i].Command.Draw();
    }

    public void DrawTransparent()
    {
        if (!_sealed || !_opaqueDrawn || _transparentDrawn)
            throw new InvalidOperationException("透明队列必须在不透明队列之后消费一次。");
        _transparentDrawn = true;
        for (int i = 0; i < _transparent.Count; i++) _transparent[i].Command.Draw();
    }

    /// <summary>
    /// 按最终绘制顺序读取透明队列中的一项，但不绘制。
    /// 后端需要在透明 Pass 内先给每一项补齐该 Pass 专属的资源绑定（例如雾采样描述符），
    /// 之后才能提交绘制，因此必须有一个不触发 Draw 的遍历入口。
    /// </summary>
    public T TransparentAt(int index)
    {
        if (!_sealed) throw new InvalidOperationException("透明队列尚未封存。");
        if ((uint)index >= (uint)_transparent.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _transparent[index].Command;
    }

    /// <summary>正常帧和异常帧均须在 finally 调用；列表容量保留，资源引用全部清除。</summary>
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

/// <summary>完整实现全部场景绘制路径的后端才实现此接口。收集期间不得提交 GPU 绘制。</summary>
public interface ISegmentedSceneGraphics
{
    void BeginSceneCollection();
    void SealSceneCollection();
    void DrawCollectedOpaque();
    void DrawCollectedTransparent(bool sampleFog);
    void ReleaseSceneCollection();
}

/// <summary>合成成功后才发布颜色源与 AO 消费标记；失败时透明阶段继续使用原场景附件。</summary>
public readonly record struct FogCompositeResult(bool Success, bool AoApplied);

/// <summary>独立合成绑定，不覆盖表面光照缓冲；ApplyAo 只能来自本帧已就绪且未消费的 AO。</summary>
public readonly record struct FogCompositeInputs(RenderTarget Color, RenderTarget Depth, StorageBuffer FrameBuffer,
    string IntegratedVolume, string LocalVolume, ComputeResourceRef Ao, float AoIntensity, bool ApplyAo);

/// <summary>Prepare 在 Pass 外解析本帧资源；Draw 仅在独立 HDR FogComposite Pass 内消费一次。</summary>
public abstract class FogCompositePipeline : IDisposable
{
    public abstract bool Prepare(in FogCompositeInputs inputs, RenderTarget destination);
    public abstract FogCompositeResult Draw();
    public abstract void Dispose();
}
