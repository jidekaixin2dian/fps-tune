[CmdletBinding()]
param(
    [switch]$SmokeTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Engine = Join-Path $Root 'delta-optimizer.ps1'
$Tuning = Join-Path $Root 'tuning-experiment.ps1'
$FriendTest = Join-Path $Root 'friend-test.ps1'
$TempDir = Join-Path $env:TEMP 'delta-gui-tmp'
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

$script:DetectData = $null
$script:ItemIdList = @()

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $enc = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

function To-PsLiteral {
    param([string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

function Invoke-GuiScript {
    param(
        [string]$Name,
        [string]$TargetScript,
        [string[]]$Arguments
    )
    $wrapper = Join-Path $TempDir "$Name.ps1"
    $stdout = Join-Path $TempDir "$Name.out.txt"
    $stderr = Join-Path $TempDir "$Name.err.txt"

    $line = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`n& " + (To-PsLiteral $TargetScript)
    foreach ($arg in $Arguments) {
        if ($arg -match '^[A-Za-z0-9_./:,+\-]+$') {
            $line += ' ' + $arg
        } else {
            $line += ' ' + (To-PsLiteral $arg)
        }
    }
    Write-Utf8File $wrapper $line

    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$wrapper) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not $p.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
    $p.Refresh()
    $outText = ''
    $errText = ''
    if ((Test-Path $stdout) -and (Get-Item $stdout).Length -gt 0) { $outText = [System.IO.File]::ReadAllText($stdout) }
    if ((Test-Path $stderr) -and (Get-Item $stderr).Length -gt 0) { $errText = [System.IO.File]::ReadAllText($stderr) }
    return @{ exitCode = $p.ExitCode; output = $outText; error = $errText; stdout = $stdout; stderr = $stderr }
}

function Show-Text {
    param($TextBox, [string]$Text)
    $TextBox.Text = $Text
    $TextBox.SelectionStart = $TextBox.TextLength
    $TextBox.ScrollToCaret()
}

function Show-Result {
    param($TextBox, $Result, [string]$Title = '结果')
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("exit=$($Result.exitCode)")
    if ($Result.output) {
        [void]$sb.AppendLine('--- STDOUT ---')
        [void]$sb.AppendLine($Result.output)
    }
    if ($Result.error) {
        [void]$sb.AppendLine('--- STDERR ---')
        [void]$sb.AppendLine($Result.error)
    }
    Show-Text $TextBox $sb.ToString()
}

function ConvertFrom-JsonSafe {
    param([string]$Text)
    if (-not $Text) { return $null }
    try { return ($Text | ConvertFrom-Json) } catch { return $null }
}

# ---------------------------------------------------------------------------
# 主窗体
# ---------------------------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'delta-force-tune GUI (功能版)'
$form.Size = New-Object System.Drawing.Size(980, 700)
$form.MinimumSize = New-Object System.Drawing.Size(840, 560)
$form.StartPosition = 'CenterScreen'

$tab = New-Object System.Windows.Forms.TabControl
$tab.Dock = 'Fill'
$form.Controls.Add($tab)

function New-TabPage {
    param([string]$Text)
    $p = New-Object System.Windows.Forms.TabPage
    $p.Text = $Text
    $tab.Controls.Add($p)
    return $p
}

function New-Button {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width = 120, [int]$Height = 30)
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text
    $b.Location = New-Object System.Drawing.Point($X, $Y)
    $b.Size = New-Object System.Drawing.Size($Width, $Height)
    $Parent.Controls.Add($b)
    return $b
}

function New-Label {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width = 120)
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $Text
    $l.Location = New-Object System.Drawing.Point($X, $Y)
    $l.Size = New-Object System.Drawing.Size($Width, 22)
    $Parent.Controls.Add($l)
    return $l
}

function New-TextBox {
    param($Parent, [int]$X, [int]$Y, [int]$Width, [int]$Height, [bool]$Multi = $false)
    $t = New-Object System.Windows.Forms.TextBox
    $t.Location = New-Object System.Drawing.Point($X, $Y)
    $t.Size = New-Object System.Drawing.Size($Width, $Height)
    if ($Multi) {
        $t.Multiline = $true
        $t.ScrollBars = 'Vertical'
        $t.Font = New-Object System.Drawing.Font('Consolas', 9)
    }
    $Parent.Controls.Add($t)
    return $t
}

function New-TopPanel {
    param($Parent, [int]$Height = 44)
    $p = New-Object System.Windows.Forms.Panel
    $p.Dock = 'Top'
    $p.Height = $Height
    $Parent.Controls.Add($p)
    return $p
}

# =========================================================
# Tab 1: 检测
# =========================================================
$pageDetect = New-TabPage '检测'
$pnlDetect = New-TopPanel $pageDetect
$btnDetectRun = New-Button $pnlDetect '运行检测' 6 6 110 30
$btnDetectLoad = New-Button $pnlDetect '加载到优化页' 124 6 130 30
$txtDetect = New-TextBox $pageDetect 6 52 950 560 $true

$btnDetectRun.Add_Click({
    Show-Text $txtDetect '正在检测...'
    $r = Invoke-GuiScript 'detect' $Engine @('-Detect','-Json')
    if ($r.exitCode -ne 0) {
        Show-Result $txtDetect $r '检测失败'
        return
    }
    $data = ConvertFrom-JsonSafe $r.output
    if (-not $data) {
        Show-Result $txtDetect $r '检测结果解析失败'
        return
    }
    $script:DetectData = $data
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('== 硬件 ==')
    [void]$sb.AppendLine("CPU: $($data.hardware.cpu)")
    [void]$sb.AppendLine("GPU: $($data.hardware.gpu)")
    [void]$sb.AppendLine("内存: $($data.hardware.ramGB) GB")
    [void]$sb.AppendLine("系统: $($data.hardware.os)")
    [void]$sb.AppendLine("笔记本: $($data.hardware.isLaptop)  管理员: $($data.hardware.isAdmin)")
    [void]$sb.AppendLine("游戏路径: $($data.gamePath)")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('== 体检 ==')
    foreach ($c in @($data.checks)) {
        [void]$sb.AppendLine("[$($c.status)] $($c.name): $($c.message)")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("优化项共 $($data.items.Count) 项，详情见原始 JSON：")
    [void]$sb.AppendLine($r.output)
    Show-Text $txtDetect $sb.ToString()
})

$btnDetectLoad.Add_Click({
    if (-not $script:DetectData) {
        [System.Windows.Forms.MessageBox]::Show($form, '请先在检测页运行一次检测。', '提示')
        return
    }
    $script:ItemIdList = @()
    $clbItems.Items.Clear()
    foreach ($it in @($script:DetectData.items)) {
        [void]$clbItems.Items.Add("$($it.id)  -  $($it.description)")
        $script:ItemIdList += [string]$it.id
    }
    [System.Windows.Forms.MessageBox]::Show($form, "已加载 $($script:ItemIdList.Count) 个优化项到优化页。", '提示')
})

# =========================================================
# Tab 2: 优化
# =========================================================
$pageOpt = New-TabPage '优化'
$pnlOpt = New-TopPanel $pageOpt 100

$radioFull = New-Object System.Windows.Forms.RadioButton
$radioFull.Text = 'full'; $radioFull.Location = New-Object System.Drawing.Point(6, 6); $radioFull.Size = New-Object System.Drawing.Size(70, 22)
$pnlOpt.Controls.Add($radioFull)

$radioBalanced = New-Object System.Windows.Forms.RadioButton
$radioBalanced.Text = 'balanced'; $radioBalanced.Location = New-Object System.Drawing.Point(84, 6); $radioBalanced.Size = New-Object System.Drawing.Size(100, 22); $radioBalanced.Checked = $true
$pnlOpt.Controls.Add($radioBalanced)

$radioSafeOnly = New-Object System.Windows.Forms.RadioButton
$radioSafeOnly.Text = 'safe-only'; $radioSafeOnly.Location = New-Object System.Drawing.Point(194, 6); $radioSafeOnly.Size = New-Object System.Drawing.Size(100, 22)
$pnlOpt.Controls.Add($radioSafeOnly)

$radioCustom = New-Object System.Windows.Forms.RadioButton
$radioCustom.Text = '自定义勾选'; $radioCustom.Location = New-Object System.Drawing.Point(304, 6); $radioCustom.Size = New-Object System.Drawing.Size(120, 22)
$pnlOpt.Controls.Add($radioCustom)

$chkConsent = New-Object System.Windows.Forms.CheckBox
$chkConsent.Text = '我已阅读并同意执行上述优化项（写前自动备份，可还原）'
$chkConsent.Location = New-Object System.Drawing.Point(6, 36)
$chkConsent.Size = New-Object System.Drawing.Size(460, 22)
$pnlOpt.Controls.Add($chkConsent)

$btnApply = New-Button $pnlOpt '应用' 6 66 110 30
$btnRestore = New-Button $pnlOpt '还原全部' 124 66 110 30

$clbItems = New-Object System.Windows.Forms.CheckedListBox
$clbItems.Location = New-Object System.Drawing.Point(6, 108)
$clbItems.Size = New-Object System.Drawing.Size(430, 460)
$pageOpt.Controls.Add($clbItems)

$txtOpt = New-TextBox $pageOpt 450 108 510 460 $true

$btnApply.Add_Click({
    if (-not $chkConsent.Checked) {
        [System.Windows.Forms.MessageBox]::Show($form, '请先勾选同意说明，再执行应用。', '未确认')
        return
    }
    $args = @()
    if ($radioFull.Checked) { $args = @('-Apply','-Preset','full','-Force','-Json') }
    elseif ($radioBalanced.Checked) { $args = @('-Apply','-Preset','balanced','-Force','-Json') }
    elseif ($radioSafeOnly.Checked) { $args = @('-Apply','-Preset','safe-only','-Force','-Json') }
    else {
        $ids = @()
        foreach ($i in $clbItems.CheckedIndices) {
            if ($i -lt $script:ItemIdList.Count) { $ids += $script:ItemIdList[$i] }
        }
        if ($ids.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show($form, '请勾选至少一个优化项。', '提示')
            return
        }
        $args = @('-Apply','-Items',($ids -join ','),'-Force','-Json')
    }
    Show-Text $txtOpt '正在应用...'
    $r = Invoke-GuiScript 'apply' $Engine $args
    if ($r.exitCode -ne 0) {
        Show-Result $txtOpt $r '应用失败'
        return
    }
    $j = ConvertFrom-JsonSafe $r.output
    if ($j) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("summary: $($j.summary)")
        [void]$sb.AppendLine("backupFile: $($j.backupFile)")
        [void]$sb.AppendLine("reboot: $($j.reboot -join ', ')")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- 原始 JSON ---')
        [void]$sb.AppendLine($r.output)
        Show-Text $txtOpt $sb.ToString()
    } else {
        Show-Result $txtOpt $r '应用完成'
    }
})

$btnRestore.Add_Click({
    $ans = [System.Windows.Forms.MessageBox]::Show($form, '确定要还原全部已备份的项目吗？', '还原确认', 'YesNo', 'Warning')
    if ($ans -ne 'Yes') { return }
    Show-Text $txtOpt '正在还原...'
    $r = Invoke-GuiScript 'restore' $Engine @('-Restore','-Json')
    if ($r.exitCode -ne 0) {
        Show-Result $txtOpt $r '还原失败'
        return
    }
    $j = ConvertFrom-JsonSafe $r.output
    if ($j) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("summary: $($j.summary)")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- 原始 JSON ---')
        [void]$sb.AppendLine($r.output)
        Show-Text $txtOpt $sb.ToString()
    } else {
        Show-Result $txtOpt $r '还原完成'
    }
})

# =========================================================
# Tab 3: A/B 实验
# =========================================================
$pageAB = New-TabPage 'A/B 实验'
$pnlAB = New-TopPanel $pageAB
$btnBase = New-Button $pnlAB '1. 基线' 6 6 100 30
$btnG1 = New-Button $pnlAB '2. group-1' 114 6 110 30
$btnG2 = New-Button $pnlAB '3. group-2' 232 6 110 30
$btnG3 = New-Button $pnlAB '4. group-3' 350 6 110 30
$btnReport = New-Button $pnlAB '5. 报告' 468 6 110 30
$btnOpenExp = New-Button $pnlAB '打开实验目录' 586 6 130 30
$txtAB = New-TextBox $pageAB 6 52 950 560 $true

$btnBase.Add_Click({
    Show-Text $txtAB '基线采样中（3 次，每次约 90 秒），请保持游戏场景固定...'
    $r = Invoke-GuiScript 'ab_baseline' $Tuning @('-Baseline','-Json')
    Show-Result $txtAB $r '基线结果'
})
$btnG1.Add_Click({
    Show-Text $txtAB 'group-1 测试中（3 次采样），请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g1' $Tuning @('-Test','-Group','group-1','-Json')
    Show-Result $txtAB $r 'group-1 结果'
})
$btnG2.Add_Click({
    Show-Text $txtAB 'group-2 测试中（3 次采样），请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g2' $Tuning @('-Test','-Group','group-2','-Json')
    Show-Result $txtAB $r 'group-2 结果'
})
$btnG3.Add_Click({
    Show-Text $txtAB 'group-3 测试中（3 次采样），请保持场景固定...'
    $r = Invoke-GuiScript 'ab_g3' $Tuning @('-Test','-Group','group-3','-Json')
    Show-Result $txtAB $r 'group-3 结果'
})
$btnReport.Add_Click({
    Show-Text $txtAB '正在生成报告...'
    $r = Invoke-GuiScript 'ab_report' $Tuning @('-Report','-Json')
    Show-Result $txtAB $r '报告'
})
$btnOpenExp.Add_Click({
    $dir = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\experiment'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Start-Process explorer.exe $dir
})

# =========================================================
# Tab 4: 朋友测试
# =========================================================
$pageFriend = New-TabPage '朋友测试'
$txtFName = New-TextBox $pageFriend 120 10 280 26 $false
$txtFScene = New-TextBox $pageFriend 120 44 520 26 $false
$txtFBeforeAvg = New-TextBox $pageFriend 120 78 160 26 $false
$txtFBeforeP1 = New-TextBox $pageFriend 360 78 160 26 $false
$txtFAfterAvg = New-TextBox $pageFriend 120 112 160 26 $false
$txtFAfterP1 = New-TextBox $pageFriend 360 112 160 26 $false
$txtFNotes = New-TextBox $pageFriend 120 146 520 52 $true
[void](New-Label $pageFriend '昵称/测试人' 10 12 100)
[void](New-Label $pageFriend '场景/画质/设置' 10 46 100)
[void](New-Label $pageFriend '优化前平均 FPS' 10 80 110)
[void](New-Label $pageFriend '优化前 1% low' 290 80 100)
[void](New-Label $pageFriend '优化后平均 FPS' 10 114 110)
[void](New-Label $pageFriend '优化后 1% low' 290 114 100)
[void](New-Label $pageFriend '备注' 10 148 100)

$btnFriendGen = New-Button $pageFriend '生成记录表' 10 210 130 34
$btnFriendOpen = New-Button $pageFriend '打开输出目录' 150 210 130 34
$txtFriend = New-TextBox $pageFriend 10 252 940 360 $true

$btnFriendGen.Add_Click({
    if (-not $txtFScene.Text) {
        [System.Windows.Forms.MessageBox]::Show($form, '请填写场景/画质/设置。', '提示')
        return
    }
    $outDir = Join-Path $env:TEMP 'delta-friend-test-out'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $name = $txtFName.Text
    if (-not $name) { $name = $env:USERNAME }
    $args = @(
        '-Name', $name,
        '-Scene', $txtFScene.Text,
        '-BeforeAvgFps', (if ($txtFBeforeAvg.Text) { $txtFBeforeAvg.Text } else { '0' }),
        '-BeforeP1Low', (if ($txtFBeforeP1.Text) { $txtFBeforeP1.Text } else { '0' }),
        '-AfterAvgFps', (if ($txtFAfterAvg.Text) { $txtFAfterAvg.Text } else { '0' }),
        '-AfterP1Low', (if ($txtFAfterP1.Text) { $txtFAfterP1.Text } else { '0' }),
        '-Notes', $txtFNotes.Text,
        '-OutDir', $outDir,
        '-NoPrompt'
    )
    Show-Text $txtFriend '正在生成记录表...'
    $r = Invoke-GuiScript 'friend_test' $FriendTest $args
    if ($r.exitCode -ne 0) {
        Show-Result $txtFriend $r '生成失败'
        return
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('生成完成。输出目录: ' + $outDir)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('--- 脚本输出 ---')
    [void]$sb.AppendLine($r.output)
    $latest = Get-ChildItem $outDir -Filter '*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('--- ' + $latest.Name + ' ---')
        [void]$sb.AppendLine([System.IO.File]::ReadAllText($latest.FullName))
    }
    Show-Text $txtFriend $sb.ToString()
})

$btnFriendOpen.Add_Click({
    $outDir = Join-Path $env:TEMP 'delta-friend-test-out'
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    Start-Process explorer.exe $outDir
})

# =========================================================
# Tab 5: 备份 / 日志
# =========================================================
$pageBackup = New-TabPage '备份 / 日志'
$pnlBackup = New-TopPanel $pageBackup
$btnListBackup = New-Button $pnlBackup '列出可还原项' 6 6 130 30
$btnRestoreAll2 = New-Button $pnlBackup '还原全部' 144 6 110 30
$btnOpenBackup = New-Button $pnlBackup '打开备份目录' 262 6 130 30
$btnOpenGuiTmp = New-Button $pnlBackup '打开 GUI 临时目录' 400 6 140 30
$txtBackup = New-TextBox $pageBackup 6 52 950 560 $true

$btnListBackup.Add_Click({
    $r = Invoke-GuiScript 'list_restore' $Engine @('-ListRestoreItems','-Json')
    Show-Result $txtBackup $r '可还原项'
})

$btnRestoreAll2.Add_Click({
    $ans = [System.Windows.Forms.MessageBox]::Show($form, '确定要还原全部已备份的项目吗？', '还原确认', 'YesNo', 'Warning')
    if ($ans -ne 'Yes') { return }
    Show-Text $txtBackup '正在还原...'
    $r = Invoke-GuiScript 'restore2' $Engine @('-Restore','-Json')
    Show-Result $txtBackup $r '还原结果'
})

$btnOpenBackup.Add_Click({
    $dir = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\backup'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Start-Process explorer.exe $dir
})

$btnOpenGuiTmp.Add_Click({
    if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir -Force | Out-Null }
    Start-Process explorer.exe $TempDir
})

# ---------------------------------------------------------------------------
# 启动
# ---------------------------------------------------------------------------
if ($SmokeTest) {
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $form.Close()
    return
}

[System.Windows.Forms.Application]::Run($form)
