# tuning-experiment.ps1
# 三角洲行动 · A/B 自动调优实验（原创实现，Clean-room）
# 作者：独立编写 · 许可证：MIT
#
# 思路：在同一台设备、固定场景（同一地图/画质/分辨率/路线）下，先采集 3 次基线，
# 再依次测试低风险候选组，用平均帧率 / 1% low / P99 帧时间 / 卡顿次数做规则决策，
# 有收益保留、无收益自动还原。样本不足、游戏退出、环境变化或基线不稳定时不形成结论。
#
# 采样器：优先自动调用官方 PresentMon（v2 语法），探测不到或调用失败时，
# 可用 -CsvPath 让用户自己用 PresentMon / FrameView 生成 CSV 后手动解析。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Baseline [-Duration 90] [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Test -Group group-1 [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Report [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Simulate [-Group group-2] [-Json]   # dry-run 测试
#
# 候选组（全部低风险、无需重启、可完整回滚）：
#   group-1 调度组：mmcss-games + sys-responsiveness + prio-separation
#   group-2 后台组：net-throttling-off + wer-off + dvr-off
#   group-3 电源组：power-ultimate

[CmdletBinding()]
param(
    [switch]$Baseline,
    [switch]$Test,
    [switch]$Report,
    [switch]$Simulate,
    [switch]$Json,
    [int]$Duration = 90,
    [string]$Group = '',
    [string]$PresentMon = '',
    [string]$CsvPath = '',
    [string]$GameName = 'DeltaForceClient-Win64-Shipping'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ToolName    = 'delta-optimizer'
$ToolVersion = '0.2.0'
$EnginePath  = Join-Path $PSScriptRoot 'delta-optimizer.ps1'
$StateDir    = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\experiment'
$StateFile   = Join-Path $StateDir 'state.json'

# 候选组定义
$CandidateGroups = @(
    @{ id = 'group-1'; name = '调度组'; items = @('mmcss-games', 'sys-responsiveness', 'prio-separation') }
    @{ id = 'group-2'; name = '后台组'; items = @('net-throttling-off', 'wer-off', 'dvr-off') }
    @{ id = 'group-3'; name = '电源组'; items = @('power-ultimate') }
)

# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------

function Get-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    # Start-Process + 临时文件重定向：比 ProcessStartInfo 重定向管道更稳，
    # 避免 GUI 子系统程序（如 PresentMon_x64.exe）在 ReadToEnd() 时卡死。
    $tmpOut = [System.IO.Path]::GetTempFileName()
    $tmpErr = [System.IO.Path]::GetTempFileName()
    try {
        $argString = ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '`"') + '"' } else { $_ }
        }) -join ' '
        $proc = Start-Process -FilePath $Exe -ArgumentList $argString -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
        $out = @(Get-Content $tmpOut -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -ne '' })
        $err = @(Get-Content $tmpErr -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -ne '' })
        return @{ code = $proc.ExitCode; output = $out; error = $err }
    } catch {
        return @{ code = -1; output = @(); error = @($_.Exception.Message) }
    } finally {
        Remove-Item $tmpOut, $tmpErr -Force -ErrorAction SilentlyContinue
    }
}

function Write-AtomicJson {
    param([string]$Path, $Object)
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Path.tmp"
    $json = $Object | ConvertTo-Json -Depth 12
    # PS 5.1 会把空数组值序列化成 {}，这里定向还原为 []
    $json = $json -replace '"groups":\s*\{\s*\}', '"groups": []'
    $json | Out-File -FilePath $tmp -Encoding UTF8
    if (Test-Path $Path) { Remove-Item $Path -Force }
    Move-Item $tmp $Path -Force
}

function Read-State {
    if (Test-Path $StateFile) {
        try {
            $s = Get-Content $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
            # 规范化 groups 为数组（兼容旧文件里的 null / 空对象 / 单元素标量）
            $g = $s.groups
            if ($null -eq $g -or ($g -is [System.Management.Automation.PSCustomObject] -and @($g.PSObject.Properties).Count -eq 0)) {
                $s.groups = @()
            } elseif (-not ($g -is [System.Array])) {
                $s.groups = @($g)
            }
            return $s
        } catch { }
    }
    return $null
}

# ---------------------------------------------------------------------------
# PresentMon 探测与采样
# ---------------------------------------------------------------------------

function Find-PresentMon {
    if ($PresentMon -and (Test-Path $PresentMon)) { return $PresentMon }
    $c = Get-Command PresentMon -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $candidates = @(
        'C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe',
        'C:\Program Files (x86)\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe'
    )
    foreach ($cand in $candidates) { if (Test-Path $cand) { return $cand } }
    return $null
}

function Get-GameProcess {
    $p = Get-Process -Name $GameName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { return $p }
    # 去掉 -Shipping 后缀再试一次
    $short = $GameName -replace '-Win64-Shipping$', ''
    $p2 = Get-Process -Name $short -ErrorAction SilentlyContinue | Select-Object -First 1
    return $p2
}

# 自动调用 PresentMon 采集一次，返回 CSV 路径；失败返回 $null
function Invoke-PresentMonAuto {
    param([int]$Seconds, [string]$CsvOut)
    $pm = Find-PresentMon
    if (-not $pm) { return $null }
    $game = Get-GameProcess
    if (-not $game) { return $null }

    # 先试官方 v2 参数（--timed / --session_name / --terminate_after_timed），
    # 优先用 --output_stdout（由脚本落盘，避免部分版本 --output_file 不落盘的问题），
    # 再回退到 --output_file 与 v1 参数（--duration）。使用独立 session 名，避免与
    # NVIDIA FrameView 服务已启动的默认 "PresentMon" 会话冲突。
    $sessionName = 'DeltaOptimizer'
    $attempts = @(
        @('--session_name', $sessionName, '--process_name', $GameName, '--timed', "$Seconds", '--terminate_after_timed', '--no_console_stats', '--output_stdout'),
        @('--session_name', $sessionName, '--process_id', "$($game.Id)", '--timed', "$Seconds", '--terminate_after_timed', '--no_console_stats', '--output_stdout'),
        @('--session_name', $sessionName, '--process_name', $GameName, '--timed', "$Seconds", '--terminate_after_timed', '--no_console_stats', '--output_file', $CsvOut),
        @('--session_name', $sessionName, '--process_id', "$($game.Id)", '--timed', "$Seconds", '--terminate_after_timed', '--no_console_stats', '--output_file', $CsvOut),
        @('--process-name', $GameName, '--duration', "$Seconds", '--output-file', $CsvOut),
        @('--process', "$($game.Id)", '--duration', "$Seconds", '--output_file', $CsvOut)
    )
    foreach ($args in $attempts) {
        if (Test-Path $CsvOut) { Remove-Item $CsvOut -Force }
        $r = Invoke-Native $pm $args
        if ($r.code -eq 0) {
            if ($args -contains '--output_stdout') {
                if ($r.output.Count -gt 0) {
                    $r.output | Set-Content -Path $CsvOut -Encoding UTF8
                    if ((Test-Path $CsvOut) -and ((Get-Item $CsvOut).Length -gt 0)) { return $CsvOut }
                }
            } elseif (Test-Path $CsvOut) {
                return $CsvOut
            }
        }
    }
    return $null
}

# 解析 PresentMon CSV → 帧统计。兼容 v1/v2 列名
function Parse-PresentMonCsv {
    param([string]$CsvPath)
    if (-not (Test-Path $CsvPath)) { return $null }
    try {
        $rows = Import-Csv $CsvPath
    } catch { return $null }
    if (-not $rows -or $rows.Count -eq 0) { return $null }

    $header = @($rows[0].PSObject.Properties.Name)
    $col = $null
    foreach ($candidate in @('msBetweenPresents', 'frame_time', 'FPS', 'fps')) {
        if ($header -contains $candidate) { $col = $candidate; break }
    }
    if (-not $col) { return @{ error = "无法识别 CSV 列名，找到的表头: $($header -join ', ')" } }

    $frameTimes = @()
    foreach ($row in $rows) {
        $v = $row.$col
        if ($null -ne $v -and $v -ne '') {
            $n = 0.0
            if ([double]::TryParse([string]$v, [ref]$n) -and $n -gt 0) { $frameTimes += $n }
        }
    }
    if ($frameTimes.Count -lt 30) { return @{ error = "有效帧样本不足（$($frameTimes.Count)）" } }

    $sorted = @($frameTimes | Sort-Object)
    $p99Index = [math]::Min([int][math]::Floor($sorted.Count * 0.99), $sorted.Count - 1)
    $p99ms = $sorted[$p99Index]
    $avgMs = ($frameTimes | Measure-Object -Average).Average
    $avgFps = 1000.0 / $avgMs
    $p1Fps = 1000.0 / $p99ms
    $stutters = @($sorted | Where-Object { $_ -gt 50 }).Count

    return @{
        samples = $frameTimes.Count
        avgFps = [math]::Round($avgFps, 2)
        p1Low = [math]::Round($p1Fps, 2)
        p99Ms = [math]::Round($p99ms, 2)
        stutters = $stutters
    }
}

# 采集 N 次采样，返回统计（自动模式或 -CsvPath 手动模式）
function Collect-Samples {
    param([int]$Count, [int]$Seconds)
    $results = @()
    $mode = 'auto'
    if ($CsvPath) { $mode = 'manual' }
    for ($i = 1; $i -le $Count; $i++) {
        if ($mode -eq 'manual') {
            $csv = $CsvPath
        } else {
            $csv = Join-Path $StateDir "sample-$i.csv"
            $auto = Invoke-PresentMonAuto $Seconds $csv
            if (-not $auto) {
                return @{ ok = $false; error = 'PresentMon 自动采样失败（未找到 PresentMon 或游戏未运行）。请安装官方 PresentMon（winget install Intel.PresentMon.Console）后用 -CsvPath 手动提供采样 CSV，或用 -Simulate 试跑。' }
            }
        }
        $stat = Parse-PresentMonCsv $csv
        if ($null -eq $stat) {
            return @{ ok = $false; error = "第 $i 次采样解析失败: 无法解析 CSV" }
        }
        if ($stat.ContainsKey('error')) {
            return @{ ok = $false; error = "第 $i 次采样解析失败: $($stat.error)" }
        }
        $results += $stat
    }
    return @{ ok = $true; mode = $mode; samples = $results }
}

# ---------------------------------------------------------------------------
# 统计与决策
# ---------------------------------------------------------------------------

# 3 次样本 → 汇总 + 稳定性（变异系数）
function Summarize-Samples {
    param($Samples)
    $avgList = @($Samples | ForEach-Object { $_.avgFps })
    $p1List  = @($Samples | ForEach-Object { $_.p1Low })
    $p99List = @($Samples | ForEach-Object { $_.p99Ms })
    $stutList = @($Samples | ForEach-Object { [int]$_.stutters })

    $mean = ($avgList | Measure-Object -Average).Average
    $sd = 0.0
    if ($avgList.Count -gt 1) {
        $variance = (($avgList | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Average).Average
        $sd = [math]::Sqrt($variance)
    }
    $cv = if ($mean -gt 0) { $sd / $mean } else { 1.0 }
    return @{
        avgFps = [math]::Round($mean, 2)
        p1Low = [math]::Round(($p1List | Measure-Object -Average).Average, 2)
        p99Ms = [math]::Round(($p99List | Measure-Object -Average).Average, 2)
        stutters = ($stutList | Measure-Object -Sum).Sum
        cv = [math]::Round($cv, 4)
        stable = ($cv -le 0.05)
    }
}

# 规则决策：基线 vs 实验组
function Decide-Keep {
    param($Baseline, $Group)
    $dAvg = ($Group.avgFps - $Baseline.avgFps) / $Baseline.avgFps * 100.0
    $dP1  = ($Group.p1Low - $Baseline.p1Low) / $Baseline.p1Low * 100.0
    $dStut = $Group.stutters - $Baseline.stutters

    if ($dAvg -ge 2.0 -and $dP1 -ge -3.0) {
        return @{ keep = $true; reason = "平均帧率提升 $([math]::Round($dAvg,1))%，1% low 未明显下降（$([math]::Round($dP1,1))%）" }
    }
    if ($dP1 -ge 5.0 -and $dAvg -ge -3.0) {
        return @{ keep = $true; reason = "1% low 提升 $([math]::Round($dP1,1))%，平均帧率未明显下降（$([math]::Round($dAvg,1))%）" }
    }
    if ($dAvg -ge 2.0 -and $dP1 -ge 5.0) {
        return @{ keep = $true; reason = "平均帧率与 1% low 双提升（$([math]::Round($dAvg,1))% / $([math]::Round($dP1,1))%）" }
    }
    if ($dStut -le -10 -and $dAvg -ge -3.0) {
        return @{ keep = $true; reason = "卡顿次数显著减少（$($Baseline.stutters) → $($Group.stutters)）且帧率未明显下降" }
    }
    return @{ keep = $false; reason = "无明显收益（平均 $([math]::Round($dAvg,1))%、1% low $([math]::Round($dP1,1))%）" }
}

# ---------------------------------------------------------------------------
# 引擎子进程调用（应用/还原候选组）
# ---------------------------------------------------------------------------

function Invoke-EngineApply {
    param([string[]]$ItemIds)
    $r = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $EnginePath, '-Apply', '-Items', ($ItemIds -join ','), '-Force', '-Json')
    if ($r.code -ne 0) { return @{ ok = $false; error = ($r.output -join ' ') } }
    try { return @{ ok = $true; result = ($r.output -join ' ' | ConvertFrom-Json) } } catch { return @{ ok = $true; result = $null } }
}

function Invoke-EngineRestore {
    param([string[]]$ItemIds)
    $r = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $EnginePath, '-Restore', '-Items', ($ItemIds -join ','), '-Json')
    if ($r.code -ne 0) { return @{ ok = $false; error = ($r.output -join ' ') } }
    return @{ ok = $true }
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

function Invoke-Baseline {
    param([int]$Seconds)
    if ($Simulate) {
        # 模拟数据：均值 ~100 FPS、CV ~1%（稳定基线）
        $rand = New-Object System.Random(7)
        $simSamples = @()
        for ($i = 0; $i -lt 3; $i++) {
            $mean = 100 + ($rand.NextDouble() * 3 - 1.5)
            $simSamples += @{
                samples = 5400
                avgFps = [math]::Round($mean, 2)
                p1Low = [math]::Round($mean * 0.55 + ($rand.NextDouble() * 2 - 1), 2)
                p99Ms = [math]::Round(1000.0 / ($mean * 0.45) + ($rand.NextDouble() * 4 - 2), 2)
                stutters = [int](8 + $rand.NextDouble() * 8)
            }
        }
        $samples = @{ ok = $true; mode = 'simulated'; samples = $simSamples }
    } else {
        $samples = Collect-Samples 3 $Seconds
    }
    if (-not $samples.ok) { return @{ tool = $ToolName; version = $ToolVersion; mode = 'baseline'; ok = $false; error = $samples.error } }
    $summary = Summarize-Samples $samples.samples

    $state = Read-State
    $newState = @{
        schema = 'v1'; updatedAt = (Get-Date).ToString('o');
        baseline = @{ samples = $samples.samples; summary = $summary; mode = $samples.mode; durationSec = $Seconds };
        groups = if ($state -and $state.groups) { @($state.groups) } else { @() }
    }
    Write-AtomicJson $StateFile $newState

    $msg = "基线采集完成：平均帧率 $($summary.avgFps) FPS、1% low $($summary.p1Low)、P99 $($summary.p99Ms) ms、卡顿 $($summary.stutters) 次"
    if ($summary.stable) { $msg += '；稳定性达标（CV ' + $summary.cv + '），可以开始测试候选组。' }
    else { $msg += '；稳定性不足（CV ' + $summary.cv + ' > 0.05），请保持同一地图/画质/路线后重新采集基线。' }

    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'baseline'; ok = $true;
        samplerMode = $samples.mode; baseline = $newState.baseline; message = $msg
    }
}

function Invoke-TestGroup {
    param([string]$GroupId, [int]$Seconds)
    $group = $CandidateGroups | Where-Object { $_.id -eq $GroupId } | Select-Object -First 1
    if (-not $group) { return @{ tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $false; error = "未知候选组: $GroupId（可选 group-1 / group-2 / group-3）" } }

    $state = Read-State
    if (-not $state -or -not $state.baseline) {
        return @{ tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $false; error = '还没有基线数据，请先运行 -Baseline（同一地图/画质/路线采集 3 次）。' }
    }
    if (-not $state.baseline.summary.stable) {
        return @{ tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $false; error = '基线不稳定，重新采集基线后再测试。' }
    }

    # 1) 应用候选组（真实模式）
    if ($Simulate) {
        $applied = @{ ok = $true }
    } else {
        $applied = Invoke-EngineApply $group.items
    }
    if (-not $applied.ok) {
        return @{ tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $false; error = '应用候选组失败: ' + $applied.error }
    }

    # 2) 采样 3 次
    if ($Simulate) {
        # 模拟数据：group-1 提升、group-2 下降、group-3 微升
        $rand = New-Object System.Random(42)
        $baseMean = [double]$state.baseline.summary.avgFps
        $offset = if ($GroupId -eq 'group-1') { 6.0 } elseif ($GroupId -eq 'group-2') { -4.0 } else { 2.5 }
        $simSamples = @()
        for ($i = 0; $i -lt 3; $i++) {
            $mean = $baseMean + $offset + ($rand.NextDouble() * 2 - 1)
            $simSamples += @{
                samples = 5400; avgFps = [math]::Round($mean, 2)
                p1Low = [math]::Round($mean * 0.55 + ($rand.NextDouble() * 4 - 2), 2)
                p99Ms = [math]::Round(1000.0 / ($mean * 0.45) + ($rand.NextDouble() * 6 - 3), 2)
                stutters = [int]($rand.NextDouble() * 8)
            }
        }
        $samples = @{ ok = $true; mode = 'simulated'; samples = $simSamples }
    } else {
        $samples = Collect-Samples 3 $Seconds
    }
    if (-not $samples.ok) {
        if (-not $Simulate) { Invoke-EngineRestore $group.items | Out-Null }   # 采样失败，先还原现场
        return @{ tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $false; error = $samples.error }
    }
    $summary = Summarize-Samples $samples.samples

    # 3) 决策
    $decision = Decide-Keep $state.baseline.summary $summary
    $kept = $decision.keep

    # 4) 无效 → 自动还原
    $reverted = $false
    if (-not $kept -and -not $Simulate) {
        $rr = Invoke-EngineRestore $group.items
        if ($rr.ok) { $reverted = $true }
    }
    if (-not $kept -and $Simulate) { $reverted = $true }

    # 5) 更新状态
    # 注意：不能写成 $groups = if (...) { @(...) }——if 语句输出会被管道展开，
    # 单元素数组会塌缩成标量。必须用赋值语句形式。
    $groups = @()
    if ($state.groups) { $groups = @($state.groups) }
    $existing = @($groups | Where-Object { $_.id -eq $GroupId })
    if ($existing.Count -gt 0) {
        $groups = @($groups | Where-Object { $_.id -ne $GroupId })
    }
    $groups += @{
        id = $group.id; name = $group.name; items = $group.items;
        appliedAt = (Get-Date).ToString('o'); summary = $summary;
        keep = $kept; reverted = $reverted; reason = $decision.reason; samplerMode = $samples.mode
    }
    $newState = @{ schema = 'v1'; updatedAt = (Get-Date).ToString('o'); baseline = $state.baseline; groups = $groups }
    Write-AtomicJson $StateFile $newState

    $verdict = if ($kept) { '保留' } else { '已还原' }
    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'test'; ok = $true;
        group = $group.id; groupName = $group.name; items = $group.items;
        baseline = $state.baseline.summary; groupSummary = $summary;
        keep = $kept; reverted = $reverted; reason = $decision.reason;
        samplerMode = $samples.mode;
        message = "$($group.name)（$($group.id)）测试完成：$verdict。$($decision.reason)"
    }
}

function Invoke-Report {
    $state = Read-State
    if (-not $state) {
        return @{ tool = $ToolName; version = $ToolVersion; mode = 'report'; ok = $false; error = '还没有实验数据。' }
    }
    $csvPath = Join-Path $StateDir 'experiment-summary.csv'
    $lines = @('group,keep,avgFps,p1Low,p99Ms,stutters,reason')
    $rows = @()
    if ($state.groups) {
        foreach ($g in @($state.groups)) {
            $lines += "$($g.id),$($g.keep),$($g.summary.avgFps),$($g.summary.p1Low),$($g.summary.p99Ms),$($g.summary.stutters),`"$($g.reason)`""
        }
    }
    $lines | Out-File -FilePath $csvPath -Encoding UTF8

    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'report'; ok = $true;
        baseline = if ($state.baseline) { $state.baseline.summary } else { $null };
        groups = if ($state.groups) { @($state.groups) } else { @() };
        stateFile = $StateFile; csvExport = $csvPath
    }
}

# ---------------------------------------------------------------------------
# 主入口
# ---------------------------------------------------------------------------

$actions = @()
if ($Baseline) { $actions += 'baseline' }
if ($Test) { $actions += 'test' }
if ($Report) { $actions += 'report' }

if ($actions.Count -ne 1) {
    Write-Host '用法: tuning-experiment.ps1 -Baseline | -Test -Group group-1|group-2|group-3 | -Report（加 -Json 输出 JSON）'
    Write-Host '可选: -Duration <秒>（单次采样时长，默认 90） -CsvPath <csv>（手动采样模式） -PresentMon <路径> -Simulate（dry-run）'
    exit 2
}

try {
    switch ($actions[0]) {
        'baseline' {
            $result = Invoke-Baseline $Duration
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else { Write-Host $result.message }
        }
        'test' {
            if (-not $Group) { throw '请用 -Group 指定候选组: group-1 / group-2 / group-3' }
            $result = Invoke-TestGroup $Group $Duration
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else { Write-Host $result.message }
        }
        'report' {
            $result = Invoke-Report
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else {
                if (-not $result.ok) { Write-Host $result.error; exit 1 }
                Write-Host ('基线: 平均 ' + $result.baseline.avgFps + ' FPS / 1% low ' + $result.baseline.p1Low + ' / 稳定度 CV ' + $result.baseline.cv)
                foreach ($g in @($result.groups)) {
                    Write-Host ("$($g.id) $($g.name): " + $(if ($g.keep) { '保留' } else { '已还原' }) + " | 平均 $($g.summary.avgFps) FPS | 1% low $($g.summary.p1Low) | $($g.reason)")
                }
                Write-Host ('CSV: ' + $result.csvExport)
            }
        }
    }
} catch {
    if ($Json) {
        @{ tool = $ToolName; version = $ToolVersion; mode = 'error'; error = $_.Exception.Message; stack = $_.ScriptStackTrace } | ConvertTo-Json -Depth 6
    } else {
        Write-Error $_.Exception.Message
    }
    exit 1
}
