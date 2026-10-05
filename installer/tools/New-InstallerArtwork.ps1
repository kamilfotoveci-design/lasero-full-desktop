<#
 Reproducible generator for the installer artwork (System.Drawing, Windows PowerShell 5.1 or 7).
 Sources:  Lasero.App/Assets/Lasero.png, LaseroWordmark.png, Fonts/Inter-*.ttf
 Output:   installer/assets/  (committed)
   wizard-164.bmp wizard-192.bmp wizard-246.bmp      WizardImageFile   (164x314, 192x386, 246x459)
   small-55.bmp small-64.bmp small-80.bmp small-96.bmp  WizardSmallImageFile
   lasero-setup.ico      multi-size (16-256) Setup icon
   lasero-uninstall.ico  uninstall icon (inverted: warm-white tile, graphite L, red beam)
 Run:  powershell -ExecutionPolicy Bypass -File installer\tools\New-InstallerArtwork.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assets = Join-Path $root 'Lasero.App\Assets'
$out    = Join-Path $root 'installer\assets'
New-Item -ItemType Directory -Force $out | Out-Null

function C([string]$hex, [int]$a = 255) {
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0,2),16), [Convert]::ToInt32($hex.Substring(2,2),16), [Convert]::ToInt32($hex.Substring(4,2),16))
}
$Warm = C 'F7F7F5'; $Graphite = C '171918'; $Muted = C '666B68'; $Border = C 'E4E5E2'; $BorderStrong = C 'D2D4D0'; $Red = C 'FF0000'

$fonts = New-Object System.Drawing.Text.PrivateFontCollection
foreach ($f in 'Inter-Regular','Inter-SemiBold','Inter-Bold') { $fonts.AddFontFile((Join-Path $assets "Fonts\$f.ttf")) }
function InterFont([double]$px, [bool]$bold) {
    $fam = $fonts.Families | Where-Object { $_.Name -eq 'Inter' } | Select-Object -First 1
    if (-not $fam) { $fam = $fonts.Families[0] }
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    New-Object System.Drawing.Font($fam, [single]$px, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

# --- logo tile: crop the dark rounded square out of Lasero.png and square it -------------------
function Get-Tile {
    $src = New-Object System.Drawing.Bitmap (Join-Path $assets 'Lasero.png')
    $minX = $src.Width; $minY = $src.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $src.Height; $y += 2) { for ($x = 0; $x -lt $src.Width; $x += 2) {
        $p = $src.GetPixel($x, $y)
        if ($p.A -gt 128 -and ($p.R + $p.G + $p.B) -lt 200) {
            if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
        } } }
    $w = $maxX - $minX + 1; $h = $maxY - $minY + 1; $side = [Math]::Max($w, $h)
    $tile = New-Object System.Drawing.Bitmap($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($tile)
    $g.Clear([System.Drawing.Color]::Transparent)
    $dest = New-Object System.Drawing.Rectangle([int](($side - $w) / 2), [int](($side - $h) / 2), $w, $h)
    $g.DrawImage($src, $dest, $minX, $minY, $w, $h, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $src.Dispose(); $tile
}
$Tile = Get-Tile

function New-Canvas([int]$w, [int]$h) {
    $b = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    return @{ Bitmap = $b; G = $g }
}
function Resize-Image([System.Drawing.Image]$img, [int]$w, [int]$h) {
    $c = New-Canvas $w $h; $c.G.Clear([System.Drawing.Color]::Transparent)
    $c.G.DrawImage($img, 0, 0, $w, $h); $c.G.Dispose(); return $c.Bitmap
}
function Save-Bmp24([System.Drawing.Bitmap]$b, [string]$path, [System.Drawing.Color]$bg) {
    $flat = New-Object System.Drawing.Bitmap($b.Width, $b.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($flat); $g.Clear($bg); $g.DrawImage($b, 0, 0, $b.Width, $b.Height); $g.Dispose()
    $flat.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $flat.Dispose()
}
function New-RoundRect([double]$x,[double]$y,[double]$w,[double]$h,[double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = $r * 2
    $p.AddArc([single]$x,[single]$y,[single]$d,[single]$d,180,90); $p.AddArc([single]($x+$w-$d),[single]$y,[single]$d,[single]$d,270,90)
    $p.AddArc([single]($x+$w-$d),[single]($y+$h-$d),[single]$d,[single]$d,0,90); $p.AddArc([single]$x,[single]($y+$h-$d),[single]$d,[single]$d,90,90); $p.CloseFigure(); return $p
}

# --- wizard side panel (164x314 design units, rendered at 4x then downsampled) ----------------
function Render-Wizard([int]$W, [int]$H) {
    $ss = 4; $s = ($W * $ss) / 164.0; $dh = $H / ($W / 164.0)
    $c = New-Canvas ($W * $ss) ($H * $ss); $b = $c.Bitmap; $g = $c.G
    $g.Clear($Warm)
    # raster-line motif: engraving scan lines fading upward from the bottom
    for ($i = 0; $i -lt 14; $i++) {
        $y = $dh - 16 - $i * 3.4; $a = [int](150 * (1 - $i / 14.0))
        $pen = New-Object System.Drawing.Pen((C 'D2D4D0' $a), [single](0.6 * $s))
        $g.DrawLine($pen, [single](20*$s), [single]($y*$s), [single](144*$s), [single]($y*$s)); $pen.Dispose()
    }
    # right edge hairline
    $pen = New-Object System.Drawing.Pen($Border, [single](1 * $s)); $g.DrawLine($pen, [single](163.5*$s), 0, [single](163.5*$s), [single]($H*$ss)); $pen.Dispose()
    # logo tile
    $g.DrawImage($Tile, [single](20*$s), [single](26*$s), [single](38*$s), [single](38*$s))
    # wordmark
    $wm = New-Object System.Drawing.Bitmap (Join-Path $assets 'LaseroWordmark.png')
    $ww = 124 * $s; $wh = $ww * $wm.Height / $wm.Width
    $g.DrawImage($wm, [single](20*$s), [single](132*$s), [single]$ww, [single]$wh); $wm.Dispose()
    # etched line under wordmark: hairline with a red lead-in
    $y = 132 + $wh / $s + 16
    $pen = New-Object System.Drawing.Pen($BorderStrong, [single](1 * $s)); $g.DrawLine($pen, [single](20*$s), [single]($y*$s), [single](144*$s), [single]($y*$s)); $pen.Dispose()
    $pen = New-Object System.Drawing.Pen($Red, [single](2 * $s)); $g.DrawLine($pen, [single](20*$s), [single]($y*$s), [single](46*$s), [single]($y*$s)); $pen.Dispose()
    # product line, tagline, footer
    $f1 = InterFont (13*$s) $true; $f2 = InterFont (9*$s) $false; $f3 = InterFont (8*$s) $false
    $br = New-Object System.Drawing.SolidBrush($Graphite); $bm = New-Object System.Drawing.SolidBrush($Muted)
    $g.DrawString('Desktop', $f1, $br, [single](18.5*$s), [single](($y+8)*$s))
    $g.DrawString("Návrh`nMateriál`nGravírování", $f2, $bm, [single](19.5*$s), [single](($y+30)*$s))
    $g.DrawString('Testovací verze', $f3, $bm, [single](19.5*$s), [single](($dh - 26)*$s))
    $br.Dispose(); $bm.Dispose(); $f1.Dispose(); $f2.Dispose(); $f3.Dispose(); $g.Dispose()
    $r = Resize-Image $b $W $H; $b.Dispose(); return $r
}
foreach ($sz in @(@(164,314), @(192,386), @(246,459))) {
    $b = Render-Wizard $sz[0] $sz[1]; Save-Bmp24 $b (Join-Path $out ("wizard-{0}.bmp" -f $sz[0])) $Warm; $b.Dispose()
}

# --- small header image (square, sits on the white page header) ---------------------------------
foreach ($n in 55, 64, 80, 96) {
    $c = New-Canvas ($n*4) ($n*4); $g = $c.G; $g.Clear([System.Drawing.Color]::White)
    $m = $n * 4 * 0.10; $side = $n * 4 - 2 * $m
    $g.DrawImage($Tile, [single]$m, [single]$m, [single]$side, [single]$side); $g.Dispose()
    $r = Resize-Image $c.Bitmap $n $n; $c.Bitmap.Dispose()
    Save-Bmp24 $r (Join-Path $out "small-$n.bmp") ([System.Drawing.Color]::White); $r.Dispose()
}

# --- icons ----------------------------------------------------------------------------------------
function Render-UninstallTile {
    # inverted tile: warm-white square, graphite border, graphite L, red beam + dot
    $c = New-Canvas 1024 1024; $g = $c.G; $g.Clear([System.Drawing.Color]::Transparent)
    $p = New-RoundRect 40 40 944 944 150
    $g.FillPath((New-Object System.Drawing.SolidBrush($Warm)), $p)
    $pen = New-Object System.Drawing.Pen($Graphite, 56); $g.DrawPath($pen, $p)
    $gr = New-Object System.Drawing.SolidBrush($Graphite)
    $g.FillRectangle($gr, 300, 220, 140, 600); $g.FillRectangle($gr, 300, 700, 420, 120)       # L
    $g.FillRectangle($gr, 520, 220, 220, 60); $g.FillRectangle($gr, 560, 280, 140, 60)         # head
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($Red)), 616, 340, 28, 300)          # beam
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($Red)), 590, 610, 80, 80)            # dot
    $g.Dispose(); return $c.Bitmap
}
function Write-Ico([System.Drawing.Image]$srcTile, [string]$path) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256; $pngs = @()
    foreach ($n in $sizes) {
        $r = Resize-Image $srcTile $n $n; $ms = New-Object System.IO.MemoryStream
        $r.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,($ms.ToArray()); $r.Dispose()
    }
    $fs = [System.IO.File]::Create($path); $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $n = $sizes[$i]; $dim = if ($n -ge 256) { 0 } else { $n }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($d in $pngs) { $bw.Write($d) }
    $bw.Close(); $fs.Close()
}
Write-Ico $Tile (Join-Path $out 'lasero-setup.ico')
$u = Render-UninstallTile; Write-Ico $u (Join-Path $out 'lasero-uninstall.ico'); $u.Dispose()
$Tile.Dispose()
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
