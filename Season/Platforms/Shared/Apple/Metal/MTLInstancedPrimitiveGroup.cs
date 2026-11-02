// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using Metal;
using System.Runtime.InteropServices;

namespace Season.Platforms.Shared.Apple.Metal;

[StructLayout(LayoutKind.Sequential)]
internal struct InstanceTransformData
{
    public Vector4 Row0;
    public Vector4 Row1;
    public Vector4 Row2;
    public Vector4 Row3;
    public Vector4 MorphWeights;

    public static InstanceTransformData FromWorld(Matrix4x4 world)
    {
        return new InstanceTransformData
        {
            Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
            Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
            Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
            Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
            MorphWeights = Vector4.Zero,
        };
    }
}

/// <summary>
/// Base class for GPU-instanced primitive groups on Metal.
/// It centralizes instance-buffer management, transparent sorting, and three-bucket grouped rendering.
/// Derived types include MTLInstancedModel for glTF template cloning
/// and MTLInstancedMesh3D for generated geometry.
/// </summary>
internal abstract class MTLInstancedPrimitiveGroup : MTLPrimitiveGroup, Season.Rendering.IInstanceCullTarget
{
    protected readonly List<PrimitiveData> _primitives = new();
    protected readonly List<PrimitiveData> _opaquePrimitives = new();
    protected readonly List<PrimitiveData> _transparentPrimitives = new();
    protected readonly List<int> _transparentInstanceOrder = new();

    protected InstanceTransformData[] _instanceData = Array.Empty<InstanceTransformData>();
    protected Matrix4x4[] _instanceWorlds = Array.Empty<Matrix4x4>();

    protected IMTLBuffer _instanceBuffer = null!;
    protected int _instanceCapacity;
    protected int _instanceCount;

    /// <summary>Whether any instance enabled Wireframe highlighting in the current frame.
    /// This is written during Update and used as a zero-cost gate during Draw.
    /// Derived Update methods reset it at the start of every frame.</summary>
    protected bool _wireframeActive;

    // === Unified highlighting: per-instance Outline2D state, finalized by derived Update methods through SetOutline2DState ===

    /// <summary>Compressed instance indices that enabled Outline2D in the current frame, rebuilt on every Update.</summary>
    protected readonly List<int> _outline2DInstances = new();

    /// <summary>Per-instance outline colors aligned with the slots in _outline2DInstances, rebuilt every frame so the per-slot mask can fetch colors by slot.</summary>
    protected readonly List<Vector4> _outline2DInstanceColors = new();

    /// <summary>Outline color of the first instance that enabled Outline2D in the current frame, used as the shared color for per-instance masks.</summary>
    protected Vector4 _outline2DInstanceColor;

    /// <summary>Outline width of the first instance that enabled Outline2D in the current frame.</summary>
    protected float _outline2DInstanceWidth;

    /// <summary>Whether host-level Outline2D is active in the current frame. When active, it uses the full mask and ignores the per-instance list.</summary>
    protected bool _outline2DHostActive;

    /// <summary>
    /// Contract clause 8(c) of 2-3: double-buffered instance streams.
    /// The write side at [_instanceWriteIndex] is fully rewritten every frame,
    /// while the opposite side contains the previous-frame per-instance world transforms and morph weights.
    /// That opposite side can be bound directly as the prev source at VS buffer(9),
    /// with no extra CPU copy.
    /// When capacity changes, both sides are rebuilt and cleared together,
    /// where r3.w == 0 acts as the "no history" sentinel.
    /// </summary>
    protected readonly IMTLBuffer[] _instanceBuffers = new IMTLBuffer[2];
    protected int _instanceWriteIndex;

    /// <summary>The previous-instance stream for the current frame.
    /// If no history exists yet, it falls back to the write side so the shader uses current values through the r3.w == 0 sentinel.</summary>
    protected IMTLBuffer PrevInstanceBuffer => _instanceBuffers[_instanceWriteIndex ^ 1] ?? _instanceBuffer;

    // === A7 逐实例 GPU 剔除 ===
    // 与 DXInstancedPrimitiveGroup / VKInstancedPrimitiveGroup 逐项对应：五个缓冲只在容量变化时重建，“只增不减”。
    // 相对另外两端这里少了两件事：Metal 没有缓冲状态机（写后读由驱动 hazard tracking 兑现），
    // 也没有需要在创建期烘死的 usage 位，所以 sorted 声明 ComputeAndVertexInput 只是为了四端校验对称。

    Season.Rendering.StorageBuffer? _cullSource;
    Season.Rendering.StorageBuffer? _cullSorted;
    Season.Rendering.StorageBuffer? _cullArgs;
    Season.Rendering.StorageBuffer? _cullVisible;
    Season.Rendering.StorageBuffer? _cullPrimInfo;
    int _cullCapacity = -1;
    /// <summary>已上传到 GPU 的参数表条数。与 <see cref="_cullPrimInfoPending"/> 不等时说明本帧要重传。</summary>
    int _cullPrimitiveCount = -1;
    /// <summary>CPU 侧已构造好的参数表字节：构造随时可做，上传要等 blit encoder 能开出来
    ///（见 <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>）。</summary>
    byte[]? _cullPrimInfoBytes;
    /// <summary>CPU 侧参数表的条数，-1 表示还没构造过。</summary>
    int _cullPrimInfoPending = -1;
    bool _cullRegistered;
    Vector3 _cullLocalCenter;
    Vector3 _cullLocalExtents;
    bool _cullDrawReady;

    protected MTLInstancedPrimitiveGroup(string name)
    {
        Name = name;
    }

    protected void RebuildPrimitiveBuckets()
    {
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();

        foreach (var primitive in _primitives)
        {
            if (primitive.IsTransparent)
                _transparentPrimitives.Add(primitive);
            else
                _opaquePrimitives.Add(primitive);
        }

        UpdateCullLocalBounds();
    }

    /// <summary>
    /// 把全部 primitive 的局部包围盒并成组级合并盒。剔除内核只收一个盒，所以并集是必填项：
    /// 传某个 surface 的盒会让其余 surface 的实例在盒外被成批误剔。
    /// </summary>
    void UpdateCullLocalBounds()
    {
        if (_primitives.Count == 0)
        {
            _cullLocalCenter = Vector3.Zero;
            _cullLocalExtents = Vector3.Zero;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var primitive in _primitives)
        {
            min = Vector3.Min(min, primitive.LocalBoundsCenter - primitive.LocalBoundsExtents);
            max = Vector3.Max(max, primitive.LocalBoundsCenter + primitive.LocalBoundsExtents);
        }

        _cullLocalCenter = (min + max) * 0.5f;
        _cullLocalExtents = (max - min) * 0.5f;
    }

    protected void EnsureInstanceBufferCapacity(int count)
    {
        if (count <= 0)
            return;

        if (_instanceBuffers[0] != null && _instanceCapacity == count)
            return;

        // On capacity changes, rebuild and clear both sides together.
        // After clearing, r3.w == 0 makes the shader fall back to the current world,
        // preventing stale indices on the old side from generating fake velocity when instance counts change.
        nuint size = (nuint)(Unsafe.SizeOf<InstanceTransformData>() * count);
        for (int i = 0; i < 2; i++)
        {
            _instanceBuffers[i]?.Dispose();
            _instanceBuffers[i] = Device.ResourceManager.CreateBuffer(size);
            unsafe
            {
                new Span<byte>((void*)_instanceBuffers[i].Contents, (int)size).Clear();
            }
        }

        _instanceCapacity = count;
    }

    /// <summary>
    /// Advances the write side of the double-buffered instance stream for 2-3 and uploads current-frame data.
    /// Derived Update methods call this at the end,
    /// after which <see cref="_instanceBuffer"/> points to the current-frame write side
    /// and <see cref="PrevInstanceBuffer"/> points to the previous-frame side.
    /// </summary>
    protected void FlipAndUploadInstanceStream(InstanceTransformData[] data, int count)
    {
        _instanceWriteIndex ^= 1;
        _instanceBuffer = _instanceBuffers[_instanceWriteIndex];
        Device.ResourceManager.UpdateBuffer(_instanceBuffer, new ReadOnlySpan<InstanceTransformData>(data, 0, count));
    }

    public new void Draw()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        bool forceFadeByAlpha = _currentAlpha < 1.0f;
        int fi = Device.FrameIndex;

        // 1. Opaque, using the Opaque PSO when overall alpha is at least 1, otherwise using Fade.
        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(enc, forceFadeByAlpha ? PipelineMode.Fade : PipelineMode.Opaque, primitive.DoubleSided);
            Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, 0, (nuint)_instanceCount, 0,
                    PrevInstanceBuffer);
        }

        // 2. Transparent, for true BLEND materials drawn per instance from back to front.
        for (int i = 0; i < _transparentPrimitives.Count; i++)
        {
            var primitive = _transparentPrimitives[i];
            BuildTransparentInstanceOrder(primitive);

            for (int orderIndex = 0; orderIndex < _transparentInstanceOrder.Count; orderIndex++)
            {
                int instanceIndex = _transparentInstanceOrder[orderIndex];
                nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * instanceIndex);
                if (primitive.DoubleSided)
                {
                    Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                    enc.SetCullMode(MTLCullMode.Front);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        _instanceBuffer, instOffset, 1, 0,
                        PrevInstanceBuffer);
                }

                Pipeline.SetPipeline(enc, PipelineMode.Transparent, false);
                enc.SetCullMode(MTLCullMode.Back);
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, instOffset, 1, 0,
                    PrevInstanceBuffer);
            }
        }

        // Unified highlighting for per-instance bounds boxes and wireframe shell boxes.
        // It uses the instances enabled this frame, draws transparent faces in two passes plus opaque edges,
        // and runs after all surfaces have finished.
        if (_boundsActive)
            DrawBoundsBoxes();
        if (_wireframeActive)
            DrawShellBoxes(_instanceBuffer, PrevInstanceBuffer);
    }

    /// <summary>
    /// Instanced shadow rendering for 1-5, drawing all instances in one draw call.
    /// Only opaque buckets are rendered because true BLEND materials do not cast shadows under contract clause 7.
    /// The shadow PSO is already bound by RenderShadowPass, so this path neither switches PSOs nor sorts.
    /// </summary>
    public override void DrawShadow()
    {
        if (!_transformInitialized || _instanceCount == 0)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;

        // When b2 and t5, meaning Metal slots 4 and 5, are identical within the group,
        // bind them only for the first primitive. See CanShareShadowMaterial.
        bool shareMaterial = CanShareShadowMaterial(_opaquePrimitives);
        bool materialBound = false;

        // A7：整组走间接路径时，索引数/实例数/baseVertex 全由 GPU 侧参数缓冲给出，
        // 实例流换成 gather 产出的重排块——按槽位分段，所以偏移设在 SetVertexBuffer 上。
        if (_cullDrawReady && _cullCapacity >= _instanceCount
            && _cullSorted is MTLStorageBuffer sorted && sorted.Buffer != null
            && _cullArgs is MTLStorageBuffer args && args.Buffer != null)
        {
            int slot = Season.Rendering.CascadedShadow.ActiveSlot;
            if (slot >= 0 && slot < Season.Rendering.InstanceCull.MaxSlots)
            {
                nuint instanceOffset = (nuint)slot * (nuint)_cullCapacity
                    * Season.Rendering.InstanceCull.WorldStride;
                for (int i = 0; i < _opaquePrimitives.Count; i++)
                {
                    var primitive = _opaquePrimitives[i];
                    Pipeline.DrawShadowPrimitiveIndirect(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                        primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                        sorted.Buffer, instanceOffset, args.Buffer,
                        Season.Rendering.InstanceCull.ArgsOffset(slot, i),
                        bindMaterial: !shareMaterial || !materialBound);
                    materialBound = true;
                }
                return;
            }
        }

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.DrawShadowPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi], IdentityBoneBuffers[fi],
                primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                (nuint)primitive.Indices.Length, primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                _instanceBuffer, 0, (nuint)_instanceCount, 0,
                bindMaterial: !shareMaterial || !materialBound);
            materialBound = true;
        }
    }

    /// <summary>
    /// Phase 4 Outline2D mask rendering.
    /// This is the default implementation for the instanced base class and is reused directly by MTLInstancedMesh3D,
    /// which uses the base-class _instanceBuffer.
    /// Host activation uses the full mask; otherwise rendering is per instance through _outline2DInstances.
    /// Only opaque buckets are drawn.
    /// The mask PSO and DSS are routed by SetPipeline from ActivePassId, mirroring VKInstancedPrimitiveGroup.DrawOutlineMask.
    /// </summary>
    public override void DrawOutlineMask()
    {
        if (!_transformInitialized || _instanceCount == 0 || !Outline2DActive)
            return;

        var enc = Device.GraphicsEncoder;
        int fi = Device.FrameIndex;
        Pipeline.SetOutlineMaskColor(enc, _outline2DColor);

        for (int i = 0; i < _opaquePrimitives.Count; i++)
        {
            var primitive = _opaquePrimitives[i];
            Pipeline.SetPipeline(enc, PipelineMode.Opaque, primitive.DoubleSided);
            enc.SetDepthStencilState(Pipeline.OutlineMaskDepthState);

            if (_outline2DHostActive)
            {
                Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                    primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                    LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                    MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                    primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                    _instanceBuffer, 0, (nuint)_instanceCount, 0,
                    PrevInstanceBuffer);
            }
            else
            {
                for (int k = 0; k < _outline2DInstances.Count; k++)
                {
                    int idx = _outline2DInstances[k];
                    if ((uint)idx >= (uint)_instanceCount)
                        continue;
                    // Write this instance's own OutlineColor for the current slot
                    // so the per-slot mask can fetch color by slot.
                    Pipeline.SetOutlineMaskColor(enc, _outline2DInstanceColors[k]);
                    nuint instOffset = (nuint)(Marshal.SizeOf<InstanceTransformData>() * idx);
                    Pipeline.DrawPrimitive(enc, primitive, primitive.VertexBuffer, primitive.IndexBuffer,
                        primitive.MatrixBuffers[fi], primitive.MaterialBuffers[fi],
                        LightConstantBuffers[fi], IdentityBoneBuffers[fi], primitive.MorphDeltasBuffer ?? DefaultMorphDeltasBuffers[fi], IdentityInstanceBoneBuffers[fi],
                        MTLPrimitiveType.Triangle, (nuint)primitive.Indices.Length,
                        primitive.Use32BitIndices ? MTLIndexType.UInt32 : MTLIndexType.UInt16, 0,
                        _instanceBuffer, instOffset, 1, 0,
                        PrevInstanceBuffer);
                }
            }
        }
    }

    protected void BuildTransparentInstanceOrder(PrimitiveData primitive)
    {
        _transparentInstanceOrder.Clear();
        for (int i = 0; i < _instanceCount; i++)
            _transparentInstanceOrder.Add(i);

        _transparentInstanceOrder.Sort((a, b) =>
        {
            float depthA = ComputeTransparentDepth(_instanceWorlds[a], primitive.LocalBoundsCenter);
            float depthB = ComputeTransparentDepth(_instanceWorlds[b], primitive.LocalBoundsCenter);
            return depthB.CompareTo(depthA);
        });
    }

    protected static float ComputeTransparentDepth(Matrix4x4 world, Vector3 localCenter)
    {
        return RenderView.SortDepth(Vector3.Transform(localCenter, world));
    }

    /// <summary>Returns the owned primitive collection for base-class operations such as SyncAlpha and ReplaceTextureBySlot.</summary>
    protected override void CollectPrimitives(List<PrimitiveData> result)
    {
        result.AddRange(_primitives);
    }

    // ============================================================
    // A7 逐实例 GPU 剔除
    // ============================================================

    int Season.Rendering.IInstanceCullTarget.CullInstanceCount => _instanceCount;

    int Season.Rendering.IInstanceCullTarget.CullCapacity => _cullCapacity;

    int Season.Rendering.IInstanceCullTarget.CullPrimitiveCount => _opaquePrimitives.Count;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSourceWorlds => _cullSource;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullSortedWorlds => _cullSorted;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullPrimitiveInfo => _cullPrimInfo;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullArgs => _cullArgs;

    Season.Rendering.StorageBuffer? Season.Rendering.IInstanceCullTarget.CullVisible => _cullVisible;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalCenter => _cullLocalCenter;

    Vector3 Season.Rendering.IInstanceCullTarget.CullLocalExtents => _cullLocalExtents;

    /// <summary>
    /// 派生类在实例流上传之后调用，只备资源。矩阵流上传延后到 Record 相位：控制件的 Update 阶段
    /// render encoder 可能还开着，那时 CreateBlitCommandEncoder 直接失败，上传被丢弃且只留一行日志。
    /// </summary>
    protected void UpdateCullUpload()
    {
        _cullDrawReady = false;
        if (_instanceCount <= 0)
            return;

        EnsureCullResources(_instanceCount);
    }

    /// <summary>
    /// 按实例数准备剔除资源。容量按 64 对齐，于是实例数小幅抖动不会每帧重建五个缓冲；
    /// 但接口上报的容量必须是真实容量，否则序号表会按本帧实例数寻址而与内核写入错位。
    /// </summary>
    void EnsureCullResources(int count)
    {
        if (!Season.Rendering.InstanceCull.Enabled)
        {
            ReleaseCullResources();
            return;
        }
        // 实例数掉到 0 不等于要释放：隐藏再显示会因此每帧重建五个缓冲。
        if (count <= 0)
            return;

        int capacity = (count + 63) / 64 * 64;
        if (_cullCapacity != capacity || _cullSorted == null)
        {
            ReleaseCullResources();
            try
            {
                var g = Season.Basic.Graphics.Instance;
                _cullSource = g.CreateStorageBuffer((uint)capacity * Season.Rendering.InstanceCull.WorldStride);
                _cullSorted = g.CreateStorageBuffer(Season.Rendering.InstanceCull.SortedBytes(capacity),
                    Season.Rendering.StorageBufferUsage.ComputeAndVertexInput);
                _cullVisible = g.CreateStorageBuffer((uint)(Season.Rendering.InstanceCull.MaxSlots * capacity) * 4u);
                _cullArgs = g.CreateStorageBuffer(Season.Rendering.InstanceCull.ArgsBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
                _cullPrimInfo = g.CreateStorageBuffer(
                    (uint)Season.Rendering.InstanceCull.MaxPrimitives * Season.Rendering.InstanceCull.PrimitiveInfoStride);
            }
            catch (Exception ex)
            {
                DeviceServices.BaseApp?.AddLog(LogType.Error, $"[InstanceCull] 缓冲创建失败：{ex.Message}");
            }

            if (_cullSource == null || _cullSorted == null || _cullVisible == null
                || _cullArgs == null || _cullPrimInfo == null)
            {
                ReleaseCullResources();
                return;
            }

            _cullCapacity = capacity;
            _cullPrimitiveCount = -1;
        }

        EnsureCullPrimitiveInfo();

        if (!_cullRegistered)
        {
            Season.Rendering.InstanceCull.Register(this);
            _cullRegistered = true;
        }
    }

    /// <summary>
    /// 只构造参数表字节，不碰 GPU —— 构造与上传的安全时机不同：构造随时可做，
    /// 上传要等 blit encoder 能开出来（见 <see cref="Season.Rendering.IInstanceCullTarget.PrepareCullInputs"/>）。
    /// primitive 集合不变就不重建。索引区间与绘制端必须同源：reset 内核把这里的
    /// indexCount 写进间接参数，而绘制端不再自己提供索引数。
    /// </summary>
    void EnsureCullPrimitiveInfo()
    {
        if (_cullPrimInfo == null)
            return;
        int n = _opaquePrimitives.Count;
        if (n <= 0 || n > Season.Rendering.InstanceCull.MaxPrimitives)
        {
            // 明确记下"没有可传的参数表"：否则 PrepareCullInputs 会拿上一次的字节反复上传，
            // 条数看起来对得上，内容却是过期的那一份。
            _cullPrimInfoBytes = null;
            _cullPrimInfoPending = 0;
            _cullPrimitiveCount = 0;
            return;
        }
        if (_cullPrimInfoPending == n)
            return;

        var bytes = new byte[n * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride];
        for (int i = 0; i < n; i++)
        {
            var primitive = _opaquePrimitives[i];
            Season.Rendering.InstanceCull.WritePrimitiveInfo(bytes,
                i * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride,
                (uint)primitive.Indices.Length, 0u, 0);
        }

        _cullPrimInfoBytes = bytes;
        _cullPrimInfoPending = n;
    }

    /// <summary>
    /// A7：把剔除的 CPU 侧输入传到 GPU。由 InstanceCullEffect 在每次派发之前回调，
    /// 那个相位在所有 Pass 之外 —— Metal 的 compute 与 render encoder 互斥，
    /// render encoder 开着时开 blit encoder 必失败，所以这是本端唯一可靠的上传时机。
    /// 输入与 IA 实例流逐位相同（都是 InstanceTransformData，步长 80 与 WorldStride 一致），
    /// 所以直接复用同一份数组，不多存副本。
    /// </summary>
    bool Season.Rendering.IInstanceCullTarget.PrepareCullInputs()
    {
        if (!Season.Rendering.InstanceCull.Enabled || _instanceCount <= 0)
            return false;

        var g = Season.Basic.Graphics.Instance;

        // 参数表跟着几何走而不跟帧走，所以只在条数变了时重传。
        if (_cullPrimInfoPending != _cullPrimitiveCount)
        {
            if (_cullPrimInfoPending <= 0 || _cullPrimInfoBytes == null || _cullPrimInfo == null)
                return false;
            int bytes = _cullPrimInfoPending * (int)Season.Rendering.InstanceCull.PrimitiveInfoStride;
            if (!g.TryUpdateStorageBuffer(_cullPrimInfo, new ReadOnlySpan<byte>(_cullPrimInfoBytes, 0, bytes)))
                return false;
            _cullPrimitiveCount = _cullPrimInfoPending;
        }

        // 矩阵流每帧都变，只传有效段，不留余量。
        return _cullSource != null && g.TryUpdateStorageBuffer(_cullSource,
            MemoryMarshal.AsBytes(_instanceData.AsSpan(0, _instanceCount)));
    }

    /// <summary>
    /// A7：把 gather 写出的重排流与参数缓冲交接到绘制可读状态。必须在 Pass 之外执行，
    /// 所以由 InstanceCullEffect 在三次派发之后回调，而不是等到 DrawShadow。
    /// Metal 这一端无事可录：写后读依赖由驱动 hazard tracking 兑现，
    /// 所以这里只核验句柄并走一次统一准入校验，"交接"即"准入"。
    /// </summary>
    public bool PrepareCullDraw()
    {
        _cullDrawReady = false;
        if (_cullSorted is not MTLStorageBuffer sorted || sorted.Buffer == null)
            return false;
        if (_cullArgs is not MTLStorageBuffer args || args.Buffer == null)
            return false;
        if (_cullCapacity <= 0)
            return false;

        if (!Season.Basic.Graphics.Instance.TryPrepareIndirectArgs(args))
            return false;

        _cullDrawReady = true;
        return true;
    }

    void ReleaseCullResources()
    {
        if (_cullRegistered)
        {
            Season.Rendering.InstanceCull.Unregister(this);
            _cullRegistered = false;
        }
        _cullSource?.Dispose(); _cullSource = null;
        _cullSorted?.Dispose(); _cullSorted = null;
        _cullArgs?.Dispose(); _cullArgs = null;
        _cullVisible?.Dispose(); _cullVisible = null;
        _cullPrimInfo?.Dispose(); _cullPrimInfo = null;
        _cullCapacity = -1;
        _cullPrimitiveCount = -1;
        _cullPrimInfoBytes = null;
        _cullPrimInfoPending = -1;
        _cullDrawReady = false;
    }

    /// <summary>
    /// Releases the instance buffers and clears all primitive collections.
    /// Derived classes should clean up their own GPU resources before calling base.Dispose().
    /// </summary>
    public override void Dispose()
    {
        ReleaseCullResources();
        _primitives.Clear();
        _opaquePrimitives.Clear();
        _transparentPrimitives.Clear();
        _transparentInstanceOrder.Clear();

        // For 2-3, both sides of the double-buffered instance stream are owned by this class,
        // and _instanceBuffer is only an alias of the current write side.
        for (int i = 0; i < _instanceBuffers.Length; i++)
        {
            _instanceBuffers[i]?.Dispose();
            _instanceBuffers[i] = null!;
        }

        _instanceBuffer = null!;
    }
}
