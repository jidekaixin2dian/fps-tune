# SVG is the source of truth. Only rect/path primitives and local linear gradients are accepted.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../FpsTune.Wpf/Assets'))
function New-Brush([string]$value, $gradients) {
    if (-not $value -or $value -eq 'none') { return $null }
    if ($value -match '^url\(#([\w-]+)\)$') {
        if (-not $gradients.ContainsKey($Matches[1])) { throw "Unknown SVG gradient: $value" }
        return $gradients[$Matches[1]]
    }
    return [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($value)
}
function Read-Svg([string]$name) {
    [xml]$svg = [IO.File]::ReadAllText((Join-Path $assetDirectory ($name + '.svg')))
    $gradients = @{}
    foreach ($definition in $svg.svg.SelectNodes('*[local-name()="defs"]/*')) {
        if ($definition.LocalName -ne 'linearGradient' -or $definition.gradientUnits -ne 'userSpaceOnUse') { throw 'Only user-space linear gradients are supported' }
        $brush = [Windows.Media.LinearGradientBrush]::new()
        $brush.MappingMode = [Windows.Media.BrushMappingMode]::Absolute
        $brush.StartPoint = [Windows.Point]::new([double]$definition.x1,[double]$definition.y1)
        $brush.EndPoint = [Windows.Point]::new([double]$definition.x2,[double]$definition.y2)
        foreach ($stop in $definition.ChildNodes) {
            if ($stop.LocalName -ne 'stop') { throw 'Unsupported gradient child' }
            $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.GetAttribute('stop-color'))
            $offset = [double]::Parse($stop.GetAttribute('offset'),[Globalization.CultureInfo]::InvariantCulture)
            $brush.GradientStops.Add([Windows.Media.GradientStop]::new($color,$offset))
        }
        $brush.Freeze()
        $gradients.Add($definition.id,$brush)
    }
    $drawing = [Windows.Media.DrawingGroup]::new()
    $box = @($svg.svg.viewBox.Split(' ') | ForEach-Object { [double]::Parse($_,[Globalization.CultureInfo]::InvariantCulture) })
    $context = $drawing.Open()
    try {
        $context.DrawRectangle([Windows.Media.Brushes]::Transparent,$null,[Windows.Rect]::new(0,0,$box[2],$box[3]))
        foreach ($node in $svg.svg.ChildNodes) {
            if ($node.LocalName -in @('title','defs') -or $node.NodeType -eq 'Comment') { continue }
            $brush = New-Brush $node.GetAttribute('fill') $gradients
            if ($node.LocalName -eq 'rect') {
                $context.DrawRoundedRectangle($brush,$null,[Windows.Rect]::new([double]$node.x,[double]$node.y,[double]$node.width,[double]$node.height),[double]$node.rx,[double]$node.rx)
            } elseif ($node.LocalName -eq 'path') {
                $pen = $null
                if ($node.GetAttribute('stroke')) {
                    $pen = [Windows.Media.Pen]::new((New-Brush $node.GetAttribute('stroke') $gradients),[double]::Parse($node.GetAttribute('stroke-width'),[Globalization.CultureInfo]::InvariantCulture))
                    $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
                    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
                }
                $context.DrawGeometry($brush,$pen,[Windows.Media.Geometry]::Parse($node.d))
            } else { throw "Unsupported SVG element: $($node.LocalName)" }
        }
    } finally { $context.Close() }
    $drawing.Freeze()
    return @{ Drawing=$drawing; Size=$box[2] }
}
function Write-Icon([string]$name,$source) {
    $frames = @()
    foreach ($size in @(16,20,24,28,32,40,48,64,96,128,256)) {
        $visual = [Windows.Media.DrawingVisual]::new(); $context = $visual.RenderOpen()
        try { $context.PushTransform([Windows.Media.ScaleTransform]::new($size/$source.Size,$size/$source.Size)); $context.DrawDrawing($source.Drawing); $context.Pop() }
        finally { $context.Close() }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.MemoryStream]::new()
        try { $encoder.Save($stream); $bytes=$stream.ToArray() } finally { $stream.Dispose() }
        $frames += @{Size=$size; Bytes=$bytes}
        if($size -eq 256 -and $name -eq 'app'){[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app-icon.png'),$bytes)}
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assetDirectory ($name+'.ico'))))
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach($frame in $frames){
            $dimension = if($frame.Size -eq 256){0}else{$frame.Size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
    } finally { $writer.Dispose() }
}
$app = Read-Svg 'app'; $tray = Read-Svg 'tray'
Write-Icon 'app' $app; Write-Icon 'tray' $tray
$resources = [Windows.ResourceDictionary]::new()
$resources.Add('AppMark',[Windows.Media.DrawingImage]::new($app.Drawing))
$resources.Add('TrayMark',[Windows.Media.DrawingImage]::new($tray.Drawing))
$resourceFile = Join-Path $PSScriptRoot '../FpsTune.Wpf/Resources/BrandAssets.xaml'
[IO.File]::WriteAllText($resourceFile,[Windows.Markup.XamlWriter]::Save($resources).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
Write-Host 'Brand assets generated from two SVG sources: WPF vectors, PNG and 11-size application/tray ICOs.'
