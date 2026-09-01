param(
  [string]$Out = "screen.png",
  [int]$X = 0, [int]$Y = 0, [int]$W = 0, [int]$H = 0,
  [double]$Scale = 1
)
# Full-screen (or region) grab via CopyFromScreen. Use this instead of shot.ps1 whenever the thing
# you need is a tooltip, ContextMenu or Popup: those are separate top-level windows, so PrintWindow
# renders the main window without them.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($W -le 0) { $W = $bounds.Width - $X }
if ($H -le 0) { $H = $bounds.Height - $Y }
$shot = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($shot)
$g.CopyFromScreen($X, $Y, 0, 0, $shot.Size)
$g.Dispose()
if ($Scale -ne 1) {
  $tw = [int]($W * $Scale); $th = [int]($H * $Scale)
  $big = New-Object System.Drawing.Bitmap $tw, $th
  $bg = [System.Drawing.Graphics]::FromImage($big)
  $bg.InterpolationMode = 'HighQualityBicubic'
  $bg.DrawImage($shot, 0, 0, $tw, $th)
  $bg.Dispose(); $shot.Dispose(); $shot = $big
}
$shot.Save($Out)
"SAVED $Out ($($shot.Width)x$($shot.Height)) region $X,$Y ${W}x${H}"
$shot.Dispose()
