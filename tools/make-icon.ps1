# Draws the app icon and writes it as a multi-size .ico: a rounded dark tile, like Windows 11 app
# icons, with a pulse line in the default Windows dark-theme accent (#60CDFF).
param([Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $d = 2 * $r
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $k = $size / 32.0
        $inset = [float]([math]::Max(0.5, 1 * $k))
        $tile = New-RoundedPath $inset $inset ([float]($size - 2 * $inset)) ([float]($size - 2 * $inset)) ([float](7 * $k))
        $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $size)),
            [System.Drawing.Color]::FromArgb(0xFF, 0x34, 0x34, 0x34), [System.Drawing.Color]::FromArgb(0xFF, 0x1C, 0x1C, 0x1C))
        $g.FillPath($fill, $tile)
        $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0x40, 0xFF, 0xFF, 0xFF), [float]([math]::Max(1, $k * 0.75)))
        $g.DrawPath($edge, $tile)
        $fill.Dispose(); $edge.Dispose(); $tile.Dispose()

        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0xFF, 0x60, 0xCD, 0xFF), [float](3 * $k))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $points = @(@(7, 17), @(11.5, 17), @(14, 10), @(18, 23), @(20.5, 17), @(25, 17)) |
            ForEach-Object { New-Object System.Drawing.PointF([float]($_[0] * $k), [float]($_[1] * $k)) }
        $g.DrawLines($pen, [System.Drawing.PointF[]] $points)
        $pen.Dispose()
    }
    finally { $g.Dispose() }
    $bmp
}

# Small sizes are stored as classic 32-bit bitmaps (readable by every tool); 256 px as PNG.
function Get-IconImageBytes([System.Drawing.Bitmap] $bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([int] 40); $w.Write([int] $size); $w.Write([int] ($size * 2))     # header; height covers image + mask
    $w.Write([int16] 1); $w.Write([int16] 32); $w.Write([int] 0); $w.Write([int] ($size * $size * 4))
    $w.Write([int] 0); $w.Write([int] 0); $w.Write([int] 0); $w.Write([int] 0)
    for ($y = $size - 1; $y -ge 0; $y--) {                                      # pixel rows run bottom-up
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte] $c.B); $w.Write([byte] $c.G); $w.Write([byte] $c.R); $w.Write([byte] $c.A)
        }
    }
    $maskRow = [int] ([math]::Ceiling($size / 32.0) * 4)                        # 1-bit mask, all opaque
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    try { , (Get-IconImageBytes $bmp) } finally { $bmp.Dispose() }
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$file = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($file)
try {
    $w.Write([int16] 0); $w.Write([int16] 1); $w.Write([int16] $sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }                # 0 means 256
        $w.Write([byte] $dim); $w.Write([byte] $dim); $w.Write([byte] 0); $w.Write([byte] 0)
        $w.Write([int16] 1); $w.Write([int16] 32); $w.Write([int] $images[$i].Length); $w.Write([int] $offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write([byte[]] $image) }
}
finally { $w.Dispose(); $file.Dispose() }
