# Build installer with Inno Setup
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$propsPath = Join-Path $root 'Directory.Build.props'
[xml]$propsXml = Get-Content $propsPath -Raw -Encoding UTF8
$version = $null
foreach ($pg in @($propsXml.Project.PropertyGroup)) { if ($pg.Version) { $version = [string]$pg.Version; break } }
if (-not $version) { Write-Error 'Directory.Build.props 缺少 <Version>'; exit 1 }
$version = $version.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { Write-Error 'Directory.Build.props 的 <Version> 必须是三段版本号'; exit 1 }

$singleExe = Join-Path $root "dist\single-file-$version\FpsTune.exe"
$portableZip = Join-Path $root "dist\FpsTune-Portable-$version.zip"
$installer = Join-Path $root "dist\installer\FpsTune-Setup-$version.exe"
$manifest = Join-Path $root "dist\SHA256SUMS-v$version.txt"

$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not (Test-Path $singleExe)) {
    Write-Error "Single-file EXE not found: $singleExe. Run .\publish-release.ps1 first."
    exit 1
}
if (-not (Test-Path -LiteralPath $portableZip)) {
    Write-Error "Versioned portable zip not found: $portableZip. Run .\publish-release.ps1 first."
    exit 1
}

if (-not $iscc) {
    Write-Error 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php'
    exit 1
}

Write-Host "App version: $version"
Write-Host "Using Inno Setup: $iscc"
& $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { exit 1 }
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Error "Installer did not produce expected file: $installer"
    exit 1
}

$hashLines = @(
    "$((Get-FileHash -LiteralPath $singleExe -Algorithm SHA256).Hash)  FpsTune.exe",
    "$((Get-FileHash -LiteralPath $portableZip -Algorithm SHA256).Hash)  FpsTune-Portable-$version.zip",
    "$((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)  FpsTune-Setup-$version.exe"
)
[System.IO.File]::WriteAllLines($manifest, [string[]]$hashLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "SHA256 manifest written: $manifest"
Write-Host 'Installer built successfully.'
