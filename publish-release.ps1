# Build final release: single-file EXE + green folder
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'DeltaForceTune.Wpf\DeltaForceTune.Wpf.csproj'
$dist = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet not found. Install .NET 8 SDK first.'
    exit 1
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host 'Publishing single-file EXE...'
dotnet publish $proj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o (Join-Path $dist 'single-file') | Out-Host
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host 'Publishing green folder...'
dotnet publish $proj -c Release -r win-x64 --self-contained false -o (Join-Path $dist 'folder') | Out-Host
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host ''
Write-Host "Done: $dist"
