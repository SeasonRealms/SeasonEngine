// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Rendering;

/// <summary>
/// A7：一个 instanced 组交给 GPU 剔除时暴露的资源面。实现方是各端的 instanced primitive group，
/// 因为只有它同时知道实例世界矩阵、各 surface 的索引区间和当前帧的实例数。
///
/// 契约的核心是"原始序号间接寻址"：剔除内核读实例世界矩阵，通过的实例把**原始下标**写进
/// <see cref="CullVisible"/>，绘制端把压缩后的 <c>SV_InstanceID</c> 经这张表还原成原始下标再去取
/// 变换。因此实例数据本身从不重建、从不重排，每帧只多写一张 4 字节的序号表和几条间接参数。
/// 这正是 1-5 在 <see cref="Season.Controls.InstancedMesh3DBase"/> 里标为延期的做法：那里的注释写明
/// "逐实例剔除需要为每个级联重建实例缓冲并上传，违反 1-3 条款 2"，而序号表绕开了重建。
///
/// 所有缓冲都按 <see cref="InstanceCull.MaxSlots"/> 个槽位平铺，槽位布局与
/// <see cref="CascadedShadow"/> 一致：0..2 是太阳级联，3 是聚光。未激活的槽位由 slotMask 直接跳过，
/// 而不是靠填一个"什么都剔掉的矩阵"—— 那需要构造退化投影，一旦提取平面的代码改动就会静默失效。
/// </summary>
public interface IInstanceCullTarget
{
    /// <summary>本帧参与剔除的实例数。0 表示这组本帧不画，内核也不会被派发。</summary>
    int CullInstanceCount { get; }

    /// <summary>
    /// 序号表与重排流按实例计的容量，必须不小于 <see cref="CullInstanceCount"/>。
    /// 它和实例数分开是有原因的：各端缓冲沿用"只增不减"的容量策略（与实例缓冲一致），
    /// 而剔除内核按 capacity 定位、gather 也按 capacity 寻址；若这里回落到本帧实例数，
    /// 容量一旦留过余量，序号表就会读错位置，表现为画出一批本不该出现的实例。
    /// </summary>
    int CullCapacity { get; }

    /// <summary>本组的 primitive 数，即每个槽位要写多少条间接参数。超过 <see cref="InstanceCull.MaxPrimitives"/> 时该组退回 CPU 剔除。</summary>
    int CullPrimitiveCount { get; }

    /// <summary>每实例 80 字节的原始世界矩阵流，布局与 IA 的实例缓冲逐位相同（四行矩阵 + 四元组形变权重）。这是剔除的输入，本身永远不被改写。</summary>
    StorageBuffer? CullSourceWorlds { get; }

    /// <summary>
    /// 每实例 80 字节的重排世界矩阵流，<see cref="InstanceCull.MaxSlots"/> 个槽位按容量分段平铺。
    /// gather 内核按序号表把可见实例物化到这里，绘制端把它绑成实例流并把实例数交给间接参数。
    /// 之所以要物化而不是让顶点阶段自己查表：D3D12 的根签名已用掉 61/64 DWORD，再挂两个 SRV 表
    /// 会把后续所有 Phase 的绑定余量吃尽，而 IA 的实例步进无法按序号索引。
    /// </summary>
    StorageBuffer? CullSortedWorlds { get; }

    /// <summary>每 primitive 16 字节：索引数、起始索引、基准顶点、保留。只在 primitive 集合变化时更新。</summary>
    StorageBuffer? CullPrimitiveInfo { get; }

    /// <summary><see cref="InstanceCull.MaxSlots"/> × <see cref="InstanceCull.MaxPrimitives"/> 条间接绘制参数，由 GPU 写。</summary>
    StorageBuffer? CullArgs { get; }

    /// <summary><see cref="InstanceCull.MaxSlots"/> × 容量个 uint，每槽位一段原始序号表，由 GPU 写。</summary>
    StorageBuffer? CullVisible { get; }

    /// <summary>组局部包围盒中心。多 surface 的组必须传合并盒，否则某个 surface 的实例会被误剔。</summary>
    Vector3 CullLocalCenter { get; }

    /// <summary>组局部包围盒半长。</summary>
    Vector3 CullLocalExtents { get; }

    /// <summary>
    /// 三次派发之后回调，让实现方把重排流与间接参数从 compute 写入状态交接到绘制可读状态，
    /// 返回本帧是否可以直接走间接路径。
    ///
    /// 必须在 Record 里完成而不能推迟到 DrawShadow 里：那时候已经在阴影 Pass 内部，
    /// 而 PassDesc 禁止在 Pass 体内切资源状态（Vulkan 的 render pass 实例内插不了 barrier）。
    /// 这与 <see cref="IGraphics.TryPrepareIndirectArgs"/> 是同一条约束的两个落点。
    /// </summary>
    bool PrepareCullDraw();
}

/// <summary>
/// A7：逐实例 GPU 剔除的跨端常量与注册表。这里的每个数字都是四端着色器与 C# 分配共同依赖的契约，
/// 改动任何一项都必须同步四份 kernel 源，否则表现为"剔除结果错位"而不是编译错误。
/// </summary>
public static class InstanceCull
{
    /// <summary>槽位数，直接取自阴影系统的图集分区数：三个级联 + 一个聚光。</summary>
    public const int MaxSlots = CascadedShadow.SlotCount;

    /// <summary>
    /// 每组 primitive 上限。间接参数表按 <c>slot * MaxPrimitives + primitive</c> 定长寻址，
    /// 定长而非紧凑排布是刻意的：内核与绘制端都不必读一个"每帧可能变"的 stride，
    /// 少一个漂移点。16 覆盖现有模型的所有 surface 组合，超出的组直接不参与 A7。
    /// </summary>
    public const int MaxPrimitives = 16;

    /// <summary>每实例世界矩阵步长，与 DX 的 InstanceTransformData、各端 IA 实例流逐位一致。</summary>
    public const uint WorldStride = 80;

    /// <summary>每 primitive 描述步长：indexCount / startIndex / baseVertex / 保留。</summary>
    public const uint PrimitiveInfoStride = 16;

    /// <summary>一条间接索引绘制参数的字节数，与 <see cref="IndirectDrawIndexedArgs"/> 同源。</summary>
    public const uint ArgsStride = IndirectDrawIndexedArgs.SizeInBytes;

    /// <summary>剔除内核与扇出内核的统一工作组长。四端都以它作为 <c>numthreads</c> / <c>local_size</c>。</summary>
    public const uint CullGroupSize = 64;

    /// <summary>每个槽位矩阵的字节数。四行 float4，与 System.Numerics 的行序一致。</summary>
    public const uint SlotMatrixStride = 64;

    /// <summary>间接参数表的总字节数：槽位 × primitive × 20。</summary>
    public const uint ArgsBytes = (uint)(MaxSlots * MaxPrimitives) * ArgsStride;

    /// <summary>重排实例流的总字节数：槽位 × 容量 × 80。容量只增不减，与实例缓冲同样的策略。</summary>
    public static uint SortedBytes(int capacity) => (uint)(MaxSlots * capacity) * WorldStride;

    /// <summary>gather 内核每槽位要扫的线程数上限，等于容量本身；一次派发覆盖全部槽位。</summary>
    public static uint GatherGroups(int capacity) => (uint)((long)MaxSlots * capacity + CullGroupSize - 1) / CullGroupSize;

    /// <summary>reset 内核的线程数：一次覆盖全部槽位与 primitive。</summary>
    public const uint ResetThreads = (uint)(MaxSlots * MaxPrimitives);

    /// <summary>槽位矩阵缓冲的字节数。</summary>
    public const uint SlotMatrixBytes = (uint)MaxSlots * SlotMatrixStride;

    /// <summary>
    /// 后端能力是否已就位。由 <see cref="InstanceCullEffect"/> 初始化成功置位，卸载或失败清回 false；
    /// 各端 group 只在此时才分配剔除资源，于是"某端没写 kernel"表现为退回 CPU 剔除而不是空缓冲绘制。
    /// </summary>
    public static bool Enabled { get; internal set; }

    /// <summary>本帧登记的剔除目标。由各端 group 在创建时加入、销毁时按引用移除。</summary>
    public static readonly List<IInstanceCullTarget> Targets = new();

    /// <summary>间接参数表中 (slot, primitive) 条目的字节偏移。</summary>
    public static uint ArgsOffset(int slot, int primitive)
        => (uint)(slot * MaxPrimitives + primitive) * ArgsStride;

    /// <summary>原始序号表中 (slot, 第 k 个可见实例) 的 uint 下标。</summary>
    public static uint VisibleIndex(int slot, uint k, int capacity)
        => (uint)(slot * capacity) + k;

    /// <summary>
    /// 写入一条 primitive 描述的四个字段。放在共享层是因为这四个字段的偏移是
    /// reset 内核与四端绘制端共同依赖的契约，各写一遍就会有一端漂移。
    /// 布局固定为 indexCount / startIndex / baseVertex / 保留，与 <see cref="PrimitiveInfoStride"/> 一致。
    /// </summary>
    public static void WritePrimitiveInfo(Span<byte> dst, int offset, uint indexCount, uint startIndex, int baseVertex)
    {
        var span = MemoryMarshal.Cast<byte, uint>(dst.Slice(offset, (int)PrimitiveInfoStride));
        span[0] = indexCount;
        span[1] = startIndex;
        span[2] = (uint)baseVertex;
        span[3] = 0u;
    }

    /// <summary>登记一组剔除目标。重复登记无副作用，避免同一组在多条路径上被加入两次。</summary>
    public static void Register(IInstanceCullTarget target)
    {
        if (target == null || Targets.Contains(target)) return;
        Targets.Add(target);
    }

    /// <summary>按引用移除；未登记时静默返回，因为失败的初始化可能根本没走到登记。</summary>
    public static void Unregister(IInstanceCullTarget target)
    {
        if (target != null) Targets.Remove(target);
    }
}

/// <summary>
/// A7：阴影剔除的调度者。<see cref="ComputePhase.FrameStart"/> 且 App 作用域，因为派发必须在任何 Pass 之外
/// （Vulkan 在 Pass 内禁止 dispatch，Metal 的 compute 与 render encoder 互斥），而阴影图集整帧只有一张，
/// 不该随视图重复剔除。
///
/// 每组每帧固定三次派发：reset 填参数表、cull 分槽位压缩、fanout 把槽位 0 的实例数摊到其余 primitive。
/// 三次都必须在 Pass 之外，这一点与粒子系统的 A0.1 链路完全相同，区别只在于这里产出的是
/// "每槽位一份间接参数 + 一份原始序号表"，而不是一条唯一的 apply 命令。
/// </summary>
public sealed partial class InstanceCullEffect : ComputeEffect, IDisposable
{
    public override string Name => "instanceCull";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    readonly Vector4[] _slotMatrices = new Vector4[InstanceCull.MaxSlots * 4];
    readonly uint[] _resetParams = new uint[4];
    readonly uint[] _gatherParams = new uint[4];
    readonly float[] _cullParams = new float[16];
    readonly ComputeResourceRef[] _resetRefs = new ComputeResourceRef[2];
    readonly ComputeResourceRef[] _cullRefs = new ComputeResourceRef[4];
    readonly ComputeResourceRef[] _gatherRefs = new ComputeResourceRef[4];

    IGraphics? _graphics;
    ComputeKernel? _reset, _cull, _gather;
    StorageBuffer? _slotBuffer;
    bool _routed;

    public override bool Initialize(IGraphics g)
    {
        if (_graphics != null) throw new InvalidOperationException("实例剔除已经初始化。");
        if (!g.ComputeSupported || !g.IndirectDrawSupported) return false;
        _graphics = g;
        try
        {
            _reset = g.CreateComputeKernel(CreateResetKernel());
            _cull = g.CreateComputeKernel(CreateCullKernel());
            _gather = g.CreateComputeKernel(CreateGatherKernel());
            if (_reset == null || _cull == null || _gather == null) { Dispose(); return false; }
            _slotBuffer = g.CreateStorageBuffer(InstanceCull.SlotMatrixBytes);
            if (_slotBuffer == null || _slotBuffer.SizeInBytes < InstanceCull.SlotMatrixBytes) { Dispose(); return false; }
            InstanceCull.Enabled = true;
            _routed = true;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            DeviceServices.BaseApp.AddLog(LogType.Error, $"[InstanceCull] 初始化失败：{ex.Message}");
            return false;
        }
    }

    public override void Record(IGraphics g)
    {
        if (!ReferenceEquals(g, _graphics) || _reset == null || _cull == null || _gather == null || _slotBuffer == null) return;
        if (!CascadedShadow.SunActive && !CascadedShadow.SpotActive) return;
        if (InstanceCull.Targets.Count == 0) return;

        // 槽位掩码在这里确定，而不是让内核拿一个"无效矩阵"去猜：未激活槽位直接不参与，
        // 它们的参数表保持 reset 写下的 InstanceCount=0，绘制端也就自然不会画。
        int slotMask = 0;
        int cascades = CascadedShadow.SunActive ? CascadedShadow.ActiveCascadeCount : 0;
        for (int slot = 0; slot < cascades && slot < CascadedShadow.MaxCascades; slot++)
        {
            WriteSlotMatrix(slot, CascadedShadow.CascadeViewProj[slot]);
            slotMask |= 1 << slot;
        }
        if (CascadedShadow.SpotActive)
        {
            WriteSlotMatrix(CascadedShadow.SpotSlot, CascadedShadow.SpotViewProj);
            slotMask |= 1 << CascadedShadow.SpotSlot;
        }
        if (slotMask == 0) return;

        // 未激活槽位清零：矩阵缓冲跨帧复用，残留的上一帧矩阵若被读到会让某个槽位画出幽灵实例。
        for (int slot = 0; slot < InstanceCull.MaxSlots; slot++)
            if ((slotMask & (1 << slot)) == 0) Array.Clear(_slotMatrices, slot * 4, 4);

        if (!g.TryUpdateStorageBuffer(_slotBuffer, MemoryMarshal.AsBytes(_slotMatrices.AsSpan()))) return;

        for (int i = 0; i < InstanceCull.Targets.Count; i++)
            DispatchTarget(g, InstanceCull.Targets[i], slotMask);
    }

    void WriteSlotMatrix(int slot, in Matrix4x4 vp)
    {
        int o = slot * 4;
        _slotMatrices[o + 0] = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14);
        _slotMatrices[o + 1] = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24);
        _slotMatrices[o + 2] = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        _slotMatrices[o + 3] = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44);
    }

    void DispatchTarget(IGraphics g, IInstanceCullTarget target, int slotMask)
    {
        int count = target.CullInstanceCount;
        int capacity = target.CullCapacity;
        int primitives = target.CullPrimitiveCount;
        if (count <= 0 || capacity < count || primitives <= 0 || primitives > InstanceCull.MaxPrimitives) return;

        var source = target.CullSourceWorlds;
        var sorted = target.CullSortedWorlds;
        var info = target.CullPrimitiveInfo;
        var args = target.CullArgs;
        var visible = target.CullVisible;
        if (source == null || sorted == null || info == null || args == null || visible == null) return;

        // 容量校验放在共享层，四端就不必各写一遍：args 是定长的，visible 与重排流必须容得下整份容量。
        // source 只需装下本帧实例——它按实例数上传，不保留余量。
        if (args.SizeInBytes < InstanceCull.ArgsBytes) return;
        if (visible.SizeInBytes < (uint)(InstanceCull.MaxSlots * capacity) * 4u) return;
        if (source.SizeInBytes < (uint)count * InstanceCull.WorldStride) return;
        if (sorted.SizeInBytes < InstanceCull.SortedBytes(capacity)) return;
        if (info.SizeInBytes < (uint)primitives * InstanceCull.PrimitiveInfoStride) return;

        // 1) reset：把每条的索引数与起始偏移写进去，InstanceCount 归零。
        _resetParams[0] = (uint)primitives;
        _resetParams[1] = (uint)InstanceCull.MaxSlots;
        _resetParams[2] = 0;
        _resetParams[3] = 0;
        _resetRefs[0] = info;
        _resetRefs[1] = args;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _reset!,
            Params = MemoryMarshal.AsBytes(_resetParams.AsSpan()),
            Resources = _resetRefs,
            GroupsX = 1,
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 2) cull：一个线程一个实例，逐槽位做球与六个裁剪平面的测试。
        _cullParams[0] = target.CullLocalCenter.X;
        _cullParams[1] = target.CullLocalCenter.Y;
        _cullParams[2] = target.CullLocalCenter.Z;
        _cullParams[3] = 0f;
        _cullParams[4] = MathF.Abs(target.CullLocalExtents.X);
        _cullParams[5] = MathF.Abs(target.CullLocalExtents.Y);
        _cullParams[6] = MathF.Abs(target.CullLocalExtents.Z);
        _cullParams[7] = 0f;
        // [8..12] 在四端着色器里全部声明为 uint，而 Params 是 16 字节对齐的裸字节块，
        // 所以这五项必须原样送位模式：直接赋 int 会先转成 float，着色器读到的就是
        // 8f / 4f 的浮点位模式（1090519040 / 1082130432），slotCount 被读成十亿，
        // 循环与越界寻址会把整帧拖到永不完成。
        _cullParams[8] = BitConverter.UInt32BitsToSingle((uint)count);
        _cullParams[9] = BitConverter.UInt32BitsToSingle((uint)InstanceCull.MaxSlots);
        _cullParams[10] = BitConverter.UInt32BitsToSingle((uint)primitives);
        _cullParams[11] = BitConverter.UInt32BitsToSingle((uint)capacity);
        _cullParams[12] = BitConverter.UInt32BitsToSingle((uint)slotMask);
        _cullParams[13] = 0f;
        _cullParams[14] = 0f;
        _cullParams[15] = 0f;

        _cullRefs[0] = _slotBuffer!;
        _cullRefs[1] = source;
        _cullRefs[2] = args;
        _cullRefs[3] = visible;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _cull!,
            Params = MemoryMarshal.AsBytes(_cullParams.AsSpan()),
            Resources = _cullRefs,
            GroupsX = (uint)((count + (int)InstanceCull.CullGroupSize - 1) / (int)InstanceCull.CullGroupSize),
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 3) gather：按序号表把可见实例物化到槽位分段里，顺便把实例数扇到其余 primitive。
        // 这一次派发扫的是容量而不是实例数，因为序号表是压缩过的，CPU 无从得知可见多少个。
        _gatherParams[0] = (uint)capacity;
        _gatherParams[1] = (uint)primitives;
        _gatherParams[2] = (uint)InstanceCull.MaxSlots;
        _gatherParams[3] = 0;
        _gatherRefs[0] = visible;
        _gatherRefs[1] = source;
        _gatherRefs[2] = sorted;
        _gatherRefs[3] = args;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _gather!,
            Params = MemoryMarshal.AsBytes(_gatherParams.AsSpan()),
            Resources = _gatherRefs,
            GroupsX = InstanceCull.GatherGroups(capacity),
            GroupsY = 1,
            GroupsZ = 1,
        });

        // 交接放到这里而不是绘制时：draw 在阴影 Pass 内部，Pass 内不允许切资源状态。
        target.PrepareCullDraw();
    }

    public override void OnUnregistered() => Dispose();

    public void Dispose()
    {
        InstanceCull.Enabled = false;
        if (_routed)
        {
            InstanceCull.Targets.Clear();
            _routed = false;
        }
        _reset?.Dispose(); _reset = null;
        _cull?.Dispose(); _cull = null;
        _gather?.Dispose(); _gather = null;
        _slotBuffer?.Dispose(); _slotBuffer = null;
        _graphics = null;
    }

    // ── Kernel declarations ──
    // reset:  [0] Params 16B / [1] StorageBufferRead primInfo -> t0 / [2] StorageBufferReadWrite args -> u0
    // cull:   [0] Params 64B / [1] StorageBufferRead slotMatrices -> t0 / [2] StorageBufferRead source -> t1
    //         [3] StorageBufferReadWrite args -> u0 / [4] StorageBufferReadWrite visible -> u1
    // gather: [0] Params 16B / [1] StorageBufferRead visible -> t0 / [2] StorageBufferRead source -> t1
    //         [3] StorageBufferReadWrite sorted -> u0 / [4] StorageBufferReadWrite args -> u1
    // 64B 远低于 Vulkan push constant 与 WebGPU uniform 的 128B 跨端上限。

    static ComputeKernelDesc CreateResetKernel() => new()
    {
        Name = "instanceCullReset",
        Source = new ShaderSourceSet
        {
            Hlsl = ResetHlsl, Glsl = ResetGlsl, Msl = ResetMsl, Wgsl = ResetWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceCull.ResetThreads,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateCullKernel() => new()
    {
        Name = "instanceCullMain",
        Source = new ShaderSourceSet
        {
            Hlsl = CullHlsl, Glsl = CullGlsl, Msl = CullMsl, Wgsl = CullWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 64 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceCull.CullGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };

    static ComputeKernelDesc CreateGatherKernel() => new()
    {
        Name = "instanceCullGather",
        Source = new ShaderSourceSet
        {
            Hlsl = GatherHlsl, Glsl = GatherGlsl, Msl = GatherMsl, Wgsl = GatherWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        WorkgroupX = InstanceCull.CullGroupSize,
        WorkgroupY = 1,
        WorkgroupZ = 1,
    };
}
