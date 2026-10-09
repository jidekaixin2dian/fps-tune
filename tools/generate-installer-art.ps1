param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer artwork must remain under dist.' }
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $output -Force | Out-Null
$iconBitmap = [Drawing.Image]::FromFile((Join-Path $root 'FpsTune.Wpf\Assets\app-icon.png'))
$bitmap = New-Object Drawing.Bitmap(328, 628)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$brushes = @()
$fonts = @()
function Draw-Label([string]$text, [string]$family, [single]$size, [single]$x, [single]$y, [string]$color, [Drawing.FontStyle]$weight = [Drawing.FontStyle]::Regular) {
    $font = New-Object Drawing.Font($family, $size, $weight, [Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($color))
    $script:fonts += $font; $script:brushes += $brush
    $graphics.DrawString($text, $font, $brush, $x, $y)
}
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#0C1015'))
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawImage($iconBitmap, 32, 36, 64, 64)
    Draw-Label "FPS`nTUNE" 'Segoe UI' 58 28 140 '#B8DD8D' ([Drawing.FontStyle]::Bold)
    Draw-Label "系统调校`n性能测量" 'Microsoft YaHei UI' 24 30 326 '#E8EEF5'
    Draw-Label '检测 · 审阅 · 实测 · 还原' 'Microsoft YaHei UI' 17 30 508 '#ABB8C9'
    Draw-Label 'WINDOWS 10 / 11  x64' 'Segoe UI' 15 30 564 '#8896A8'
    $bitmap.Save((Join-Path $output 'wizard.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
} finally {
    $graphics.Dispose(); $bitmap.Dispose()
    foreach ($font in $fonts) { $font.Dispose() }
    foreach ($brush in $brushes) { $brush.Dispose() }
}
$small = New-Object Drawing.Bitmap(55, 55)
$smallGraphics = [Drawing.Graphics]::FromImage($small)
try {
    $smallGraphics.Clear([Drawing.Color]::White)
    $smallGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $smallGraphics.DrawImage($iconBitmap, 7, 7, 41, 41)
    $small.Save((Join-Path $output 'logo.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
} finally { $smallGraphics.Dispose(); $small.Dispose(); $iconBitmap.Dispose() }
Write-Host 'Installer branding generated from the project icon and layout; no external assets.'
