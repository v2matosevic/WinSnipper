# Generates assets/icon.ico — blue circle, white viewfinder brackets, red dot —
# at 16/24/32/48/64/128/256 px, each entry PNG-compressed.
Add-Type -AssemblyName System.Drawing

function New-IconPngBytes([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $s = $size / 32.0

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 105, 220))
    $g.FillEllipse($bg, 1 * $s, 1 * $s, 30 * $s, 30 * $s)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (2.4 * $s)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    function pt([double]$x, [double]$y) { New-Object System.Drawing.PointF ($x * $s), ($y * $s) }

    $g.DrawLines($pen, [System.Drawing.PointF[]]@((pt 9 13), (pt 9 9), (pt 13 9)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((pt 19 9), (pt 23 9), (pt 23 13)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((pt 23 19), (pt 23 23), (pt 19 23)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((pt 13 23), (pt 9 23), (pt 9 19)))

    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 90, 80))
    $g.FillEllipse($dot, 13.5 * $s, 13.5 * $s, 5 * $s, 5 * $s)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (New-IconPngBytes $sz) }

$out = Join-Path $PSScriptRoot "..\assets\icon.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$msOut = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $msOut

# ICONDIR
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)

# ICONDIRENTRYs
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim)        # width
    $w.Write([byte]$dim)        # height
    $w.Write([byte]0)           # palette
    $w.Write([byte]0)           # reserved
    $w.Write([uint16]1)         # planes
    $w.Write([uint16]32)        # bpp
    $w.Write([uint32]$images[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }

[System.IO.File]::WriteAllBytes($out, $msOut.ToArray())
$w.Dispose()
"Wrote $out ($((Get-Item $out).Length) bytes)"
