# Draws the app icon (a yellow folder with a blue "i" badge) and writes src/IphoneMover/app.ico.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\IphoneMover\app.ico"

function New-IconBitmap([int]$size) {
    $s = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
        $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
        $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
        $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
        $p.CloseFigure()
        return $p
    }

    # folder back with tab
    $back = New-Object System.Drawing.Drawing2D.GraphicsPath
    $back.AddPolygon(@(
        (New-Object System.Drawing.PointF (16 * $s), (44 * $s)),
        (New-Object System.Drawing.PointF (96 * $s), (44 * $s)),
        (New-Object System.Drawing.PointF (116 * $s), (66 * $s)),
        (New-Object System.Drawing.PointF (240 * $s), (66 * $s)),
        (New-Object System.Drawing.PointF (240 * $s), (120 * $s)),
        (New-Object System.Drawing.PointF (16 * $s), (120 * $s))))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 214, 150, 20))), $back)

    # folder front
    $front = RoundRect (16 * $s) (80 * $s) (224 * $s) (152 * $s) (14 * $s)
    $rect = New-Object System.Drawing.RectangleF (16 * $s), (80 * $s), (224 * $s), (152 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 255, 213, 79)), ([System.Drawing.Color]::FromArgb(255, 245, 176, 30)), 90.0
    $g.FillPath($grad, $front)

    # blue circle with a white "i"
    $cx = 128 * $s; $cy = 158 * $s; $r = 58 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 21, 101, 192))), $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $dot = 15 * $s
    $g.FillEllipse($white, $cx - $dot, $cy - 42 * $s, 2 * $dot, 2 * $dot)
    $stem = RoundRect ($cx - 13 * $s) ($cy - 6 * $s) (26 * $s) (52 * $s) (6 * $s)
    $g.FillPath($white, $stem)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO file: header, one directory entry per size, then the PNG data.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# Preview PNG for the README.
$preview = New-IconBitmap 256
$preview.Save((Join-Path $PSScriptRoot "..\docs\icon.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
"wrote $out"
