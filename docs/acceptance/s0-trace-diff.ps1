param(
    [Parameter(Mandatory = $true)][string]$Baseline,
    [Parameter(Mandatory = $true)][string]$Candidate,
    # 差异行前后各带几行上下文。定位"哪一类图元开始错位"通常看两三行状态切换就够，
    # 但同帧内两个桶挨得近时，六行才能看出是不是整段被换了顺序。
    [int]$Context = 6
)

# 绘制序列 trace 的 A/B 判据（S0 基线的一部分）。
#
# 判据是"逐行相同"，不是"差异率低于某个阈值"。截图证明的是画面没变，而绘制对象族重构
# 每一步改的都是产出这条序列的代码；两帧截图一模一样但状态切换顺序变了，说明结果对、
# 过程错，下一次改动就会把错误暴露成画面差异。
#
# 因此这里不做任何归一化：行号、顺序、每个字段都是判据的一部分。唯一放宽的是表头的
# openedAt —— 它记录第一行落在第几帧，窗口内取值合法但不必相等，单独报出来供人判读。

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Trace([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少 trace 文件：$path" }
    $lines = [System.IO.File]::ReadAllLines($path)
    if ($lines.Length -eq 0) { throw "trace 是空文件：$path" }

    $header = $null
    $body = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        if ($null -eq $header -and $line.StartsWith('#')) { $header = $line; continue }
        if ($line.Length -eq 0) { continue }
        $body.Add($line)
    }

    return @{ Path = $path; Header = $header; Body = $body }
}

function Get-HeaderField([string]$header, [string]$name) {
    if ($null -eq $header) { return '' }
    $match = [regex]::Match($header, "$name=(\S+)")
    return $(if ($match.Success) { $match.Groups[1].Value } else { '' })
}

# 事件种类与绘制阶段的分布。逐行比对只回答"一样不一样"，这一节回答"少掉的是哪一类"——
# 一条 shadow 行消失和一条 replay 行消失，指向的代码位置完全不同。
function Get-Breakdown($trace) {
    $kinds = @{}
    $phases = @{}

    foreach ($line in $trace.Body) {
        $parts = $line.Split("`t")
        $kind = $parts[0]
        if (-not $kinds.ContainsKey($kind)) { $kinds[$kind] = 0 }
        $kinds[$kind]++

        if ($kind -eq 'D' -and $parts.Length -ge 3) {
            $phase = $parts[2]
            if (-not $phases.ContainsKey($phase)) { $phases[$phase] = 0 }
            $phases[$phase]++
        }
    }

    return @{ Kinds = $kinds; Phases = $phases }
}

$a = Read-Trace $Baseline
$b = Read-Trace $Candidate

Write-Output ("baseline : {0}" -f $a.Path)
Write-Output ("candidate: {0}" -f $b.Path)
Write-Output ''

$atA = Get-HeaderField $a.Header 'at'
$atB = Get-HeaderField $b.Header 'at'
$frA = Get-HeaderField $a.Header 'frames'
$frB = Get-HeaderField $b.Header 'frames'
$opA = Get-HeaderField $a.Header 'openedAt'
$opB = Get-HeaderField $b.Header 'openedAt'

Write-Output ("窗口   at={0} frames={1}  vs  at={2} frames={3}  {4}" -f $atA, $frA, $atB, $frB,
    $(if ($atA -eq $atB -and $frA -eq $frB) { '一致' } else { '不一致（两次运行不是同一帧窗口，比对无意义）' }))
Write-Output ("openedAt {0} vs {1}（窗口内取值合法，仅供参考，不参与判据）" -f $opA, $opB)
Write-Output ("行数   {0} vs {1}" -f $a.Body.Count, $b.Body.Count)
Write-Output ''

$ba = Get-Breakdown $a
$bb = Get-Breakdown $b

$allKinds = @($ba.Kinds.Keys) + @($bb.Kinds.Keys) | Sort-Object -Unique
$allPhases = @($ba.Phases.Keys) + @($bb.Phases.Keys) | Sort-Object -Unique

$kindLabel = @{ G = '分组'; S = '状态'; C = '剔除'; D = '绘制'; F = '帧头' }

Write-Output '事件分布'
foreach ($kind in $allKinds) {
    $ka = $(if ($ba.Kinds.ContainsKey($kind)) { $ba.Kinds[$kind] } else { 0 })
    $kb = $(if ($bb.Kinds.ContainsKey($kind)) { $bb.Kinds[$kind] } else { 0 })
    $label = $(if ($kindLabel.ContainsKey($kind)) { $kindLabel[$kind] } else { '未知' })
    Write-Output ("  {0} {1,-6} {2,8} {3,8}  {4}" -f $kind, $label, $ka, $kb,
        $(if ($ka -eq $kb) { '=' } else { '≠' }))
}

Write-Output '绘制阶段分布'
foreach ($phase in $allPhases) {
    $pa = $(if ($ba.Phases.ContainsKey($phase)) { $ba.Phases[$phase] } else { 0 })
    $pb = $(if ($bb.Phases.ContainsKey($phase)) { $bb.Phases[$phase] } else { 0 })
    Write-Output ("  {0,-10} {1,8} {2,8}  {3}" -f $phase, $pa, $pb, $(if ($pa -eq $pb) { '=' } else { '≠' }))
}
Write-Output ''

# 第一处差异及其上下文。只报"有 N 行不同"没有用：重构引入的错位几乎总是某一段整体平移，
# 看第一处差异的上下文就能判断是哪个桶的分支被换掉了。
$limit = [Math]::Min($a.Body.Count, $b.Body.Count)
$firstDiff = -1

for ($i = 0; $i -lt $limit; $i++) {
    if (-not [string]::Equals($a.Body[$i], $b.Body[$i], [StringComparison]::Ordinal)) { $firstDiff = $i; break }
}

if ($firstDiff -lt 0 -and $a.Body.Count -eq $b.Body.Count) {
    Write-Output '判定：逐行相同。'
    exit 0
}

if ($firstDiff -lt 0) {
    $firstDiff = $limit
    Write-Output ("判定：前 {0} 行相同，之后行数不同（baseline {1} / candidate {2}）。" -f $limit, $a.Body.Count, $b.Body.Count)
}
else {
    $diffCount = 0
    for ($i = 0; $i -lt $limit; $i++) {
        if (-not [string]::Equals($a.Body[$i], $b.Body[$i], [StringComparison]::Ordinal)) { $diffCount++ }
    }
    Write-Output ("判定：至少 {0} 行不同，首处位于第 {1} 行。" -f $diffCount, ($firstDiff + 1))
}

Write-Output ''
$from = [Math]::Max(0, $firstDiff - $Context)
$to = [Math]::Min($limit - 1, $firstDiff + $Context)

Write-Output ("上下文 {0}..{1}（- 基线 / + 候选）" -f ($from + 1), ($to + 1))
for ($i = $from; $i -le $to; $i++) {
    $mark = $(if ($i -eq $firstDiff) { '>' } else { ' ' })
    Write-Output ("{0} {1,7} - {2}" -f $mark, ($i + 1), $a.Body[$i])
    Write-Output ("{0} {1,7} + {2}" -f $mark, ($i + 1), $b.Body[$i])
}

exit 1
