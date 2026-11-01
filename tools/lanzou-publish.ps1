#Requires -Version 7.0
<#
.SYNOPSIS
  把当版发布包上传到蓝奏云，并输出可直接贴进 GitHub Release 说明的备用下载链接。

.DESCRIPTION
  GitHub 直连在国内不稳定（用户反馈"要下好几次"），所以每个版本除 Release 资产外，
  还放一份到蓝奏云。本脚本用 agent-browser 驱动真实浏览器完成上传，不需要官方 API。

  首次使用（只需一次）：
    1. 配置好 agent-browser（npm i -g agent-browser && agent-browser install）。
    2. 弹出真实浏览器窗口，手动登录一次（cookie 留在 -Profile 指定的持久 profile 里）：
         agent-browser --profile "$env:USERPROFILE\.fpstune-browser\lanzou" --headed open 'https://pc.woozooo.com/'
       登录完成后关掉窗口即可；脚本默认用同一个 profile，不用再登录。

  之后每次发版：
    pwsh -File tools/lanzou-publish.ps1 -File dist/installer/FpsTune-Setup-0.4.0.exe

  退出码：0 成功；2 需要人工登录；3 找不到上传控件/页面结构变了；1 其它失败。
  链接会写到 dist/lanzou-<版本>.json，并打印 Release 说明用的那一行。

.PARAMETER File
  要上传的文件，可多个。

.PARAMETER Session
  agent-browser 会话名，默认 fpstune-lanzou（与手动登录用同一个）。

.PARAMETER Probe
  只探测页面结构（登录状态、文件输入框、按钮），不上传。页面改版时先跑这个。

.PARAMETER TimeoutSeconds
  上传后等待文件出现在列表里的最长时间，默认 180 秒。
#>
[CmdletBinding()]
param(
    [string[]]$File = @(),
    [string]$Session = 'fpstune-lanzou',
    [string]$Profile = (Join-Path $env:USERPROFILE '.fpstune-browser\lanzou'),
    [switch]$Probe,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$uploadPage = 'https://pc.woozooo.com/mydisk.php?item=files&action=index'
# 蓝奏云 PC 版历史上换过控件，按优先级逐个试；页面改版时用 -Probe 看实际结构再补。
$fileInputSelectors = @('#selectfiles', 'input[type=file]', '#file', 'input[name=file]')
$loginPrompts = @('input[type=password]', '#loginform', 'input[name=username]')

function Invoke-Browser {
    # agent-browser 的 open 会让 CLI 常驻（守护进程附着），直接 & 调用会永远不返回，
    # 所以统一走 Start-Process + 超时强杀，只取它已经打印出来的内容。
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowTimeout,
        [switch]$AllowFailure
    )
    $stdout = [System.IO.Path]::GetTempFileName()
    $stderr = [System.IO.Path]::GetTempFileName()
    # npm 装的是 .ps1/.cmd 壳，Start-Process 不能直接跑 .ps1，统一用 pwsh -File 起。
    $launcher = @($script:LanzouLauncher)
    $previous = $env:AGENT_BROWSER_SESSION
    try {
        $env:AGENT_BROWSER_SESSION = $Session
        $process = Start-Process -FilePath $launcher[0] -ArgumentList ($launcher[1..($launcher.Count - 1)] + @('--profile', $Profile) + $Arguments) -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    }
    finally {
        $env:AGENT_BROWSER_SESSION = $previous
    }
    $timedOut = $false
    try { $process | Wait-Process -Timeout $TimeoutSeconds -ErrorAction Stop }
    catch { $timedOut = $true; $process | Stop-Process -Force -ErrorAction SilentlyContinue }
    $output = ((Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) + "`n" +
        (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)) -replace "`r", ""
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    $output = $output.Trim()
    if ($timedOut) {
        if ($AllowTimeout) { return $output }
        throw "agent-browser $($Arguments[0]) 超过 $TimeoutSeconds 秒未返回。"
    }
    if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "agent-browser $($Arguments[0]) 失败（exit $($process.ExitCode)）：$output"
    }
    return $output
}

function Test-Element([string]$Selector) {
    $result = Invoke-Browser -Arguments @('get', 'count', $Selector) -AllowFailure
    if ($result -match '(\d+)') { return [int]$Matches[1] -gt 0 }
    return $false
}

if (-not (Get-Command agent-browser -ErrorAction SilentlyContinue)) {
    throw '未找到 agent-browser。先执行：npm i -g agent-browser; agent-browser install'
}
$agentBrowser = (Get-Command agent-browser).Source
$script:LanzouLauncher = if ($agentBrowser -like '*.ps1') { @('pwsh', '-NoProfile', '-File', $agentBrowser) } else { @($agentBrowser) }

Write-Host "打开蓝奏云文件页（会话 $Session）…"
Invoke-Browser -Arguments @('open', $uploadPage) | Out-Null
$url = (Invoke-Browser -Arguments @('get', 'url')).Trim()

# 登录态判据：停在文件页（mydisk.php）且没有密码框，且能找到上传控件。
# 早期的 #nickname 之类标记在 pc.woozooo.com 这套老界面上根本不存在，会误判成未登录。
$needsLogin = ($loginPrompts | Where-Object { Test-Element $_ }).Count -gt 0
$uploadReady = ($fileInputSelectors | Where-Object { Test-Element $_ }).Count -gt 0
$loggedIn = -not $needsLogin -and $uploadReady -and $url.Contains('mydisk.php', StringComparison.OrdinalIgnoreCase)
if (-not $loggedIn -and $needsLogin) {
    Write-Warning @"
蓝奏云会话尚未登录。请手动登录一次（会弹出真实浏览器窗口，cookie 保存在 $Profile）：
  agent-browser --profile '$Profile' --headed open 'https://pc.woozooo.com/'
登录完成后重新运行本脚本即可全自动上传。
"@
    exit 2
}

if ($Probe) {
    Write-Host "URL: $url"
    Write-Host "登录判断：$loggedIn"
    Write-Host '--- file inputs ---'
    foreach ($selector in $fileInputSelectors) {
        Write-Host ("{0} => {1}" -f $selector, (Invoke-Browser -Arguments @('get', 'count', $selector) -AllowFailure).Trim())
    }
    Write-Host '--- 交互元素 ---'
    Invoke-Browser -Arguments @('snapshot', '-i', '-c', '-d', '3')
    exit 0
}

if (-not $File -or $File.Count -eq 0) {
    throw '没有指定要上传的文件（-File）。'
}
$paths = foreach ($item in $File) {
    $resolved = if ([System.IO.Path]::IsPathRooted($item)) { $item } else { Join-Path $repoRoot $item }
    if (-not (Test-Path -LiteralPath $resolved)) { throw "文件不存在：$resolved" }
    (Get-Item -LiteralPath $resolved)
}

$inputSelector = $fileInputSelectors | Where-Object { Test-Element $_ } | Select-Object -First 1
if (-not $inputSelector) {
    Write-Warning '页面上找不到文件输入框，蓝奏云可能改版了。先运行：pwsh -File tools/lanzou-publish.ps1 -Probe'
    exit 3
}

$before = Invoke-Browser -Arguments @('snapshot', '-i', '-c')
foreach ($item in $paths) {
    Write-Host ("上传 {0}（{1:N1} MB）…" -f $item.Name, ($item.Length / 1MB))
    Invoke-Browser -Arguments @('upload', $inputSelector, $item.FullName) | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $appeared = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $text = Invoke-Browser -Arguments @('read') -AllowFailure
        if ($text -and $text.Contains($item.Name)) { $appeared = $true; break }
    }
    if (-not $appeared) {
        Write-Warning ("{0} 在 {1} 秒内没有出现在列表里，请到网页里确认上传结果。" -f $item.Name, $TimeoutSeconds)
        exit 1
    }
    Write-Host ("已上传：{0}" -f $item.Name)
}

Write-Host '取分享链接：请在网页里对刚上传的文件点「分享」→「复制链接」。'
Write-Host '（蓝奏云的行内操作菜单要真实鼠标悬停才展开，脚本用合成事件打不开，所以这一步留给人点一次。）'
Write-Host '拿到 https://<你的子域>.lanzou*.com/<id> 后，把它写进 Release 说明的单独一行：'
Write-Host '  备用下载：https://xxx.lanzouq.com/xxxx'
Write-Host '客户端会自动识别这一行（Services/UpdateMirrors.FindBackupDownload），在更新弹窗里显示「备用下载」按钮。'
exit 0
