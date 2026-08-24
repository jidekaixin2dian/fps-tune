# Build final release: single-file EXE + green folder + portable zip
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'DeltaForceTune.Wpf\DeltaForceTune.Wpf.csproj'
$dist = Join-Path $root 'dist'
$publishTmp = Join-Path $dist 'publish-tmp'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet not found. Install .NET 8 SDK first.'
    exit 1
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path $publishTmp -Force | Out-Null

$singleBld = Join-Path $publishTmp 'single-bld'
$folderBld = Join-Path $publishTmp 'folder-bld'

Write-Host 'Publishing single-file EXE...'
dotnet publish $proj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:OutDir=$singleBld -o (Join-Path $dist 'single-file') | Out-Host
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host 'Publishing green folder...'
dotnet publish $proj -c Release -r win-x64 --self-contained false /p:OutDir=$folderBld -o (Join-Path $dist 'folder') | Out-Host
if ($LASTEXITCODE -ne 0) { exit 1 }

$singleExe = Join-Path $dist 'single-file\DeltaForceTune.exe'
if (-not (Test-Path $singleExe)) {
    Write-Error "Single-file publish did not produce expected file: $singleExe"
    exit 1
}

Write-Host 'Creating portable zip...'
$zip = Join-Path $dist 'DeltaForceTune-Portable.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist 'folder\*') -DestinationPath $zip -CompressionLevel Optimal

# 清理发布过程的临时中间目录
Remove-Item $publishTmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Done: $dist"
Write-Host "  $singleExe"
Write-Host "  $zip"
