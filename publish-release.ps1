# Build final release: single-file EXE + green folder + portable zip.
#
# This script is a post-commit release gate. It deliberately refuses a dirty
# tracked tree and embeds the SHA of the HEAD it actually builds. A build made
# before the final commit must not be treated as a release artifact.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'FpsTune.Wpf\FpsTune.Wpf.csproj'
$propsPath = Join-Path $root 'Directory.Build.props'
$dist = Join-Path $root 'dist'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git not found. A release build requires a clean committed HEAD.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install .NET 8 SDK first.'
}

function Invoke-GitValue {
    param([Parameter(Mandatory = $true)][string[]]$GitArgs)
    $value = (& git -C $root @GitArgs 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw ('git command failed: git ' + ($GitArgs -join ' '))
    }
    return $value
}

function Assert-CleanSource {
    $status = @(& git -C $root status --porcelain --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw '无法读取 Git 工作树状态。'
    }

    # review-output/ is intentionally untracked and must remain untouched;
    # every other tracked or untracked change blocks a release build.
    $unexpected = @($status | Where-Object {
        $line = [string]$_
        $line -and $line -notmatch '^\?\?\s+review-output(?:[\\/]|$)'
    })
    if ($unexpected.Count -gt 0) {
        throw ('发布构建要求 tracked tree 干净（仅允许 review-output/ 未跟踪）：' +
            [Environment]::NewLine + ($unexpected -join [Environment]::NewLine))
    }
}

$finalSha = Invoke-GitValue @('rev-parse', 'HEAD')
if ($finalSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "HEAD 不是完整 40 位 Git SHA：$finalSha"
}
Assert-CleanSource

[xml]$propsXml = Get-Content $propsPath -Raw -Encoding UTF8
# 版本唯一来源拆两段：VersionPrefix（三段数字，产物目录/清单/比较用）+ VersionSuffix（预发布标识）
$versionPrefix = $null
$versionSuffix = $null
foreach ($pg in @($propsXml.Project.PropertyGroup)) {
    if (-not $versionPrefix -and $pg.VersionPrefix) { $versionPrefix = ([string]$pg.VersionPrefix).Trim() }
    if (-not $versionSuffix -and $pg.VersionSuffix) { $versionSuffix = ([string]$pg.VersionSuffix).Trim() }
}
$version = $versionPrefix
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Directory.Build.props 缺少有效的三段 VersionPrefix'
}
$displayVersion = if ($versionSuffix) { "$versionPrefix-$versionSuffix" } else { $versionPrefix }

$singleOut = Join-Path $dist "single-file-$version"
$folderOut = Join-Path $dist "folder-$version"
$publishTmp = Join-Path $dist "publish-tmp-$version"
$singleBld = Join-Path $publishTmp 'single-bld'
$folderBld = Join-Path $publishTmp 'folder-bld'
$zipName = "FpsTune-Portable-$version.zip"
$zip = Join-Path $dist $zipName
$manifest = Join-Path $dist "SHA256SUMS-v$version.txt"
$informationalVersion = "$displayVersion+$finalSha"

New-Item -ItemType Directory -Path $dist -Force | Out-Null

# 只清理当前版本的输出，历史 dist 资产和 review-output/ 保持不变。
foreach ($path in @($singleOut, $folderOut, $publishTmp, $zip, $manifest)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $publishTmp -Force | Out-Null

function Assert-SourceStillUnchanged {
    $currentSha = Invoke-GitValue @('rev-parse', 'HEAD')
    if (-not [string]::Equals($currentSha, $finalSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布期间 HEAD 发生变化：起始 $finalSha，当前 $currentSha"
    }
    Assert-CleanSource
}

function Assert-ProductIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少发布产物：$Path"
    }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $productVersion = [string]$info.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        $productVersion.IndexOf($version, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $productVersion.IndexOf($finalSha, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "产物版本追溯校验失败：$Path；ProductVersion='$productVersion'，需要同时包含 $version 和 $finalSha"
    }
    Write-Host "  ProductVersion: $productVersion  [$Path]"
}

function Invoke-StandaloneCli {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $previous = Get-Location
    try {
        # Run from a directory containing only the copied EXE. This exercises
        # embedded catalog/scripts instead of accidentally using repository files.
        Set-Location $standaloneDir
        $output = (& $Executable @Arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
        return @{ Output = $output; ExitCode = $exitCode }
    }
    finally {
        Set-Location $previous
    }
}

try {
    Write-Host "Publishing single-file EXE v$version from HEAD $finalSha..."
    # WPF native libraries and declared content are bundled/extracted by the
    # single-file host, so the uploaded FpsTune.exe works by itself.
    $singleArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '/p:PublishSingleFile=true',
        '/p:IncludeNativeLibrariesForSelfExtract=true',
        '/p:IncludeAllContentForSelfExtract=true',
        '/p:EnableCompressionInSingleFile=true',
        '/p:DebugType=none',
        "/p:InformationalVersion=$informationalVersion",
        "/p:SourceRevisionId=$finalSha",
        "/p:OutDir=$singleBld",
        '-o', $singleOut
    )
    & dotnet @singleArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'single-file publish failed' }

    Write-Host "Publishing green folder v$version..."
    $folderArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '/p:DebugType=none',
        "/p:InformationalVersion=$informationalVersion",
        "/p:SourceRevisionId=$finalSha",
        "/p:OutDir=$folderBld",
        '-o', $folderOut
    )
    & dotnet @folderArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'folder publish failed' }

    Assert-SourceStillUnchanged

    $singleExe = Join-Path $singleOut 'FpsTune.exe'
    $folderExe = Join-Path $folderOut 'FpsTune.exe'
    Assert-ProductIdentity $singleExe
    Assert-ProductIdentity $folderExe

    # A release asset is uploaded as one executable. Any external native DLL,
    # script, catalog or PDB here would make that promise false.
    $singleFullPath = (Get-Item -LiteralPath $singleExe).FullName
    $singleExtra = @(Get-ChildItem -LiteralPath $singleOut -Recurse -File |
        Where-Object { $_.FullName -ne $singleFullPath })
    $singleDirs = @(Get-ChildItem -LiteralPath $singleOut -Recurse -Directory)
    if ($singleExtra.Count -gt 0 -or $singleDirs.Count -gt 0) {
        $names = @($singleExtra | ForEach-Object { $_.FullName }) +
            @($singleDirs | ForEach-Object { $_.FullName })
        throw ('single-file 输出包含 FpsTune.exe 之外的文件或目录：' +
            [Environment]::NewLine + ($names -join [Environment]::NewLine))
    }

    Write-Host 'Creating portable zip...'
    # DebugType=none 后 folder 输出本应无 pdb; 此清理属双保险。
    Get-ChildItem -LiteralPath $folderOut -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $folderOut '*') -DestinationPath $zip -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw 'portable zip was not created' }

    # Check the portable archive contains a version/SHA-bearing executable.
    $zipCheck = Join-Path $publishTmp 'zip-check'
    Expand-Archive -LiteralPath $zip -DestinationPath $zipCheck -Force
    Assert-ProductIdentity (Join-Path $zipCheck 'FpsTune.exe')

    # Smoke-test the exact uploaded single EXE from an empty directory.
    $standaloneDir = Join-Path $publishTmp 'standalone'
    New-Item -ItemType Directory -Path $standaloneDir -Force | Out-Null
    $standaloneExe = Join-Path $standaloneDir 'FpsTune.exe'
    Copy-Item -LiteralPath $singleExe -Destination $standaloneExe
    $versionResult = Invoke-StandaloneCli $standaloneExe @('-Version')
    if ($versionResult.ExitCode -ne 0 -or $versionResult.Output -notmatch [regex]::Escape($version)) {
        throw "单文件 -Version 验证失败（exit=$($versionResult.ExitCode)）：$($versionResult.Output)"
    }
    $detectResult = Invoke-StandaloneCli $standaloneExe @('-Detect', '-Json')
    if ($detectResult.ExitCode -ne 0) {
        throw "单文件 -Detect -Json 验证失败（exit=$($detectResult.ExitCode)）：$($detectResult.Output)"
    }
    try {
        $detectJson = $detectResult.Output | ConvertFrom-Json
        if ($null -eq $detectJson.hardware -or $null -eq $detectJson.items) {
            throw 'JSON 缺少 hardware/items 字段'
        }
    }
    catch {
        throw "单文件 -Detect -Json 输出无法解析：$($_.Exception.Message)"
    }
    Write-Host '  Standalone -Version and -Detect -Json: OK'

    $hashLines = @(
        "$((Get-FileHash -LiteralPath $singleExe -Algorithm SHA256).Hash)  FpsTune.exe",
        "$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)  $zipName"
    )
    [System.IO.File]::WriteAllLines($manifest, [string[]]$hashLines, [System.Text.UTF8Encoding]::new($false))

    $manifestLines = @(Get-Content -LiteralPath $manifest -Encoding UTF8)
    if ($manifestLines.Count -ne 2 -or
        $manifestLines[0] -notmatch '^[0-9A-Fa-f]{64}\s+FpsTune\.exe$' -or
        $manifestLines[1] -notmatch ('^[0-9A-Fa-f]{64}\s+' + [regex]::Escape($zipName) + '$')) {
        throw "SHA256 清单格式或资产口径校验失败：$manifest"
    }

    Assert-SourceStillUnchanged
}
finally {
    if (Test-Path -LiteralPath $publishTmp) {
        Remove-Item -LiteralPath $publishTmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "Done: $dist"
$artifacts = @(
    @{ Label = 'single-file EXE'; Path = (Join-Path $singleOut 'FpsTune.exe') },
    @{ Label = 'portable zip'; Path = $zip },
    @{ Label = 'manifest'; Path = $manifest }
)
foreach ($a in $artifacts) {
    if (Test-Path -LiteralPath $a.Path) {
        $mb = [math]::Round((Get-Item -LiteralPath $a.Path).Length / 1MB, 1)
        Write-Host ("  {0,-16} {1}  ({2} MB)" -f $a.Label, $a.Path, $mb)
    }
}
