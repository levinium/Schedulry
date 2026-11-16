# Renders the Schedulry app icon, in the same family as Tenantry and Contactry: a midnight tile with
# a 3x3 grid of cells (here read as a calendar), lit as an S. The centre cell is a clock face.
# Writes
#   src/Schedulry.App/Assets/schedulry.ico       Windows executable and window icon (16-256 px)
#   src/Schedulry.App/Assets/schedulry-256.png   in-app logo
#   src/Schedulry.App/Assets/schedulry-1024.png  master for packaging
# Run from any folder with PowerShell 7 on Windows (uses System.Drawing):
#   pwsh -File tools/icon/render-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\..\src\Schedulry.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Fills a rounded rect given in unit (0..1) coordinates.
function Add-UnitRect($graphics, $brush, $size, $x, $y, $w, $h, $r) {
    $path = New-RoundRect ($x * $size) ($y * $size) ($w * $size) ($h * $size) ($r * $size)
    $graphics.FillPath($brush, $path)
    $path.Dispose()
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # Background: the family's rounded tile, diagonal midnight gradient, soft inner rim that holds
    # the edge against dark taskbars.
    $tile = New-RoundRect 0 0 $size $size ($size * 0.22)
    $top = [System.Drawing.ColorTranslator]::FromHtml('#2A3350')
    $bottom = [System.Drawing.ColorTranslator]::FromHtml('#0F1424')
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), $top, $bottom
    $graphics.FillPath($gradient, $tile)
    $rimWidth = [Math]::Max(1, $size * 0.025)
    $inset = $rimWidth / 2 + $size * 0.01
    $rim = New-RoundRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ($size * 0.2)
    $rimPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), $rimWidth
    $graphics.DrawPath($rimPen, $rim)

    # Grid lit as an S: the top row's right two cells, the centre, the bottom row's left two.
    # The whole stroke is bright (a fade made the ends blend into the unlit cells); unlit cells stay faint.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $arm = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 255, 255, 255))
    $tip = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
    $faint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(45, 255, 255, 255))
    $cell = 0.17; $gap = 0.045; $origin = 0.2; $radius = $cell * 0.2
    $shades = @(
        @($faint, $arm, $arm),
        @($faint, $white, $faint),
        @($arm, $arm, $faint)
    )
    for ($row = 0; $row -lt 3; $row++) {
        for ($col = 0; $col -lt 3; $col++) {
            Add-UnitRect $graphics $shades[$row][$col] $size ($origin + $col * ($cell + $gap)) ($origin + $row * ($cell + $gap)) $cell $cell $radius
        }
    }

    # Clock hands in the centre cell, in the tile's midnight colour. Dropped below 32 px, where
    # they would only smudge the cell.
    if ($size -ge 32) {
        $cx = ($origin + $cell + $gap + $cell / 2) * $size
        $cy = $cx
        $handPen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#1A2140')), ([Math]::Max(1.5, $size * 0.022))
        $handPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $handPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($handPen, $cx, $cy, $cx, $cy - $cell * $size * 0.33)          # minute hand, to 12
        $graphics.DrawLine($handPen, $cx, $cy, $cx + $cell * $size * 0.24, $cy)          # hour hand, to 3
        $handPen.Dispose()
    }

    $white.Dispose(); $arm.Dispose(); $tip.Dispose(); $faint.Dispose(); $rimPen.Dispose(); $rim.Dispose(); $gradient.Dispose(); $tile.Dispose()
    $graphics.Dispose()
    return $bmp
}

function Get-PngBytes([int]$size) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $stream.ToArray()
}

# .ico with PNG-compressed entries (supported since Windows Vista), one per size.
function Write-Ico([string]$path, [int[]]$sizes) {
    $images = foreach ($size in $sizes) { , (Get-PngBytes $size) }
    $file = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $file
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
    $writer.Dispose()
}

Write-Ico (Join-Path $assets 'schedulry.ico') @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in 256, 1024) {
    [System.IO.File]::WriteAllBytes((Join-Path $assets "schedulry-$size.png"), (Get-PngBytes $size))
}
"wrote icon assets to $((Resolve-Path $assets).Path)"
