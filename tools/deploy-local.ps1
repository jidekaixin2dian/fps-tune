#Requires -Version 5.1
<#
把已构建的产物部署到本机安装目录，并当场核对「安装信息」还是完整的。

为什么要单独做这一步：
  以前收工时是直接把开发构建目录整个拷进 D:/FpsTune。安装器（Inno）当年写下的是
  单文件形态（FpsTune.exe + LICENSE + THIRD-PARTY-NOTICES.txt）与一条卸载项、两个快捷方式；
  一旦目录被换成 dotnet build 的多文件产物，卸载器只会删它认识的那几个文件，
  而卸载项/快捷方式被外部工具清掉后程序还能跑、开始菜单却搜不到 —— 这次就踩到了。

所以部署固定做三件事：
  1) 产物只认发布产物（dist/single-file-<版本> 优先；多文件版本要显式 -AllowFolderSource）；
  2) 覆盖前整目录备份到 work/install-backups/，覆盖后把多出来的文件挪进备份（不硬删）；
  3) 覆盖后跑 FpsTune.exe -InstallCheck，缺什么就用 -InstallRepair 补回来，并把结果写进报告。

用法：
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/deploy-local.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/deploy-local.ps1 -DryRun
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/deploy-local.ps1 -Source dist\folder-0.4.1 -AllowFolderSource
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'D:\FpsTune',
    [string]$Source = '',
    [switch]$AllowFolderSource,
    [switch]$NoBackup,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installFull = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$productExe = 'FpsTune.exe'

function Get-ProductVersion {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    $text = [System.IO.File]::ReadAllText($props)
    $m = [regex]::Match($text, '<VersionPrefix>([^<]+)</VersionPrefix>')
    if (-not $m.Success) { throw "读不到 Directory.Build.props 里的 VersionPrefix" }
    $suffix = [regex]::Match($text, '<VersionSuffix>([^<]*)</VersionSuffix>').Groups[1].Value
    return ($m.Groups[1].Value + $suffix).Trim()
}

$version = Get-ProductVersion
if (-not $Source) {
    $single = Join-Path $repoRoot ("dist\single-file-$version")
    if (Test-Path -LiteralPath $single) { $Source = $single }
    else { $Source = Join-Path $repoRoot ("dist\folder-$version") }
}
$sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
    throw "找不到部署源：$sourceFull（先跑 publish-release.ps1）"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceFull $productExe) -PathType Leaf)) {
    throw "部署源里没有 $productExe：$sourceFull"
}

# 开发构建目录（bin/obj 或带 pdb 的多文件产物）不能直接当发布产物用。
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceFull -File -Recurse -Force)
$pdb = @($sourceFiles | Where-Object { $_.Extension -eq '.pdb' })
if ($pdb.Count -gt 0) {
    throw ("部署源里带调试符号（{0}），这不是发布产物：{1}" -f $pdb[0].Name, $sourceFull)
}
$isSingleFile = ($sourceFiles.Count -le 3)
if (-not $isSingleFile -and -not $AllowFolderSource) {
    throw ("部署源是多文件形态（{0} 个文件），而安装记录写的是单文件。确实要多文件就显式加 -AllowFolderSource。" -f $sourceFiles.Count)
}

Write-Host "部署版本: $version"
Write-Host "部署源  : $sourceFull ($($sourceFiles.Count) 个文件)"
Write-Host "安装目录: $installFull"

if (-not (Test-Path -LiteralPath $installFull -PathType Container)) {
    throw "安装目录不存在：$installFull（本脚本只覆盖已有的本机安装，不做全新安装）"
}

if ($DryRun) {
    Write-Host (Get-Content -LiteralPath (Join-Path $installFull 'FpsTune.exe') -TotalCount 0 -ErrorAction SilentlyContinue)
    Write-Host 'DryRun：只检查，不备份、不覆盖、不启动。'
    return
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $repoRoot ('work\install-backups\FpsTune-backup-' + $stamp)
$reportDir = Join-Path $repoRoot ('work\verify-deploy-' + $stamp)
[void][System.IO.Directory]::CreateDirectory($reportDir)

# 1) 先关掉正在运行的实例：文件被占用时复制会失败，硬覆盖还可能留下半新半旧。
$running = @(Get-Process -Name 'FpsTune' -ErrorAction SilentlyContinue)
foreach ($proc in $running) {
    Write-Host "关闭正在运行的 FpsTune (PID $($proc.Id))"
    try {
        if ($proc.MainWindowHandle -ne 0) { [void]$proc.CloseMainWindow() }
    } catch { }
}
if ($running.Count -gt 0) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'FpsTune' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    $left = @(Get-Process -Name 'FpsTune' -ErrorAction SilentlyContinue)
    foreach ($proc in $left) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    if ($left.Count -gt 0) { Start-Sleep -Milliseconds 500 }
}

# 2) 备份：整目录（含 unins000.*，方便回滚安装信息本身）。
if (-not $NoBackup) {
    [void][System.IO.Directory]::CreateDirectory($backupRoot)
    $before = @(Get-ChildItem -LiteralPath $installFull -File -Force)
    foreach ($file in $before) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $backupRoot $file.Name) -Force
    }
    $after = @(Get-ChildItem -LiteralPath $backupRoot -File -Force)
    if ($after.Count -ne $before.Count) {
        throw ("备份文件数不一致：安装目录 {0} 个，备份 {1} 个" -f $before.Count, $after.Count)
    }
    $srcHash = (Get-FileHash -LiteralPath (Join-Path $installFull $productExe) -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash -LiteralPath (Join-Path $backupRoot $productExe) -Algorithm SHA256).Hash
    if ($srcHash -ne $dstHash) { throw '备份后的 FpsTune.exe 哈希不一致' }
    Write-Host "已备份 $($after.Count) 个文件到 $backupRoot"
}

# 3) 覆盖：发布产物整份复制过来。
$keep = @('unins000.exe', 'unins000.dat', 'FpsTune.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($sourceFull.Length).TrimStart('\')
    $dest = Join-Path $installFull $relative
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($dest))
    Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
}

# 4) 多出来的文件挪进备份目录，而不是删掉：用户可能还在用旧版排障。
#    目录同样要挪：旧版安装器留下的 amd64\ 这类遗留载荷，新卸载器并不认识。
$strays = @(Get-ChildItem -LiteralPath $installFull -File -Force | Where-Object { $keep -notcontains $_.Name })
$strayDirs = @(Get-ChildItem -LiteralPath $installFull -Directory -Force)
$moved = @()
foreach ($file in $strays) {
    $target = Join-Path $backupRoot ('stray-' + $file.Name)
    Move-Item -LiteralPath $file.FullName -Destination $target -Force
    $moved += $file.Name
}
foreach ($dir in $strayDirs) {
    $target = Join-Path $backupRoot ('stray-dir-' + $dir.Name)
    Move-Item -LiteralPath $dir.FullName -Destination $target -Force
    $moved += ($dir.Name + '\')
}
if ($moved.Count -gt 0) {
    Write-Host ("移除安装记录之外的文件 {0} 个（已挪进备份）：{1}" -f $moved.Count, ($moved -join ', '))
}

# 5) 核对版本，然后自检安装信息；缺了就补。
$exePath = Join-Path $installFull $productExe
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($info.ProductVersion -notlike "$version*") {
    throw ("部署后的 ProductVersion 与版本号不符：{0}（期望 {1}）" -f $info.ProductVersion, $version)
}
Write-Host "部署后 ProductVersion: $($info.ProductVersion)"

$checkRaw = & $exePath -InstallCheck -Json 2>&1 | Out-String
$checkJson = $null
try { $checkJson = $checkRaw | ConvertFrom-Json } catch { }
$repairJson = $null
if ($checkJson -and -not $checkJson.complete) {
    Write-Host ('安装信息缺失：' + (@($checkJson.gaps | ForEach-Object { $_.gap }) -join ', ') + '，开始修复')
    $repairRaw = & $exePath -InstallRepair -Json 2>&1 | Out-String
    try { $repairJson = $repairRaw | ConvertFrom-Json } catch { }
    $checkRaw = & $exePath -InstallCheck -Json 2>&1 | Out-String
    try { $checkJson = $checkRaw | ConvertFrom-Json } catch { }
}

$report = [ordered]@{
    deployedAt = (Get-Date).ToString('o')
    version = $version
    installDir = $installFull
    source = $sourceFull
    sourceFiles = $sourceFiles.Count
    singleFile = $isSingleFile
    backup = $backupRoot
    productVersion = $info.ProductVersion
    movedStrays = $moved
    installCheck = $checkJson
    installRepair = $repairJson
}
$reportPath = Join-Path $reportDir 'deploy.json'
[System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))
Write-Host "部署报告: $reportPath"

if (-not $checkJson) { throw '安装信息自检没有返回可解析的 JSON' }
if (-not $checkJson.complete) {
    throw ('安装信息仍不完整：' + (@($checkJson.gaps | ForEach-Object { $_.gap }) -join ', '))
}
Write-Host '安装信息完整，部署完成。'
