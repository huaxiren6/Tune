# Render an SVG glyph (Material Design icon) into a tray-icon ICO pair.
#   -on.ico  = white glyph  (music playing / unmuted)
#   -off.ico = red glyph    (muted)
# Design: dark rounded square background + glyph, visible on any taskbar.
param(
    [Parameter(Mandatory = $true)][string]$Svg,
    [Parameter(Mandatory = $true)][string]$OutBase
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

# ---- parse glyph geometry from SVG ----
$svgText = Get-Content $Svg -Raw
$dm = [regex]::Match($svgText, 'd="([^"]+)"')
if (-not $dm.Success) { throw 'no path d found in svg' }
$geo = [System.Windows.Media.Geometry]::Parse($dm.Groups[1].Value)
$geo.Freeze()

function Render-Glyph {
    param([int]$size, [string]$colorHex)
    $brush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.ColorConverter]::ConvertFromString($colorHex))
    $bgBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(235, 30, 31, 36))
    $vis = New-Object System.Windows.Media.DrawingVisual
    $dc = $vis.RenderOpen()
    $bgRect = New-Object System.Windows.Rect(0, 0, $size, $size)
    $radius = $size * 0.24
    $dc.DrawRoundedRectangle($bgBrush, $null, $bgRect, $radius, $radius)
    $b = $geo.Bounds
    $target = $size * 0.64
    $scale = $target / [Math]::Max($b.Width, $b.Height)
    $tx = ($size - $b.Width * $scale) / 2 - $b.X * $scale
    $ty = ($size - $b.Height * $scale) / 2 - $b.Y * $scale
    $mtx = New-Object System.Windows.Media.Matrix($scale, 0, 0, $scale, $tx, $ty)
    $tfm = New-Object System.Windows.Media.MatrixTransform($mtx)
    $dc.PushTransform($tfm)
    $dc.DrawGeometry($brush, $null, $geo)
    $dc.Pop()
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($vis)
    return $rtb
}

function Get-Pixels {
    param($rtb)
    $w = $rtb.PixelWidth
    $bytes = New-Object byte[] ($w * $w * 4)
    $rtb.CopyPixels($bytes, $w * 4, 0)
    return ,@($w, $bytes)
}

function New-IcoFile {
    param([string]$path, $frame16, $frame32)
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]2)
    $offset = 6 + 32
    foreach ($s in @($frame16, $frame32)) {
        $w = $s[0]; $bytes = $s[1]
        $andRowBytes = [int][Math]::Ceiling($w / 8.0)
        $dataSize = 40 + $bytes.Length + ($andRowBytes * $w)
        $bw.Write([byte]$w); $bw.Write([byte]$w)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$dataSize); $bw.Write([uint32]$offset)
        $offset += $dataSize
    }
    foreach ($s in @($frame16, $frame32)) {
        $w = $s[0]; $bytes = $s[1]
        $andRowBytes = [int][Math]::Ceiling($w / 8.0)
        $bw.Write([uint32]40)
        $bw.Write([int32]$w); $bw.Write([int32]($w * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]0)
        $bw.Write([int32]0); $bw.Write([int32]0)
        $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $w - 1; $y -ge 0; $y--) {
            $bw.Write($bytes, $y * $w * 4, $w * 4)
        }
        $zeros = New-Object byte[] ($andRowBytes * $w)
        $bw.Write($zeros)
    }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

$on16 = Render-Glyph 16 '#FFFFFF'
$on32 = Render-Glyph 32 '#FFFFFF'
$off16 = Render-Glyph 16 '#E84646'
$off32 = Render-Glyph 32 '#E84646'

New-IcoFile ($OutBase + '-on.ico')  (Get-Pixels $on16)  (Get-Pixels $on32)
New-IcoFile ($OutBase + '-off.ico') (Get-Pixels $off16) (Get-Pixels $off32)

# PNG previews for human inspection
$png16 = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$png16.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($on32))
$fsPng = [System.IO.File]::Create($OutBase + '-preview.png')
$png16.Save($fsPng); $fsPng.Close()

Write-Output ("generated: " + $OutBase + '-on.ico / -off.ico / -preview.png')
