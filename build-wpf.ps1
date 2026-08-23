# Build / publish WPF version
param(
    [ValidateSet('Build','PublishSingleFile','PublishFolder')]
    [string]$Mode = 'Build'
)

$project = Join-Path $PSScriptRoot 'DeltaForceTune.Wpf\DeltaForceTune.Wpf.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet not found. Please install .NET 8 SDK first.'
    exit 1
}

switch ($Mode) {
    'Build' {
        dotnet build $project -c Release
    }
    'PublishSingleFile' {
        dotnet publish $project -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
    }
    'PublishFolder' {
        dotnet publish $project -c Release -r win-x64 --self-contained false
    }
}
