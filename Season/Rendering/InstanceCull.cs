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
    /// 每次派发之前回调，让实现方把剔除的 CPU 侧输入（每实例矩阵流与 primitive 参数表）传到 GPU。
    ///
    /// 这个回调存在的唯一理由是"相位"：GPU 上传要求命令列表已打开且不在 Pass 内，
    /// 而控制件的 Update 两个条件都不满足。Windows 主循环先跑 App.Update 再调 Device.BeforeRender，
    /// 于是 Update 阶段命令列表还停在上一帧关闭后的状态 —— DX12 在那里录 barrier 直接抛 SEH，
    /// Vulkan 则被 IsRecording 判定静默丢弃（缓冲保持全零，整组实例被误剔，画面只是"少了一批东西"）。
    /// 两端连失败方式都不一样，正说明上传时机不该由控制件自己决定，而该由"命令列表此刻能不能录"决定。
    /// 本回调由 <see cref="InstanceCullEffect"/> 在 Record 里调用，刚好落在 BeforeRender 之后、
    /// 阴影 Pass 之前，是四端唯一共同满足条件的相位。
    ///
    /// 返回 false 表示本帧输入不可用，调用方必须跳过该目标；绘制端会自行退回 CPU 整批剔除。
    /// </summary>
    bool PrepareCullInputs();

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

    /// <summary>
    /// gather 内核沿 X 的组数上限，X 维是一个槽位内的候选序号。按容量算而不是按实例数算：
    /// 序号表是压缩过的，CPU 无从得知可见多少个，只有 GPU 知道。
    /// 走间接派发时这一维改由 finalize 内核写成"最大槽位可见数对应的组数"，容量在这里只是上界。
    /// </summary>
    public static uint GatherGroupsX(int capacity) => (uint)((long)capacity + CullGroupSize - 1) / CullGroupSize;

    /// <summary>gather 内核沿 Y 的组数，一个槽位一行。两条派发路径都用这个值：A/B 对比只允许 X 维变化。</summary>
    public const uint GatherGroupsY = MaxSlots;

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

    /// <summary>
    /// 进程内走间接派发的 gather 次数。诊断用，两个数字合起来是"这一轮 A/B 到底走了哪条路"的唯一证据：
    /// 只比较两张截图是不够的——两条路都正确时它们必然相同，但"gather 压根没被派发"同样会让它们相同，
    /// 而那种情况下这组对比什么也没证明。
    ///
    /// 累加发生在 <see cref="InstanceCullEffect"/> 的 DispatchTarget 里，每目标每帧最多加一，
    /// 所以它与帧数的比值就是实际参与剔除的目标数。
    /// </summary>
    public static long IndirectGatherDispatches;

    /// <summary>进程内走固定组数派发的 gather 次数，与 <see cref="IndirectGatherDispatches"/> 互斥且穷尽。</summary>
    public static long FixedGatherDispatches;

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
/// 每组每帧固定四次派发：reset 填参数表、cull 分槽位压缩、finalize 把可见数归约成 gather 的派发尺寸、
/// gather 按序号物化重排流并把实例数扇到其余 primitive。四次都必须在 Pass 之外，这一点与粒子系统的
/// A0.1 链路完全相同，区别只在于这里产出的是"每槽位一份间接参数 + 一份原始序号表"，而不是一条唯一的
/// apply 命令。
///
/// finalize 只在 gather 走间接派发时才录制（<see cref="IndirectGather"/>）。固定派发不需要它：
/// CPU 手里的容量就是个足够用的上界，多派出去的空线程各自读一个 uint 就早退。
/// </summary>
public sealed partial class InstanceCullEffect : ComputeEffect, IDisposable
{
    public override string Name => "instanceCull";

    public override ComputePhase Phase => ComputePhase.FrameStart;

    /// <summary>
    /// A7 gather 的派发方式开关，也是 A0.2 的第一处真实消费者。
    ///
    /// 打开时 gather 的组数由 GPU 决定：finalize 内核把"每槽位可见数的最大值"折成组数写进一个
    /// 12 字节的 <see cref="IndirectDispatchArgs"/> 块，gather 用 ExecuteIndirect / CmdDispatchIndirect
    /// 一类原语消费它。关闭时 CPU 按容量上界固定派发。
    ///
    /// 两条路径的**线程映射完全相同**（X = 槽位内序号，Y = 槽位），唯一变量是 X 维的组数，
    /// 所以两次运行的画面必须逐像素一致 —— 这就是 A/B 验证的判据。任何差异都是派发契约的 bug，
    /// 不是"性能优化的副作用"。
    ///
    /// 默认关闭，用环境变量开启，因为它存在的理由是验证而不是提速。实测收益取决于容量与可见数的
    /// 差距：容量按 64 对齐，而可见数常常只是容量的一个零头，省下的就是那批"读一个 uint 就退出"的
    /// 空转线程 —— 它们各自很便宜，但数量按槽位数倍乘。
    /// </summary>
    public static bool IndirectGather { get; set; } =
        Environment.GetEnvironmentVariable("SEASON_CULL_INDIRECT") == "1";

    readonly Vector4[] _slotMatrices = new Vector4[InstanceCull.MaxSlots * 4];
    readonly uint[] _resetParams = new uint[4];
    readonly uint[] _gatherParams = new uint[4];
    readonly uint[] _finalizeParams = new uint[4];
    readonly float[] _cullParams = new float[16];
    readonly ComputeResourceRef[] _resetRefs = new ComputeResourceRef[2];
    readonly ComputeResourceRef[] _cullRefs = new ComputeResourceRef[4];
    readonly ComputeResourceRef[] _finalizeRefs = new ComputeResourceRef[2];
    readonly ComputeResourceRef[] _gatherRefs = new ComputeResourceRef[4];

    IGraphics? _graphics;
    ComputeKernel? _reset, _cull, _finalize, _gather;
    StorageBuffer? _slotBuffer;

    /// <summary>
    /// 12 字节的派发参数块，由 finalize 内核写、gather 读。
    ///
    /// 全部剔除目标共用一份，因为同一帧里每一组的四次派发都是背靠背录制的：GPU 读完这个块跑完
    /// gather 之后，下一组的 finalize 才会覆盖它。共享省掉的是"每组建一个 12 字节缓冲"，
    /// 代价是将来若改成并行录制多组就必须拆开 —— 到那时这个字段要变成 per-target。
    /// </summary>
    StorageBuffer? _dispatchArgs;

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
            _finalize = g.CreateComputeKernel(CreateFinalizeKernel());
            _gather = g.CreateComputeKernel(CreateGatherKernel());
            if (_reset == null || _cull == null || _finalize == null || _gather == null) { Dispose(); return false; }
            _slotBuffer = g.CreateStorageBuffer(InstanceCull.SlotMatrixBytes);
            if (_slotBuffer == null || _slotBuffer.SizeInBytes < InstanceCull.SlotMatrixBytes) { Dispose(); return false; }

            // 派发参数块只要后端支持就建，不跟着开关走：开关是"这一帧想走哪条路"，缓冲是"能不能走"。
            // 让两者解耦，A/B 就能在同一进程内逐帧切换而不必重启 —— 逐像素比对需要的正是这一点。
            // 拿不到就退回固定派发，而不是让整个 A7 失效：剔除本身与"gather 怎么被派发"是两件事。
            if (g.DispatchIndirectSupported)
            {
                _dispatchArgs = g.CreateStorageBuffer(Season.Rendering.IndirectDispatchArgs.SizeInBytes,
                    Season.Rendering.StorageBufferUsage.ComputeAndIndirectArgs);
                if (_dispatchArgs == null
                    || _dispatchArgs.SizeInBytes < Season.Rendering.IndirectDispatchArgs.SizeInBytes)
                {
                    _dispatchArgs = null;
                    DeviceServices.BaseApp.AddLog(LogType.Backend,
                        "[InstanceCull] 间接派发参数块创建失败，gather 退回固定组数派发。");
                }
            }

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
        if (!ReferenceEquals(g, _graphics) || _reset == null || _cull == null || _finalize == null
            || _gather == null || _slotBuffer == null) return;
        if (!CascadedShadow.SunActive && !CascadedShadow.SpotActive) return;
        // 阴影图复用命中时整个阴影 Pass 会被跳过，剔除与收集的产物没有任何消费者：
        // 这一帧的四次派发（reset/cull/finalize/gather）连同每槽位的绘制参数都是纯浪费，直接早退。
        // 早退安全的前提是本帧没有人读这些缓冲——绘制端只由阴影 Pass 驱动，而它这一帧不存在。
        if (CascadedShadow.ReuseActive) return;
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
        if (count <= 0) return;

        // 上传排在 CullPrimitiveCount 的读取之前，而不是排在容量校验之后：
        // Web 端那个属性发布的是"参数表已上传"的条数，若先读它再上传，首次运行时它还是 -1，
        // 校验当场早退，上传于是永远没有机会发生 —— 一个自己锁死自己的顺序。
        if (!target.PrepareCullInputs()) return;

        int capacity = target.CullCapacity;
        int primitives = target.CullPrimitiveCount;
        if (capacity < count || primitives <= 0 || primitives > InstanceCull.MaxPrimitives) return;

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

        // 3) finalize（仅间接路径）：把每槽位的可见数归约成"X 维要派发几组"。
        //    尺寸只能由 GPU 给出 —— 序号表是压缩过的，CPU 手里只有容量这个上界。
        //    它是四端里唯一一个 [numthreads(1,1,1)] 的内核：槽位数上限是 4，串行读四个 uint
        //    比开一个工作组做归约再同步便宜得多，也不必为此在四端各写一遍 threadsync 的方言。
        bool indirect = IndirectGather && _dispatchArgs != null;
        if (indirect)
        {
            // 扫的槽位数就是 gather 的 Y 维组数，直接取 MaxSlots 而不是 GatherGroupsY：
            // 两者眼下相等，但语义不同 —— 后者是"派发多少行"，前者是"参数表里有几行"。
            _finalizeParams[0] = (uint)InstanceCull.MaxSlots;
            // 槽位跨度 = 一个槽位占多少字节的参数表。写成参数而不是让内核自带常量：
            // 那条 MAX_PRIMITIVES × 20 的乘法是 reset 与 gather 共同依赖的契约，
            // 多写一份就多一个漂移点。
            _finalizeParams[1] = (uint)InstanceCull.MaxPrimitives * InstanceCull.ArgsStride;
            _finalizeParams[2] = InstanceCull.CullGroupSize;
            _finalizeParams[3] = 0;
            _finalizeRefs[0] = args;
            _finalizeRefs[1] = _dispatchArgs!;
            g.DispatchCompute(new ComputeDispatchArgs
            {
                Kernel = _finalize!,
                Params = MemoryMarshal.AsBytes(_finalizeParams.AsSpan()),
                Resources = _finalizeRefs,
                GroupsX = 1,
                GroupsY = 1,
                GroupsZ = 1,
            });
        }

        // 4) gather：按序号表把可见实例物化到槽位分段里，顺便把实例数扇到其余 primitive。
        // X 维是槽位内序号，Y 维是槽位。这个二维映射两条派发路径完全共用 —— 否则 A/B 对比的
        // 就不是"派发方式"，而是两套寻址，任何差异都无从归因。
        _gatherParams[0] = (uint)capacity;
        _gatherParams[1] = (uint)primitives;
        _gatherParams[2] = (uint)InstanceCull.MaxSlots;
        _gatherParams[3] = 0;
        _gatherRefs[0] = visible;
        _gatherRefs[1] = source;
        _gatherRefs[2] = sorted;
        _gatherRefs[3] = args;
        // 计数器放在派发点而不是分支判定处：将来若 finalize 被合并进别的内核，分支还在、gather 却可能
        // 不再被派发，那时候数字要与真实派发一一对应才有诊断价值。
        if (indirect) InstanceCull.IndirectGatherDispatches++;
        else InstanceCull.FixedGatherDispatches++;
        g.DispatchCompute(new ComputeDispatchArgs
        {
            Kernel = _gather!,
            Params = MemoryMarshal.AsBytes(_gatherParams.AsSpan()),
            Resources = _gatherRefs,
            // 间接路径下这一维被参数块取代，写 0 而不是写一个永远用不上的容量：
            // 抓帧时"0 组"是"去看参数块"的最短提示。
            GroupsX = indirect ? 0u : InstanceCull.GatherGroupsX(capacity),
            GroupsY = InstanceCull.GatherGroupsY,
            GroupsZ = 1,
            Arguments = indirect ? _dispatchArgs : null,
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
        _finalize?.Dispose(); _finalize = null;
        _gather?.Dispose(); _gather = null;
        _slotBuffer?.Dispose(); _slotBuffer = null;
        _dispatchArgs?.Dispose(); _dispatchArgs = null;
        _graphics = null;
    }

    // ── Kernel declarations ──
    // reset:    [0] Params 16B / [1] StorageBufferRead primInfo -> t0 / [2] StorageBufferReadWrite args -> u0
    // cull:     [0] Params 64B / [1] StorageBufferRead slotMatrices -> t0 / [2] StorageBufferRead source -> t1
    //           [3] StorageBufferReadWrite args -> u0 / [4] StorageBufferReadWrite visible -> u1
    // finalize: [0] Params 16B / [1] StorageBufferRead args -> t0 / [2] StorageBufferReadWrite dispatch -> u0
    // gather:   [0] Params 16B / [1] StorageBufferRead visible -> t0 / [2] StorageBufferRead source -> t1
    //           [3] StorageBufferReadWrite sorted -> u0 / [4] StorageBufferReadWrite args -> u1
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

    static ComputeKernelDesc CreateFinalizeKernel() => new()
    {
        Name = "instanceCullFinalize",
        Source = new ShaderSourceSet
        {
            Hlsl = FinalizeHlsl, Glsl = FinalizeGlsl, Msl = FinalizeMsl, Wgsl = FinalizeWgsl, EntryPoint = "CSMain",
        },
        Bindings = new[]
        {
            new ComputeBindingDesc { Type = ComputeBindingType.Params, SizeInBytes = 16 },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferRead },
            new ComputeBindingDesc { Type = ComputeBindingType.StorageBufferReadWrite },
        },
        // 一个线程。归约的上限是 MaxSlots(4) 个 uint，开一整个工作组只为了同步四个读远比它本身贵，
        // 更不用说四端各写一遍 threadsync 的方言（GroupMemoryBarrierWithGroupSync / barrier /
        // threadgroup_barrier / workgroupBarrier）就意味着四份可能漂移的实现。
        WorkgroupX = 1,
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
