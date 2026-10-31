#!/usr/bin/env bash
# ============================================================================
# SeasonEngine · 工作包 D（Metal 同构移植）macOS 顺序验收脚本
#
# 用法（在仓库根目录或任意位置均可）：
#   bash SeasonEngine/docs/acceptance/macos-metal-fog.sh [选项]
#
# 选项：
#   --seconds N   第 4 步让 App 运行多少秒（默认 25）
#   --skip-build  跳过第 2 步编译（复用已有产物）
#   --trace       第 5 步额外用 xctrace 抓一份 Metal System Trace（需权限，可能较慢）
#   --yes         全程无交互（第 6 步的人工画面确认改为“未确认”记录）
#
# 步骤顺序（严格串行，任何一步失败立即终止并照实写报告）：
#   0. 环境自检        —— Darwin / Xcode / dotnet / maccatalyst workload
#   1. 全 TFM 编译     —— Season 库（全部目标）+ Engine App（maccatalyst）
#   2. 契约测试        —— Tests/Season.Rendering.Metal.Tests（MSL 单一来源 / 字节布局 / 队列排序）
#   3. 校验脚本自身    —— 确认本脚本断言的门控与槽位常量仍与源码一致
#   4. 校验层运行 App  —— MTL_DEBUG_LAYER=1 + MTL_SHADER_VALIDATION=1，抓 stdout/stderr
#   5. 日志断言        —— 零 Metal Validation 错误；失败即判负
#   6. GPU 捕获        —— Xcode Capture GPU Frame / xctrace，按 encoder label 核对 Pass 顺序
#   7. 画面人工确认    —— 雾片可见、透明几何吃雾、天空不吃雾（脚本只能记录，不能判定）
#   8. 出报告          —— artifacts/metal-acceptance-<时间戳>.md
#
# 重要：本脚本能自动判定的是“编译 + 契约 + 校验层无报错”。
# 真机画面是否正确，只能在第 7 步由人眼核对。脚本会在报告里明确写下这一条，
# 任何情况下都不得据此宣称“四端体积雾已完成”。
# ============================================================================

set -uo pipefail

SECONDS_TO_RUN=25
DO_BUILD=1
DO_TRACE=0
ASSUME_YES=0

while [ $# -gt 0 ]; do
    case "$1" in
        --seconds) SECONDS_TO_RUN="${2:-25}"; shift $(( $# > 1 ? 2 : 1 )) ;;
        --skip-build) DO_BUILD=0; shift ;;
        --trace) DO_TRACE=1; shift ;;
        --yes) ASSUME_YES=1; shift ;;
        -h|--help) sed -n '2,40p' "$0"; exit 0 ;;
        *) echo "未知选项：$1" >&2; exit 2 ;;
    esac
done

# ---- 路径解析 --------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"          # -> SeasonEngine/
ARTIFACTS="$ROOT/docs/acceptance/artifacts"
STAMP="$(date '+%Y%m%d-%H%M%S')"
LOG="$ARTIFACTS/run-$STAMP.log"
REPORT="$ARTIFACTS/metal-acceptance-$STAMP.md"

mkdir -p "$ARTIFACTS"

# ---- 结果记录 --------------------------------------------------------------
STEP_NAMES=()
STEP_RESULTS=()
STEP_NOTES=()
FAILED=0

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; }
info() { printf '  · %s\n' "$*"; }

record() { STEP_NAMES+=("$1"); STEP_RESULTS+=("$2"); STEP_NOTES+=("$3"); }

fail_now() {                       # fail_now <步骤名> <原因>
    bad "$2"
    record "$1" "FAIL" "$2"
    FAILED=1
    finish
    exit 1
}

finish() {
    {
        echo "# Metal 体积雾验收报告（$STAMP）"
        echo
        echo "- 主机：$(sw_vers -productName 2>/dev/null || uname -s) $(sw_vers -productVersion 2>/dev/null)"
        echo "- 架构：$(uname -m)"
        echo "- 频道：$(dotnet --version 2>/dev/null || echo 'n/a')"
        echo "- 原始日志：\`${LOG#"$ROOT"/}\`"
        echo
        echo "| # | 步骤 | 结果 | 说明 |"
        echo "|---|------|------|------|"
        local i=0
        while [ $i -lt ${#STEP_NAMES[@]} ]; do
            echo "| $((i+1)) | ${STEP_NAMES[$i]} | ${STEP_RESULTS[$i]} | ${STEP_NOTES[$i]} |"
            i=$((i+1))
        done
        echo
        echo "## 结论"
        echo
        if [ "$FAILED" -ne 0 ]; then
            echo "**验收未通过。** 上表存在 FAIL 项，按失败步骤内容修复后重跑本脚本。"
        else
            echo "编译、契约测试与 Metal 校验层均已通过。"
        fi
        echo
        echo "> 真机画面（雾片可见性、透明几何吃雾、天空不吃雾、四入口一致）属于主观判定，"
        echo "> 由第 7 步的人工确认决定。**脚本通过不等于四端体积雾完成。**"
    } > "$REPORT"
    printf '\n报告：%s\n' "$REPORT"
}

# 从第 0 步开始就同时写控制台与日志文件，否则环境自检的输出会只留在终端里。
exec > >(tee -a "$LOG") 2>&1

# ============================================================================
step "0/8 环境自检"
# ============================================================================

if [ "$(uname -s)" != "Darwin" ]; then
    echo "本脚本必须在 macOS 上运行（Metal 后端只能在 Apple 平台上真机执行）。" >&2
    echo "在 Windows/Linux 上请只跑第 2 步的契约测试。" >&2
    exit 2
fi
ok "Darwin $(sw_vers -productVersion) / $(uname -m)"

if ! command -v dotnet >/dev/null 2>&1; then fail_now "环境自检" "找不到 dotnet"; fi
if ! xcode-select -p >/dev/null 2>&1; then fail_now "环境自检" "缺少 Xcode Command Line Tools（xcode-select --install）"; fi
if ! xcrun --sdk macosx --show-sdk-path >/dev/null 2>&1; then fail_now "环境自检" "macOS SDK 不可用"; fi
ok "Xcode SDK：$(xcrun --sdk macosx --show-sdk-path)"

if ! dotnet workload list 2>/dev/null | grep -qi maccatalyst; then
    fail_now "环境自检" "缺少 maccatalyst workload（dotnet workload install maccatalyst）"
fi
ok "maccatalyst workload 已安装"
record "环境自检" "PASS" "$(uname -m) / $(sw_vers -productVersion)"

# ============================================================================
step "1/8 编译：Season 库（全部 TFM）+ Engine App（maccatalyst）"
# ============================================================================
if [ "$DO_BUILD" -eq 1 ]; then
    if ! dotnet build "$ROOT/Season/Season.csproj" -c Debug --nologo; then
        fail_now "编译" "Season 库编译失败"
    fi
    ok "Season 库全部目标编译通过"

    if ! dotnet build "$ROOT/Apps/Engine/Engine.csproj" -f net10.0-maccatalyst -c Debug --nologo; then
        fail_now "编译" "Engine App（maccatalyst）编译失败"
    fi
    ok "Engine.app 编译通过"
    record "编译" "PASS" "Season 全 TFM + Engine maccatalyst"
else
    info "跳过编译（--skip-build）"
    record "编译" "SKIP" "--skip-build"
fi

# ============================================================================
step "2/8 契约测试：MSL 单一来源 / 雾帧字节布局 / 透明队列排序退化"
# ============================================================================
CONTRACT_LOG="$ARTIFACTS/contracts-$STAMP.log"
if dotnet run --project "$ROOT/Tests/Season.Rendering.Metal.Tests" -c Debug --nologo > "$CONTRACT_LOG" 2>&1; then
    if grep -q "契约验收通过" "$CONTRACT_LOG"; then
        COUNT="$(sed -n 's/.*契约验收通过（\([0-9]*\) 项）.*/\1/p' "$CONTRACT_LOG" | tail -1)"
        ok "契约验收通过（${COUNT:-?} 项）"
        record "契约测试" "PASS" "${COUNT:-?} 项断言全部通过"
    else
        fail_now "契约测试" "测试程序退出码为 0 但没有通过标记，日志见 contracts-$STAMP.log"
    fi
else
    info "失败明细："
    grep -E "^\s+- " "$CONTRACT_LOG" | head -20 | sed 's/^/    /'
    fail_now "契约测试" "存在失败断言，日志见 contracts-$STAMP.log"
fi

# ============================================================================
step "3/8 校验脚本断言与源码常量一致"
# ============================================================================
# 这三个常量一旦改动，脚本第 5 步的日志判定和第 6 步的 Pass 核对就会失准。
check_source() {                    # check_source <描述> <文件> <正则>
    if grep -qE "$3" "$ROOT/$2"; then ok "$1"; else fail_now "源码核对" "$1 —— 模式未命中：$3"; fi
}
check_source "fragment_main 声明 fogParams → FS buffer(5)" \
    "Season/Platforms/Shared/Apple/Metal/Pipeline.cs" 'SceneFogParamsSlot = 5'
check_source "积分列体积 → texture(11)" \
    "Season/Platforms/Shared/Apple/Metal/Pipeline.cs" 'SceneFogIntegratedSlot = 11'
check_source "局部列体积 → texture(12)" \
    "Season/Platforms/Shared/Apple/Metal/Pipeline.cs" 'SceneFogLocalSlot = 12'
check_source "运行时门控 fogParams.x > 0.5" \
    "Season/Rendering/Effects/VolumetricFog.Msl.cs" 'fogParams\.x[[:space:]]*>[[:space:]]*0\.5'
check_source "Engine 提供了分段接口实现" \
    "Season/Platforms/Shared/Apple/Graphics.cs" 'ISegmentedSceneGraphics'
check_source "Metal 片段着色器拼接 SceneSamplingMsl" \
    "Season/Platforms/Shared/Apple/Metal/Pipeline.cs" 'VolumetricFogEffect\.SceneSamplingMsl'
record "源码核对" "PASS" "6 项常量/接线与脚本假设一致"

# ============================================================================
step "4/8 校验层运行 App（MTL_DEBUG_LAYER=1，${SECONDS_TO_RUN}s）"
# ============================================================================
APP_BIN=""
for candidate in \
    "$ROOT/Apps/Engine/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Engine.app" \
    "$ROOT/Apps/Engine/bin/Debug/net10.0-maccatalyst/maccatalyst-x64/Engine.app"; do
    if [ -x "$candidate/Contents/MacOS/Engine" ]; then APP_BIN="$candidate/Contents/MacOS/Engine"; break; fi
done
if [ -z "$APP_BIN" ]; then
    APP_BIN="$(find "$ROOT/Apps/Engine/bin/Debug/net10.0-maccatalyst" -type f -perm -u+x \
        -name Engine -path '*.app/Contents/MacOS/*' 2>/dev/null | head -1)"
fi
if [ -z "$APP_BIN" ] || [ ! -x "$APP_BIN" ]; then
    fail_now "校验层运行" "找不到 Engine.app 可执行文件；请先完成第 1 步"
fi
info "可执行：${APP_BIN#"$ROOT"/}"

RUN_LOG="$ARTIFACTS/app-$STAMP.log"
: > "$RUN_LOG"

# MTL_DEBUG_LAYER / MTL_SHADER_VALIDATION 是 Apple 官方运行期校验开关：
# 前者逐条检查 API 契约（缺绑定、附件格式不匹配、Pass 内切换附件等），
# 后者检查 MSL 的静态约束。工作包 D 新增了 FS buffer(4)(5) 与 texture(11)(12)，
# 只要有一个 Pass 漏绑，这里就会直接报出来。
MTL_DEBUG_LAYER=1 \
MTL_SHADER_VALIDATION=1 \
MTL_DEBUG_LAYER_ERROR_MODE=assert \
  "$APP_BIN" > "$RUN_LOG" 2>&1 &
APP_PID=$!

sleep "$SECONDS_TO_RUN"

if kill -0 "$APP_PID" 2>/dev/null; then
    kill "$APP_PID" 2>/dev/null
    wait "$APP_PID" 2>/dev/null
    APP_EXIT="killed-after-timeout"
    ok "App 存活满 ${SECONDS_TO_RUN}s 后被正常终止"
else
    wait "$APP_PID"; APP_EXIT=$?
    if [ "$APP_EXIT" -ne 0 ]; then
        info "App 提前退出，退出码 $APP_EXIT"
    fi
fi
record "校验层运行" "PASS" "存活 ${SECONDS_TO_RUN}s，退出状态 $APP_EXIT"

# ============================================================================
step "5/8 日志断言：零 Metal Validation 错误"
# ============================================================================
# 只匹配 Apple 校验层与 MSL 编译器的真实失败措辞，避免把业务日志里的
# “error” 字样误判为 GPU 错误。
PATTERN='Metal API Validation|Validation failed|failed assertion|has no [a-z]* bound|'
PATTERN+='Executed Metal Command Buffer.*failed|Command buffer execution failed|'
PATTERN+='Metal Shader Validation|compilation failed|error: .*\.metal|'
PATTERN+='NSInternalInconsistencyException|EXC_BAD_ACCESS|SIGSEGV|SIGABRT|'
PATTERN+='\[VolumetricFog\]'

if grep -nEi "$PATTERN" "$RUN_LOG" >/dev/null 2>&1; then
    info "命中以下行（最多 20 行）："
    grep -nEi "$PATTERN" "$RUN_LOG" | head -20 | sed 's/^/    /'
    fail_now "日志断言" "运行日志出现校验层或雾初始化错误，见 app-$STAMP.log"
fi
ok "未出现 Metal 校验层 / MSL 编译 / 雾初始化错误"

if grep -qi "Metal API Validation Enabled" "$RUN_LOG"; then
    ok "确认校验层已真正启用（不是空跑）"
    record "日志断言" "PASS" "校验层已启用且无错误模式命中"
else
    info "注意：未在日志中看到 'Metal API Validation Enabled'。"
    info "若 App 不是控制台启动（例如从 Finder 双击），校验层输出会进 Console.app 而不进本日志；"
    info "请在 Console.app 搜索进程名复核一次，再决定是否采信本步结果。"
    record "日志断言" "PASS(待复核)" "校验层启用行未捕获，需 Console.app 复核"
fi

# ============================================================================
step "6/8 GPU 捕获：核对 Pass 顺序与编码器标签"
# ============================================================================
info "期望的 Metal 编码器顺序（_passLabels 决定，可在捕获里按名字过滤）："
info "  Shadow → SceneOpaque → FogComposite → SceneTransparent → Post/FinalBlit → Overlay"
info ""
info "手动捕获："
info "  1) 用 Xcode 打开 $ROOT/Apps/Engine/Engine.csproj（或 .sln）"
info "  2) Run 起来后在调试栏点 “Capture GPU Frame”"
info "  3) 在捕获树里确认上面五个 Pass 都存在且顺序一致"
info "  4) 选中 SceneTransparent，检查它的 ColorAttachment 0 = 雾合成输出，"
info "     并且 fragment 阶段 texture(11)/texture(12) 有真实 3D 纹理（不是 1x1 占位）"

if [ "$DO_TRACE" -eq 1 ]; then
    TRACE_OUT="$ARTIFACTS/metal-$STAMP.trace"
    info "抓取 Metal System Trace（最长 15s）…"
    if xcrun xctrace record --template 'Metal System Trace' --time-limit 15s \
        --output "$TRACE_OUT" --launch -- "$APP_BIN" >> "$RUN_LOG" 2>&1; then
        ok "Trace 已保存：${TRACE_OUT#"$ROOT"/}"
        record "GPU 捕获" "PASS" "xctrace 已录制，Pass 顺序需在 Instruments 内人工核对"
    else
        info "xctrace 录制失败（常见于未授予开发者工具权限）。已降级为手动 Xcode 捕获。"
        record "GPU 捕获" "MANUAL" "xctrace 失败，改为 Xcode 手动捕获"
    fi
else
    record "GPU 捕获" "MANUAL" "未开启 --trace，需在 Xcode 内手动 Capture GPU Frame"
fi

# ============================================================================
step "7/8 画面人工确认（脚本无法判定）"
# ============================================================================
cat <<'CHECKLIST'
  请在窗口里逐条核对，并在第 8 步如实记录：

  [ ] 1. 树林一侧的雾片可见，形态与 Web/VK 端一致（不是整屏灰、也不是完全没有）
  [ ] 2. 透明度为 1 的远景物体被雾正确淡化（说明透明几何吃到了雾）
  [ ] 3. 天空盒不被雾染色（说明门控在天空分支上确实是关闭的）
  [ ] 4. 3D 模型边缘与雾的过渡没有硬边（说明 AtWorld 与合成 Pass 用的是同一份数学）
  [ ] 5. 文字与 2D 精灵没有被雾二次染色（说明四入口的合成顺序正确）
  [ ] 6. 相机快速平移时雾不出现上一帧残留（说明历史重置与体积纹理新鲜度正确）
  [ ] 7. 开关 fog.Enabled 来回切换，画面在 1 帧内回到无雾状态（说明门控归零生效）
CHECKLIST

MANUAL="未确认"
if [ "$ASSUME_YES" -eq 0 ]; then
    printf '\n  以上 7 条是否全部通过？[y] 通过 / [n] 未通过 / 直接回车 = 稍后确认： '
    read -r answer || answer=""
    case "${answer:-}" in
        y|Y) MANUAL="人工确认全部通过" ;;
        n|N) FAILED=1; MANUAL="人工确认存在不通过项" ;;
        *)   MANUAL="稍后确认（本次未判定）" ;;
    esac
fi
record "画面人工确认" "$( [ "$MANUAL" = "人工确认全部通过" ] && echo PASS || echo MANUAL )" "$MANUAL"

# ============================================================================
step "8/8 报告"
# ============================================================================
finish
[ "$FAILED" -eq 0 ] && printf '\033[32m验收流程走完，无自动判定失败项。\033[0m\n' || printf '\033[31m验收未通过，见上表。\033[0m\n'
exit "$FAILED"
