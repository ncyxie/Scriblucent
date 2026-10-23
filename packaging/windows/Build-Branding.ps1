[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName PresentationCore, WindowsBase

# Render the existing outlined logo, rather than depending on installed fonts or SVG tools.
[xml]$logo = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Scriblucent/Assets/brand-logo-light.svg') -Raw
$shapes = @($logo.svg.g.g.ChildNodes | Where-Object { $_.LocalName -in 'path', 'circle' })
$mark = @($shapes | Select-Object -First 11)
$wordmark = @($shapes | Select-Object -Skip 11)

function Brush([string]$Color) {
    [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($Color)
}

function Draw-Logo($Context, $Nodes, [Windows.Rect]$Bounds, [double]$X, [double]$Y,
    [double]$Width, [string]$Ink, [string]$Accent) {
    $scale = $Width / $Bounds.Width
    $Context.PushTransform([Windows.Media.TranslateTransform]::new($X, $Y))
    $Context.PushTransform([Windows.Media.ScaleTransform]::new($scale, $scale))
    $Context.PushTransform([Windows.Media.TranslateTransform]::new(-$Bounds.X, -$Bounds.Y))
    foreach ($node in $Nodes) {
        $color = $Ink
        if ($node.GetAttribute('class') -eq 'cls-3') { $color = $Accent }
        if ($node.LocalName -eq 'circle') {
            $Context.DrawEllipse((Brush $color), $null,
                [Windows.Point]::new([double]$node.cx, [double]$node.cy), [double]$node.r, [double]$node.r)
        } else {
            $geometry = [Windows.Media.PathGeometry]::CreateFromGeometry([Windows.Media.Geometry]::Parse($node.d))
            $geometry.FillRule = [Windows.Media.FillRule]::Nonzero
            $Context.DrawGeometry((Brush $color), $null, $geometry)
        }
    }
    $Context.Pop(); $Context.Pop(); $Context.Pop()
}

function Render-Png([int]$Width, [int]$Height, [scriptblock]$Draw) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try { & $Draw $context } finally { $context.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Width, $Height, 96, 96,
        [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}

$markBounds = [Windows.Rect]::new(44, 49.89, 171.25, 190.51)
$textBounds = [Windows.Rect]::new(226.1, 139.77, 477.38, 70)

# Each ICO frame is rendered independently. No opaque tile, legacy mask, or scaled-up small frame.
$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)) {
    $bytes = Render-Png $size $size {
        param($dc)
        $height = $size * 0.9
        $width = $height * $markBounds.Width / $markBounds.Height
        Draw-Logo $dc $mark $markBounds (($size - $width) / 2) (($size - $height) / 2) $width '#435CEC' '#435CEC'
    }
    $frames += @{ Size = $size; Bytes = $bytes }
}
$stream = [IO.File]::Create((Join-Path $PSScriptRoot 'setup.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = $frame.Size % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }

foreach ($theme in @('light', 'dark')) {
    $ink = '#17191F'; $accent = '#435CEC'
    if ($theme -eq 'dark') { $ink = '#FFFFFF' }
    $bytes = Render-Png 192 192 {
        param($dc)
        Draw-Logo $dc $mark $markBounds 25 17 142 $ink $accent
    }
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "wizard-logo-$theme.png"), $bytes)

    $bytes = Render-Png 656 1256 {
        param($dc)
        $dc.PushTransform([Windows.Media.ScaleTransform]::new(4, 4))
        $background = [Windows.Media.LinearGradientBrush]::new()
        $background.StartPoint = [Windows.Point]::new(0, 0)
        $background.EndPoint = [Windows.Point]::new(1, 1)
        $top = '#F4F5FA'; $bottom = '#E8EBF6'
        if ($theme -eq 'dark') { $top = '#292B35'; $bottom = '#1E2028' }
        $background.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString($top), 0))
        $background.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString($bottom), 1))
        $dc.DrawRectangle($background, $null, [Windows.Rect]::new(0, 0, 164, 314))

        # Quiet light behind the mark and flowing outlines echo the app's layered surfaces.
        $glow = [Windows.Media.RadialGradientBrush]::new()
        $glow.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.ColorConverter]::ConvertFromString('#16435CEC'), 0))
        $glow.GradientStops.Add([Windows.Media.GradientStop]::new([Windows.Media.Colors]::Transparent, 1))
        $dc.DrawEllipse($glow, $null, [Windows.Point]::new(82, 155), 120, 130)
        $pen = [Windows.Media.Pen]::new((Brush '#18435CEC'), 0.65)
        foreach ($offset in @(0, 16, 32)) {
            $curve = 'M -25,{0} C 34,{1} 120,{2} 190,{3}' -f (245 + $offset), (171 + $offset), (292 + $offset), (209 + $offset)
            $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($curve))
        }
        Draw-Logo $dc $wordmark $textBounds 23 27 118 $ink $accent
        Draw-Logo $dc $mark $markBounds 45 113 74 $ink $accent
        $dc.Pop()
    }
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "wizard-panel-$theme.png"), $bytes)
}
Write-Host 'Rendered installer branding from the Scriblucent vector logo.'
