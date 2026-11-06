# S0 基线：绘制对象族重构的验收闸门

这份文档定义可绘制对象族重构（`S0`–`S7`）里 **S0** 的全部内容，以及后面每一步（S1…S7）必须重复执行的
四道判据。S0 本身不改任何运行时行为：它只增加观测手段，然后把当前的行为录下来。

## 为什么先做这一步

后面每一步都在搬代码：把 `DXPrimitiveGroup` / `VKPrimitiveGroup` / `MTLPrimitiveGroup` 的三份同构
绘制流程合并成共享核心，把三份 `MaterialParams` 的填充逻辑合并成 `MaterialOps`。这类重构的失败模式不是
"编译不过"，而是"编译过了、画面看起来也一样、但某一帧某一段状态切换顺序变了"。这类问题在真机上才会以
某个角度闪一下的形式出现，靠肉眼回归是抓不住的。

所以 S0 的产出不是一个结论，而是四把尺子：

| 判据 | 回答的问题 | 工具 |
|---|---|---|
| 布局冻结 | CPU 写的字节还是不是着色器读的那份布局 | `Tests/Shared/MaterialParamsLayoutChecks.cs` |
| 绘制序列 trace | 状态切换与绘制的**顺序**有没有变 | `Season/Rendering/DrawTrace.cs` |
| 截图 A/B | 画出来的像素有没有变 | `Apps/Engine/AbCapture.cs` + `.dbg/ab_pixel_diff.ps1` |
| CPU 帧时间 | 热路径有没有因为多了一层而变慢 | `Season/Rendering/CostProfile.cs` |

四条全部通过，才允许合入下一步。

## 1. 布局冻结

三端的 `MaterialParams` 是同一份语义、三份不同的字节布局。这不是疏忽，是三个 API 的常量缓冲规则不
一样（DX 的 cbuffer、VK 的 std140、Metal 的 `[[buffer(n)]]`），但它们之间的漂移已经超出必要范围：
`TintMode` 在 DX 是 124、在 VK/MTL 是 72；`BonePaletteStride` 是 72 / 84 / 84；`MorphWeights` 是
96 / 112 / 112。这些数字改错任何一个都不会产生编译错误，只会让画面在某个平台上悄悄错位。

冻结表在 `Tests/Shared/MaterialParamsLayoutChecks.cs`，两个测试工程用 `<Compile Include>` 共享同一份：

| 字段 | DirectX | Vulkan | Metal |
|---|---:|---:|---:|
| **总长** | **144** | **144** | **160** |
| BaseColor | 0 | 0 | 0 |
| EmissiveFactor | 16 | 16 | 16 |
| MetallicFactor / RoughnessFactor | 32 / 36 | 32 / 36 | 32 / 36 |
| UseAlbedoMap / Normal / MR / Occlusion / Emissive | 40 / 44 / 48 / 52 / 56 | 同左 | 同左 |
| AlphaCutoff | 60 | 60 | 60 |
| AlphaMode / RenderMode | 64 / 68 | 64 / 68 | 64 / 68 |
| TintMode | **124** | **72** | **72** |
| IsInstanced / IsSkinned | 76 / 80 | 76 / 80 | 76 / 80 |
| BonePaletteStride | **72** | **84** | **84** |
| HasMorphTargets / MorphTargetCount / MorphVertexCount | 84 / 88 / 92 | 88 / 92 / 96 | 88 / 92 / 96 |
| HasPrevBones / HasPrevInstanceWorld / HasPrevMorph | 112 / 116 / 120 | 100 / 104 / 108 | 100 / 104 / 108 |
| MorphWeights | **96** | **112** | **112** |
| PrevMorphWeights | — | — | **128** |
| VertexWave | 128 | 128 | **144** |

Metal 多出的 32 字节是 `PrevMorphWeights`：它没有 VK 那种额外的结构化缓冲可放上一帧的形变权重，只能
内联在常量缓冲里。

断言不只比偏移，还比**字段集合**：新字段如果被塞进已有的空洞，总长和所有旧偏移都不会变，只比偏移会漏掉它。

### 怎么跑

```powershell
# DirectX：反射 Season.Platforms.Windows.DirectX.MaterialParams
dotnet run --project SeasonEngine\Tests\Season.Rendering.DirectX.Tests -c Debug

# Vulkan（反射）+ Metal（解析源码）
dotnet run --project SeasonEngine\Tests\Season.Rendering.Metal.Tests -c Debug
```

Windows 上 `Platforms/Shared/Apple/**` 永远不参与编译，Metal 的 `MaterialParams` 在运行时根本不存在，
所以 Metal 那一路是**源码字面量解析**。它只能证明"源码写的和基线一致"，不能代替 `docs/acceptance/macos-metal-fog.sh`
的真机验收。源码路径由 `[CallerFilePath]` 反推；构建产物被搬到别处时用 `SEASON_SOURCE_ROOT` 指向 `Season` 目录。

DirectX 那一路在 `FogTest` 构造之前执行，所以没有可用适配器时也会先报布局问题。

## 2. 绘制序列 trace

### 打在哪里

不是逐处打，而是打在三端 `Pipeline` 的公共出口上：

| 后端 | 事件点 |
|---|---|
| DX | `Pipeline.SetPipeline`、`Pipeline.DrawPrimitive`、`Pipeline.DrawShadowPrimitive`、`DXSceneDrawCommand.Draw`（重放） |
| VK | `Pipeline.SetPipeline`、`Pipeline.DrawPrimitive`（收集）、`Pipeline.DrawPrimitiveCore`（提交）、`Pipeline.DrawShadowPrimitive` |
| MTL | `Pipeline.SetPipeline`、`Pipeline.SetCullMode`、`Pipeline.DrawPrimitive`、`Pipeline.DrawShadowPrimitive` |

选这三个出口是因为 2D、文字、精灵、模型、实例化、阴影、描边遮罩全部经由它们；逐个 `ApplySurfaceState`
调用点打点要改三端十几处，而且必然漏掉实例化路径。

分组标记 `G` 打在三端 `PrimitiveGroup` 的 `Draw` / `DrawShadow` / `DrawOutlineMask`（含实例化子类的
重写版本），用组名标注之后续绘制行，差异定位时能直接看到是哪个对象开始错位。

### 收集期与提交期分开记

三端都是"收集 → 重放"两段式：`Pipeline` 在收集期只记录状态与句柄，真正提交发生在 Pass 内重放。
trace 把两半都记下来，用倒数第二个字段区分：

- `c` = 收集期，代表**分组代码决定的顺序**；
- `.` = 提交期，代表**GPU 实际收到的顺序**。

这两者不是一回事：DX 的不透明桶可能被 `DiscardOpaque` 丢弃，透明桶会按深度重新排序，实例化会按实例拆表。
重构改的是前者，画面取决于后者，所以两个都要守。

### 行格式

制表符分隔，UTF-8 无 BOM。

```
# season-draw-trace v1 at=900 frames=1 openedAt=900
F	900
G	dx	main	<组名>	<图元数>
S	dx	.	<mode>	<cull>	<depthWrite>
C	mtl	c	<cull>
D	dx	replay	.	<doubleSided>	<aoExempt>	<indexCount>	<instanceCount>	<startInstance>	<partName>
```

- `F` 帧头，`G` 分组，`S` PSO/DSS 切换，`C` 剔除模式切换（只有 Metal 有，它的剔除是 encoder 动态状态），`D` 绘制。
- `mode` 是三端统一编码：`0` = Opaque，`1` = Fade，`2` = Transparent。**不是**枚举的整型值——三端枚举顺序
  是 `Opaque/Transparent/Fade`，直接转型会把 Fade 和 Transparent 标反，所以打点处一律用显式 `switch`。
- `cull`：`0` = None，`1` = Back，`2` = Front。
- `kind`：`scene`（场景入口）、`submit`（VK 的实际提交）、`replay`（DX 的重放提交）、`shadow`、`outline`。
- 绑定模式不在 `D` 行里重复：它已经在前一行 `S` 上，而在 Metal 上它根本不在任何绘制入口的可见范围内。

### 环境变量

| 变量 | 默认 | 作用 |
|---|---|---|
| `SEASON_DRAW_TRACE` | 未设置 = 关闭 | 输出文件全路径。没设置时每个状态切换/每次绘制只付一个可预测分支，不分配、不开文件 |
| `SEASON_DRAW_TRACE_AT` | `900` | 起始帧号，与 `AbCapture` 的取证帧对齐，避开加载期 |
| `SEASON_DRAW_TRACE_FRAMES` | `1` | 记录帧数。一帧已经是完整签名，多帧是给二分查找用的 |
| `SEASON_DRAW_TRACE_LINES` | `2000000` | 行数上限，防止忘记关闭时把磁盘写满 |

文件在进程退出时落盘（`ProcessExit` 钩子），异常一律吞掉——trace 是观测手段，不能把进程带崩。

### 判据

```powershell
# 基线：重构前
$env:SEASON_DRAW_TRACE = 'D:\Surface\SeasonEngine\.dbg\s0\dx-a.txt'
$env:SEASON_DRAW_TRACE_AT = '900'
$env:SEASON_DRAW_TRACE_FRAMES = '1'
dotnet run --project SeasonEngine\Apps\Engine -c Release

# 候选：改完再录一次
$env:SEASON_DRAW_TRACE = 'D:\Surface\SeasonEngine\.dbg\s0\dx-b.txt'
dotnet run --project SeasonEngine\Apps\Engine -c Release

powershell -NoProfile -ExecutionPolicy Bypass -File `
    SeasonEngine\docs\acceptance\s0-trace-diff.ps1 `
    -Baseline D:\Surface\SeasonEngine\.dbg\s0\dx-a.txt `
    -Candidate D:\Surface\SeasonEngine\.dbg\s0\dx-b.txt
```

脚本输出四个部分：帧窗口与行数、事件种类分布（`G`/`S`/`C`/`D` 各多少）、绘制阶段分布
（`scene`/`submit`/`replay`/`shadow`/`outline` 各多少）、第一处差异及其前后各六行上下文。

- 退出码 `0` = 逐行相同，可以合入。
- 退出码 `1` = 有差异。**不要**用"差异不多"来放行；先看事件分布判断是少了一整类（说明某个分支没走到）
  还是只是顺序变了（说明桶的判定条件变了）。
- 表头 `openedAt` 只在窗口内取值合法，不参与判据；`at` / `frames` 必须一致，否则两次运行不是同一帧窗口，
  比对没有意义。

更换后端时整套流程照搬，只把 `SEASON_DRAW_TRACE` 的路径换成对应名字。三端的 trace 之间不做横向比对：
它们的分桶与提交策略本来就不同，跨端比对没有判据。

## 3. 截图 A/B

设施已经存在（`Apps/Engine/AbCapture.cs`），不需要新代码。它做四件事保证可复现：固定 1/60 步长、
固定绝对帧号、粒子相位归零、单模型动画时钟重钉；稳定后重置时间与帧号再跑到取证帧，落 PNG 与侧车。

```powershell
$env:SEASON_AB_CAPTURE   = 'D:\Surface\SeasonEngine\.dbg\s0\a-dx.png'
$env:SEASON_AB_ABS_FRAME = '900'
$env:SEASON_AB_AA        = 'off'
$env:SEASON_AB_PARTICLES = 'off'
$env:SEASON_AB_LOD       = 'off'
dotnet run --project SeasonEngine\Apps\Engine -c Release
```

命名约定 `a-*` = 重构前，`b-*` = 重构后；`a-x` / `a-y` 这种同前缀的两次是噪声底，`a-*` 与 `b-*` 之间才是判据。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .dbg\ab_pixel_diff.ps1 -Dir D:\Surface\SeasonEngine\.dbg\s0 -Grid
```

判读方式：`CROSS / SAME` 比值接近 1 表示跨路径差异没有超出同路径重复运行时的波动；显著大于 1 才需要
回头查。逐像素全量比对，不做抽样——抽样会把真实差异藏起来。

## 4. CPU 帧时间

`Season/Rendering/CostProfile.cs` 输出帧级与 Pass 级两张 CSV：

```powershell
$env:SEASON_PROFILE_OUT      = 'D:\Surface\SeasonEngine\.dbg\s0\profile-before'
$env:SEASON_PROFILE_SCENARIO = '<场景名>'
$env:SEASON_PROFILE_CAMERA   = '<相机名>'
$env:SEASON_PROFILE_WARMUP   = '120'
$env:SEASON_PROFILE_FRAMES   = '600'
$env:SEASON_PROFILE_EXIT     = '1'
dotnet run --project SeasonEngine\Apps\Engine -c Release
```

改前改后各跑一次，比对两张 CSV 的帧级中位数与 `Collect` / `Replay` 两个区间的占比。判据不是绝对毫秒
数（机器状态会漂），而是**同一个 `Collect` 占比不能因为多了一层而上升**。重构把代码收进共享核心时，
这是唯一能发现"多了一层虚调用"的手段。

## 5. 既有测试全绿

前面四条跑完之后，既有套件必须仍然是零失败、零 validation 消息：

```powershell
dotnet run --project SeasonEngine\Tests\Season.Rendering.DirectX.Tests -c Debug   # 需要 GPU，可用 --warp
dotnet run --project SeasonEngine\Tests\Season.Rendering.Metal.Tests  -c Debug    # 无 GPU 契约断言
dotnet run --project SeasonEngine\Tests\Season.Rendering.Vulkan.Tests -c Debug    # 需要 Vulkan 设备
dotnet run --project SeasonEngine\Tests\Season.Rendering.Tests        -c Debug    # net10.0-browser
```

## 每一步之后的验收流程

S1…S7 每一步都按同一顺序走，不允许跳步：

1. `dotnet build` 全部 TFM（`net10.0`、`net10.0-windows10.0.19041.0`、`net10.0-ios`、`net10.0-browser`）
   零错误。Apple 目标在 Windows 上也能编译，只是不能运行——这一步就是靠它抓 Metal 侧的语法与类型错误。
2. `Season.Rendering.DirectX.Tests` 与 `Season.Rendering.Metal.Tests` 全绿（布局冻结含在其中）。
3. 同设备录 `dx-a` / `dx-b` 两份 trace，`s0-trace-diff.ps1` 退出码为 0。
4. 同设备截 `a-*` / `b-*` 两张图，`ab_pixel_diff.ps1` 的 `CROSS / SAME` 接近 1。
5. `CostProfile` 两张 CSV，`Collect` 占比未上升。
6. 旧文件删除之后再跑一遍第 3 步——这一步专门抓"分享同一个核心之后，某个后端的分支被静默删掉了"。

Windows 上能做 1–6；VK 与 MTL 的第 3–5 步必须在各自的真机上重复一次，`docs/acceptance/macos-metal-fog.sh`
与 Android 验收脚本是它们的入口。

## 已知限制

- Windows 上无法反射 Metal 的 `MaterialParams`，布局只能靠源码解析；这不是等价证明。
- trace 是渲染线程独占的，不加锁；从别的线程调 `Pipeline` 的后端不支持（当前没有这种后端）。
- trace 只记录状态与绘制，不记录资源绑定与着色器常量内容；它回答"顺序对不对"，不回答"数值对不对"。
- 截图 A/B 只能证明"这一帧一样"，不能证明整段动画一样；所以判据里 trace 与截图必须同时通过。
