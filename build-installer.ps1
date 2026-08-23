# Build installer with Inno Setup
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    Write-Error 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php'
    exit 1
}
& $iscc (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host 'Installer built successfully.'
