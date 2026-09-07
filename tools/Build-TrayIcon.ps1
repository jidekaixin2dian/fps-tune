# Small-size vector companion to the full application artwork; no resampled master bitmap.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$frames = @()
foreach ($size in @(16,20,24,28,32,40,48,64)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#07141B'))
    $cyan = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#22D3EE'),2)
    $white = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#E5FCFF'),1.7)
    $stream = [IO.MemoryStream]::new()
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.ScaleTransform(($size/16.0),($size/16.0))
        $path.AddArc(0.25,0.25,6,6,180,90); $path.AddArc(9.75,0.25,6,6,270,90)
        $path.AddArc(9.75,9.75,6,6,0,90); $path.AddArc(0.25,9.75,6,6,90,90); $path.CloseFigure()
        $g.FillPath($ink,$path)
        $cyan.StartCap = $cyan.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $white.StartCap = $white.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($cyan,2.1,2.1,11.8,11.8,140,260)
        $g.DrawLine($white,8,8.5,11.5,5)
        $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
        $frames += [pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
    } finally { $stream.Dispose();$white.Dispose();$cyan.Dispose();$ink.Dispose();$path.Dispose();$g.Dispose();$bitmap.Dispose() }
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $PSScriptRoot '../FpsTune.Wpf/Assets/tray.ico')))
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
    $offset=6+16*$frames.Count
    foreach($f in $frames) {
        $writer.Write([byte]$f.Size);$writer.Write([byte]$f.Size);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$f.Bytes.Length);$writer.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach($f in $frames) { $writer.Write([byte[]]$f.Bytes) }
} finally { $writer.Dispose() }
