# Build final release: single-file EXE + green folder + portable zip
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'FpsTune.Wpf\FpsTune.Wpf.csproj'
$propsPath = Join-Path $root 'Directory.Build.props'
$dist = Join-Path $root 'dist'

[xml]$propsXml = Get-Content $propsPath -Raw -Encoding UTF8
$version = $null
foreach ($pg in @($propsXml.Project.PropertyGroup)) {
    if ($pg.Version) { $version = [string]$pg.Version; break }
}
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error 'Directory.Build.props 缺少有效的三段版本号'
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet not found. Install .NET 8 SDK first.'
    exit 1
}

$singleOut = Join-Path $dist "single-file-$version"
$folderOut = Join-Path $dist "folder-$version"
$publishTmp = Join-Path $dist "publish-tmp-$version"
$singleBld = Join-Path $publishTmp 'single-bld'
$folderBld = Join-Path $publishTmp 'folder-bld'
$zipName = "FpsTune-Portable-$version.zip"
$zip = Join-Path $dist $zipName
$manifest = Join-Path $dist "SHA256SUMS-v$version.txt"

New-Item -ItemType Directory -Path $dist -Force | Out-Null

# 只清理当前版本的输出，历史 dist 资产保持不变。
foreach ($path in @($singleOut, $folderOut, $publishTmp, $zip, $manifest)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishTmp -Force | Out-Null

try {
    Write-Host "Publishing single-file EXE v$version..."
    # 压缩 + 去 PDB: 单文件体积 149MB → 约 70-80MB（WPF 不支持 trimming）
    dotnet publish $proj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none /p:OutDir=$singleBld -o $singleOut | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'single-file publish failed' }

    Write-Host "Publishing green folder v$version..."
    dotnet publish $proj -c Release -r win-x64 --self-contained false /p:DebugType=none /p:OutDir=$folderBld -o $folderOut | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'folder publish failed' }

    $singleExe = Join-Path $singleOut 'FpsTune.exe'
    $folderExe = Join-Path $folderOut 'FpsTune.exe'
    if (-not (Test-Path -LiteralPath $singleExe) -or -not (Test-Path -LiteralPath $folderExe)) {
        throw '发布未生成预期的 FpsTune.exe'
    }

    Write-Host 'Creating portable zip...'
    # DebugType=none 后 folder 输出本应无 pdb; 此清理属双保险。
    # 不用 Get-ChildItem -Include: 它与 -LiteralPath(目录)+-Recurse 组合在 PS5.1 下
    # 不做过滤, 会把目录里全部文件当匹配项删掉
    Get-ChildItem -LiteralPath $folderOut -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $folderOut '*') -DestinationPath $zip -CompressionLevel Optimal
    # Compress-Archive 对空源目录会静默不产出 zip, 必须显式断言
    if (-not (Test-Path -LiteralPath $zip)) { throw 'portable zip was not created' }

    $hashLines = @(
        "$((Get-FileHash -LiteralPath $singleExe -Algorithm SHA256).Hash)  FpsTune.exe",
        "$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)  $zipName"
    )
    $installer = Join-Path $dist "installer\FpsTune-Setup-$version.exe"
    if (Test-Path -LiteralPath $installer) {
        $hashLines += "$((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)  FpsTune-Setup-$version.exe"
    }
    [System.IO.File]::WriteAllLines($manifest, [string[]]$hashLines, [System.Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $publishTmp) {
        Remove-Item -LiteralPath $publishTmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "Done: $dist"
$artifacts = @(
    @{ Label = "single-file EXE"; Path = (Join-Path $singleOut 'FpsTune.exe') },
    @{ Label = "portable zip";    Path = $zip },
    @{ Label = "manifest";        Path = $manifest }
)
$installerPath = Join-Path $dist "installer\FpsTune-Setup-$version.exe"
if (Test-Path -LiteralPath $installerPath) {
    $artifacts += @{ Label = 'installer'; Path = $installerPath }
}
foreach ($a in $artifacts) {
    if (Test-Path -LiteralPath $a.Path) {
        $mb = [math]::Round((Get-Item -LiteralPath $a.Path).Length / 1MB, 1)
        Write-Host ("  {0,-16} {1}  ({2} MB)" -f $a.Label, $a.Path, $mb)
    }
}
