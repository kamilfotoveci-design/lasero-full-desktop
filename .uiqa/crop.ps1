param([string]$In,[string]$Out,[int]$X,[int]$Y,[int]$W,[int]$H,[double]$Scale=2)
Add-Type -AssemblyName System.Drawing
$src=[System.Drawing.Image]::FromFile($In)
$rw=[int]($W*$Scale); $rh=[int]($H*$Scale)
$bmp=New-Object System.Drawing.Bitmap($rw,$rh)
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.DrawImage($src,(New-Object System.Drawing.Rectangle(0,0,$rw,$rh)),(New-Object System.Drawing.Rectangle($X,$Y,$W,$H)),[System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $bmp.Save($Out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $src.Dispose()
"CROPPED $Out"
