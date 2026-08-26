# Build installer with Inno Setup
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$singleExe = Join-Path $root 'dist\single-file\DeltaForceTune.exe'

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

if (-not $iscc) {
    Write-Error 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php'
    exit 1
}

$csproj = Join-Path $root 'DeltaForceTune.Wpf\DeltaForceTune.Wpf.csproj'
[xml]$projXml = Get-Content $csproj
$version = ($projXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '1.0.0' }
Write-Host "App version: $version"
Write-Host "Using Inno Setup: $iscc"
& $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host 'Installer built successfully.'
