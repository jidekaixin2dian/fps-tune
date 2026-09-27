# UIA 验证脚本（P3-2 固化版）——Agent 每轮改完界面后的标准验证工具。
#
# 用法一（整流程）：启动 → 切到第 N 个主导航页签 → 断言文本出现/不出现 → 截图 → 核对 error.log
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\uia-verify.ps1 `
#     -NavIndex 3 -ExpectText '驱动内手动设置清单,从文件应用…' -AbsentText '备份文件' `
#     -LatencyTarget 'DLSS 超分模型覆盖（仅 NVIDIA RTX）' -Shot C:\temp\display.png
#
# 用法二（当函数库点源，自己编排复杂流程：弹窗 / 预热计时 / 逐签断言）：
#   . .\tools\uia-verify.ps1   # 只加载函数，不执行主流程
#   $p = Start-App; $win = Wait-MainWindow $p; $tabs = Get-NavTabs $win
#   Select-Nav $tabs[2]; Invoke-Button $win '我的配置方案'; ...
#
# 实测坑（详见 docs/dev/AI-WORKFLOW.md §四）：
#  - ShowInTaskbar=False 的工具窗在 UIA Root 子查询里不可见 → 用 Find-ToolWindow（EnumWindows）；
#  - AppDialogWindow 的窗口 Title 恒为应用名，小确认框用高度 < 500 区分；
#  - 物理鼠标点击不可靠 → 一律控件模式（Invoke/SelectionItem/Value pattern）；
#  - 本脚本必须带 UTF-8 BOM（中文断言），仓库内已保证；改完请确认 BOM 还在。
#
# 输出：逐行 KEY: VALUE 便于 Agent 解析；最后一行 RESULT: PASS / FAIL；退出码 0/1。
param(
    [string]$ExePath = "$PSScriptRoot\..\FpsTune.Wpf\bin\Release\net10.0-windows\FpsTune.exe",
    [int]$NavIndex = -1,              # 主导航页签序号：0 概览 / 1 检测 / 2 优化 / 3 显示与画质 / 4 性能会话 / 5 A·B / 6 备份日志 / 7 设置；-1 不切页
    [string]$ExpectText = "",         # 切页后必须出现的文本，ASCII 逗号分隔多个（子串匹配，自动加通配；中文全角逗号不影响）
    [string]$AbsentText = "",         # 切页后必须不出现的文本(子串匹配，自动加通配)
    [string]$LatencyTarget = '',      # 可选：点击页签后等待出现的元素名，输出点击→可见毫秒数
    [string]$Shot = "",               # 可选：截图保存路径
    [switch]$DumpTexts,               # 调试：打印采集到的全部元素名
    [int]$StartupWaitSec = 4,         # 启动后等待秒数（含空闲预热窗口）
    [switch]$WatchSplash,             # 观察启动画面时序（逐 100ms 打印进程窗口）
    [switch]$KeepAlive,               # 验证后不杀进程（给用户目检用）
    [switch]$KillExisting             # 启动前先停掉同名进程
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class UiaNative {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    // 进程内全部可见顶层窗口："hwnd|pid|标题"（工具窗只在这里可见）
    public static List<string> AllWindows() {
        var list = new List<string>();
        EnumWindows((h, lp) => {
            if (!IsWindowVisible(h)) return true;
            uint p; GetWindowThreadProcessId(h, out p);
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length > 0) list.Add(h.ToInt64() + "|" + p + "|" + sb);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static IntPtr FindByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, lp) => {
            if (!IsWindowVisible(h)) return true;
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$script:RootEl = [System.Windows.Automation.AutomationElement]::RootElement

# ---------- 函数（可点源复用） ----------

function Start-App {
    param([string]$Path = $ExePath)
    return [System.Diagnostics.Process]::Start((Resolve-Path $Path).Path)
}

function Wait-MainWindow {
    param($Process, [int]$TimeoutSec = 30)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $deadline = [DateTime]::Now.AddSeconds($TimeoutSec)
    while ([DateTime]::Now -lt $deadline) {
        $win = $script:RootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($win) {
            # 主窗判定要过 splash（300 DIP 高、460 DIP 宽）：宽 <1200 视为启动画面继续等；
            # splash 关闭瞬间读属性会抛陈旧元素异常，捕获后继续轮询。
            try {
                $r = $win.Current.BoundingRectangle
                if ($r.Width -ge 1200 -and $r.Height -ge 500) { return $win }
            }
            catch { Start-Sleep -Milliseconds 150 }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-NavTabs {
    param($MainWindow)
    $all = $MainWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $rt = $MainWindow.Current.BoundingRectangle
    $tabs = @()
    foreach ($el in $all) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::RadioButton) {
            $b = $el.Current.BoundingRectangle
            if ($b.Height -gt 0 -and $b.Y -ge $rt.Y + 45 -and $b.Y -le $rt.Y + 100) { $tabs += $el }
        }
    }
    return @($tabs | Sort-Object { $_.Current.BoundingRectangle.X })
}

function Select-Nav {
    param($Tab)
    $Tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Invoke-Button {
    param($Scope, [string]$Name)
    $b = $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    if (-not $b) { return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

function Find-ToolWindow {
    param($Process, [string]$Title)
    $h = [UiaNative]::FindByTitle($Process.Id, $Title)
    if ($h -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($h)
}

function Texts-Of {
    param($Element)
    $t = @()
    foreach ($c in $Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($c.Current.Name) { $t += $c.Current.Name }
    }
    return ,$t
}

function Wait-Element {
    param($Scope, [string]$Name, [int]$TimeoutMs = 5000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ($Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)) {
            return $sw.ElapsedMilliseconds
        }
        Start-Sleep -Milliseconds 10
    }
    return -1
}

function Save-Shot {
    param([string]$Path)
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# ---------- 主流程（点源调用时跳过） ----------

if ($MyInvocation.InvocationName -ne '.') {
    $fail = @()
    $errlog = Join-Path $env:LOCALAPPDATA 'FpsTune\logs\error.log'
    $before = if (Test-Path $errlog) { (Get-Item $errlog).Length } else { -1 }
    if (-not (Test-Path $ExePath)) { Write-Output "RESULT: FAIL (exe 不存在: $ExePath)"; exit 1 }

    if ($KillExisting) {
        Get-Process FpsTune -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    $p = Start-App -Path $ExePath
    try {
        if ($WatchSplash) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
            while ($sw.ElapsedMilliseconds -lt ($StartupWaitSec * 1000)) {
                try {
                    $wins = $script:RootEl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
                    $desc = @()
                    foreach ($w in $wins) {
                        $r = $w.Current.BoundingRectangle
                        $desc += ('{0}({1}x{2})' -f $w.Current.Name, [int]$r.Width, [int]$r.Height)
                    }
                    Write-Output ("SPLASH_TIMELINE t={0,5}ms n={1} {2}" -f $sw.ElapsedMilliseconds, $wins.Count, ($desc -join ' | '))
                    if ($wins.Count -eq 1 -and $sw.ElapsedMilliseconds -gt 1500) { break }
                }
                catch { Write-Output ("SPLASH_TIMELINE t={0,5}ms 枚举失败（元素已消失，忽略）" -f $sw.ElapsedMilliseconds) }
                Start-Sleep -Milliseconds 100
            }
        }
        else { Start-Sleep -Seconds $StartupWaitSec }

        $win = Wait-MainWindow $p
        if (-not $win) { Write-Output 'RESULT: FAIL (主窗未出现)'; exit 1 }

        if ($NavIndex -ge 0) {
            $tabs = Get-NavTabs $win
            Write-Output ("NAV_TABS: " + $tabs.Count)
            if ($NavIndex -ge $tabs.Count) { Write-Output 'RESULT: FAIL (页签序号越界)'; exit 1 }
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Select-Nav $tabs[$NavIndex]
            if ($LatencyTarget) {
                Write-Output ("LATENCY_MS: " + (Wait-Element $win $LatencyTarget))
            }
            else { Start-Sleep -Seconds 2 }
        }

        # 断言轮询：切页后 UIA 树需要时间长全（高负载下可达数秒），重试到全部命中或超时
        $swAssert = [System.Diagnostics.Stopwatch]::StartNew()
        $expects = @($ExpectText -split "," | Where-Object { $_ })
        $missing = @($expects)
        $joined = ""
        do {
            $texts = Texts-Of $win
            $joined = $texts -join "`n"
            $missing = @($expects | Where-Object { $joined -notlike "*$_*" })
            if ($missing.Count -gt 0) { Start-Sleep -Milliseconds 300 }
        } while ($missing.Count -gt 0 -and $swAssert.ElapsedMilliseconds -lt 6000)

        if ($DumpTexts) { $texts | ForEach-Object { Write-Output ("TXT: " + $_.Substring(0, [Math]::Min(60, $_.Length))) } }
        foreach ($expect in $expects) {
            $ok = -not ($missing -contains $expect)
            Write-Output ("EXPECT '$expect': " + $ok)
            if (-not $ok) { $fail += "缺少：$expect" }
        }
        if ($AbsentText) {
            $ok = -not ($joined -like "*$AbsentText*")
            Write-Output ("ABSENT '$AbsentText': " + $ok)
            if (-not $ok) { $fail += "不应出现：$AbsentText" }
        }
        Write-Output ("MISSING_KEYS: " + ($joined -like '*!Str.*'))
        if ($joined -like '*!Str.*') { $fail += '存在 !Str.* 缺键' }

        if ($Shot) { Save-Shot $Shot; Write-Output ("SHOT: " + $Shot) }

        $after = if (Test-Path $errlog) { (Get-Item $errlog).Length } else { -1 }
        Write-Output ("ERRLOG_BYTES: before=$before after=$after")
        if ($after -ne $before) { $fail += "error.log 变化了（$before → $after），有新异常" }
    }
    catch {
        # 高负载桌面下 UIA 元素可能中途消失（陈旧元素异常）——如实报 FAIL 而不是无声死掉
        $fail += "脚本异常：" + $_.Exception.Message
    }
    finally {
        if (-not $KeepAlive -and $p -and -not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($fail.Count -eq 0) { Write-Output 'RESULT: PASS'; exit 0 }
    Write-Output ("RESULT: FAIL — " + ($fail -join '；'))
    exit 1
}
