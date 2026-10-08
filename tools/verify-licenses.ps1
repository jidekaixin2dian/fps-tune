# Verify the reviewed source inventory and reproducible user-facing notices.
[CmdletBinding()]
param([switch]$Generate)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$inventory = Get-Content (Join-Path $root 'licenses/manifest.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content (Join-Path $root 'FpsTune.Wpf/FpsTune.Wpf.csproj') -Raw
foreach ($package in $project.Project.ItemGroup.PackageReference) {
    if (-not $package.Include) { continue }
    $component = @($inventory.Components | Where-Object Name -eq $package.Include)
    if ($component.Count -ne 1 -or $component[0].Version -ne $package.Version) {
        throw "Unreviewed package license: $($package.Include) $($package.Version)"
    }
}
if ([string]$project.Project.PropertyGroup.RuntimeFrameworkVersion -ne $inventory.RuntimeVersion) {
    throw 'Runtime license inventory differs from RuntimeFrameworkVersion.'
}
$notice = "FPS Tune - third-party notices`n`nThe project LICENSE applies to FPS Tune's own code. Components retain their respective licenses.`nMicrosoft packages: Copyright (c) Microsoft Corporation. All rights reserved.`nThe NVIDIA driver is supplied by the user and is not bundled.`n`n"
foreach ($component in $inventory.Components) {
    $notice += "============================================================`n$($component.Name) - $($component.Version)`n$($component.Scope)`nSource: $($component.Source)`n`n"
    foreach ($file in $component.Files) {
        $path = Join-Path $root $file.Path
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($algorithm.ComputeHash([IO.File]::ReadAllBytes($path))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        if ($actualHash -ne $file.Sha256) {
            throw "License source changed without review: $($file.Path)"
        }
        $notice += "$($file.Path)`n" + [IO.File]::ReadAllText($path) + "`n`n"
    }
}
$notice += "API references (not bundled libraries):`n"
foreach ($reference in $inventory.References) {
    $notice += "$($reference.Name): $($reference.License)`n$($reference.Source)`n$($reference.Scope)`n`n"
}
$noticePath = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
if ($Generate) { [IO.File]::WriteAllText($noticePath, $notice, [Text.UTF8Encoding]::new($false)) }
if (-not (Test-Path -LiteralPath $noticePath) -or [IO.File]::ReadAllText($noticePath) -cne $notice) {
    throw 'THIRD-PARTY-NOTICES.txt differs from the reviewed inventory. Review changes, then run -Generate.'
}
Write-Host "License inventory verified: $($inventory.Components.Count) components; full notices ready for embedding and packaging."
