# Rebuild the tray companion and application assets from their SVG source files.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build-BrandAssets.ps1')
