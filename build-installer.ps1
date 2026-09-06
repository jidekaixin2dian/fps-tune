# Build installer with Inno Setup
[CmdletBinding()]
param(
    [string]$IsccPath = $env:ISCC_PATH,
    [switch]$CheckOnly
)

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

$isccCandidates = @()
if ($IsccPath) {
    $isccCandidates += $IsccPath
}
else {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $isccCandidates += $command.Source }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' }))) {
        if ($base) { $isccCandidates += Join-Path $base 'Inno Setup 6\ISCC.exe' }
    }
}
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) {
    throw ("Cannot locate ISCC.exe in this execution environment (user: $([Environment]::UserName)). " +
        "This does not prove Inno Setup is uninstalled. Pass -IsccPath or set ISCC_PATH. Checked: " +
        ($isccCandidates -join '; '))
}
$iscc = (Get-Item -LiteralPath $iscc).FullName
Write-Host "Using Inno Setup: $iscc"
try {
    # ProcessStartInfo avoids Windows PowerShell 5.1 treating native help on
    # stderr as a terminating PowerShell error under ErrorActionPreference=Stop.
    $probe = New-Object System.Diagnostics.Process
    $probe.StartInfo.FileName = $iscc
    $probe.StartInfo.Arguments = '/?'
    $probe.StartInfo.UseShellExecute = $false
    $probe.StartInfo.CreateNoWindow = $true
    $probe.StartInfo.RedirectStandardOutput = $true
    $probe.StartInfo.RedirectStandardError = $true
    [void]$probe.Start()
    $stdout = $probe.StandardOutput.ReadToEndAsync()
    $stderr = $probe.StandardError.ReadToEndAsync()
    $probe.WaitForExit()
    $compilerHelp = $stdout.Result + $stderr.Result
    $compilerExit = $probe.ExitCode
}
catch {
    throw "ISCC.exe exists at '$iscc', but could not be started. Check sandbox/Windows execution permissions; do not reinstall based on this error. Details: $($_.Exception.Message)"
}
finally {
    if ($probe) { $probe.Dispose() }
}
# ISCC 6 returns 1 for /? even when help is successfully displayed.
if ($compilerExit -notin @(0, 1) -or ($compilerHelp -join "`n") -notmatch 'Inno Setup.*Command-Line Compiler') {
    throw "ISCC.exe probe failed (exit=$compilerExit): $compilerHelp"
}
if ($CheckOnly) {
    Write-Host 'Inno Setup compiler execution verified.'
    return
}

if (-not (Test-Path $singleExe)) {
    Write-Error "Single-file EXE not found: $singleExe. Run .\publish-release.ps1 first."
    exit 1
}
if (-not (Test-Path -LiteralPath $portableZip)) {
    Write-Error "Versioned portable zip not found: $portableZip. Run .\publish-release.ps1 first."
    exit 1
}

Write-Host "App version: $version"
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
