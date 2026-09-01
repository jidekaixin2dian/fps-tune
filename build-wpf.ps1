# Build / publish WPF version
param(
    [ValidateSet('Build','PublishSingleFile','PublishFolder')]
    [string]$Mode = 'Build'
)

$project = Join-Path $PSScriptRoot 'FpsTune.Wpf\FpsTune.Wpf.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet not found. Please install .NET 8 SDK first.'
    exit 1
}

switch ($Mode) {
    'Build' {
        dotnet build $project -c Release
    }
    'PublishSingleFile' {
        # 压缩 + 去 PDB: 单文件体积 149MB → 约 70-80MB（WPF 不支持 trimming）
        dotnet publish $project -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none
    }
    'PublishFolder' {
        dotnet publish $project -c Release -r win-x64 --self-contained false
    }
}
