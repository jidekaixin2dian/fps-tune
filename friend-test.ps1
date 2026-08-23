[CmdletBinding()]
param(
    [string]$Name = '',
    [string]$Scene = '',
    [string]$OutDir = '',
    [double]$BeforeAvgFps = 0,
    [double]$BeforeP1Low = 0,
    [double]$AfterAvgFps = 0,
    [double]$AfterP1Low = 0,
    [string]$Notes = '',
    [switch]$NoPrompt
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# 读取本机配置（只读，不修改任何设置）
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$gpu = (Get-CimInstance Win32_VideoController | Select-Object -First 1).Name
$ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$os = (Get-CimInstance Win32_OperatingSystem).Caption

function Read-Required {
    param([string]$Prompt, [string]$Default)
    if ($Default) { return $Default }
    if ($NoPrompt) { return '未填写' }
    $v = Read-Host $Prompt
    while (-not $v) {
        $v = Read-Host ($Prompt + '(必填)')
    }
    return $v
}

function Read-OptionalNumber {
    param([string]$Prompt, [double]$Default)
    if ($Default -ne 0) { return $Default }
    if ($NoPrompt) { return $null }
    $raw = Read-Host ($Prompt + '（没有可留空）')
    if (-not $raw) { return $null }
    $n = 0.0
    while (-not [double]::TryParse([string]$raw, [ref]$n)) {
        $raw = Read-Host ($Prompt + '（请输入数字，没有可留空）')
        if (-not $raw) { return $null }
    }
    return $n
}

if (-not $Name) { $Name = $env:USERNAME }
if (-not $Scene) { $Scene = Read-Required '场景/画质/设置（例：靶场，2K 全高，DLSS 质量）' $Scene }
$beforeAvg = Read-OptionalNumber '优化前平均 FPS' $BeforeAvgFps
$beforeP1  = Read-OptionalNumber '优化前 1% low' $BeforeP1Low
$afterAvg  = Read-OptionalNumber '优化后平均 FPS' $AfterAvgFps
$afterP1   = Read-OptionalNumber '优化后 1% low' $AfterP1Low
if (-not $Notes) {
    if ($NoPrompt) { $Notes = '' } else { $Notes = Read-Host '备注（体感、是否还原等，可留空）' }
}

# 输出目录：优先用 -OutDir，其次桌面，最后当前目录
if (-not $OutDir) {
    $OutDir = [Environment]::GetFolderPath('Desktop')
    if (-not $OutDir -or -not (Test-Path $OutDir)) { $OutDir = Get-Location }
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$mdPath = Join-Path $OutDir "delta-friend-test-$stamp.md"
$csvPath = Join-Path $OutDir "delta-friend-test-$stamp.csv"

$enc = New-Object System.Text.UTF8Encoding($true)

# Markdown
$md = @()
$md += '# 三角洲行动 · 朋友测试记录'
$md += ''
$md += '- 测试人: ' + $Name
$md += '- 机器: ' + $cpu + ' / ' + $gpu + ' / ' + $ramGB + ' GB / ' + $os
$md += '- 场景/画质: ' + $Scene
$md += '- 记录时间: ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$md += ''
$md += '| 项目 | 数值 |'
$md += '|---|---|'
$md += '| 优化前平均 FPS | ' + $(if ($null -eq $beforeAvg) { '—' } else { $beforeAvg }) + ' |'
$md += '| 优化前 1% low | ' + $(if ($null -eq $beforeP1) { '—' } else { $beforeP1 }) + ' |'
$md += '| 优化后平均 FPS | ' + $(if ($null -eq $afterAvg) { '—' } else { $afterAvg }) + ' |'
$md += '| 优化后 1% low | ' + $(if ($null -eq $afterP1) { '—' } else { $afterP1 }) + ' |'
$md += '| 备注 | ' + $Notes + ' |'
$md += ''
$md += '> 回传这个 .md 文件或把表格内容发给项目方即可。'
[System.IO.File]::WriteAllText($mdPath, ($md -join "`r`n"), $enc)

# CSV（UTF-8 BOM，Excel 可直接打开）
$csv = @(
    'name,scene,avg_fps_before,p1low_before,avg_fps_after,p1low_after,notes',
    ($Name + ',' + $Scene + ',' + $(if ($null -eq $beforeAvg) { '' } else { $beforeAvg }) + ',' + $(if ($null -eq $beforeP1) { '' } else { $beforeP1 }) + ',' + $(if ($null -eq $afterAvg) { '' } else { $afterAvg }) + ',' + $(if ($null -eq $afterP1) { '' } else { $afterP1 }) + ',"' + ($Notes -replace '"', '""') + '"')
)
[System.IO.File]::WriteAllText($csvPath, ($csv -join "`r`n"), $enc)

Write-Output '测试记录已生成:'
Write-Output ('  Markdown: ' + $mdPath)
Write-Output ('  CSV:      ' + $csvPath)
