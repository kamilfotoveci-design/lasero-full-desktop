param(
  [string]$Out = "shot.png",
  [string]$WindowTitle = "",
  [int]$Index = 0
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$procs = Get-Process Lasero.App -ErrorAction SilentlyContinue
if (-not $procs) { Write-Output "NO_PROCESS"; exit 2 }
$pids = @($procs.Id)
$found = New-Object System.Collections.ArrayList
$cb = [Win+EnumProc]{
  param($h, $p)
  $wpid = 0
  [void][Win]::GetWindowThreadProcessId($h, [ref]$wpid)
  if ($pids -contains $wpid -and [Win]::IsWindowVisible($h)) {
    $len = [Win]::GetWindowTextLength($h)
    $sb = New-Object System.Text.StringBuilder ($len + 2)
    [void][Win]::GetWindowText($h, $sb, $sb.Capacity)
    $r = New-Object Win+RECT
    [void][Win]::GetWindowRect($h, [ref]$r)
    if (($r.R - $r.L) -gt 200 -and ($r.B - $r.T) -gt 150) {
      [void]$found.Add([pscustomobject]@{H=$h; Title=$sb.ToString(); W=($r.R-$r.L); Ht=($r.B-$r.T)})
    }
  }
  return $true
}
[void][Win]::EnumWindows($cb, [IntPtr]::Zero)
if ($found.Count -eq 0) { Write-Output "NO_WINDOW"; exit 3 }
$found | ForEach-Object { Write-Output ("WINDOW: '" + $_.Title + "' " + $_.W + "x" + $_.Ht) }
$target = if ($WindowTitle) { $found | Where-Object { $_.Title -like "*$WindowTitle*" } | Select-Object -First 1 } else { $found[$Index] }
if (-not $target) { Write-Output "NO_MATCH"; exit 4 }
[void][Win]::SetForegroundWindow($target.H)
Start-Sleep -Milliseconds 700
$bmp = New-Object System.Drawing.Bitmap($target.W, $target.Ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][Win]::PrintWindow($target.H, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("SAVED " + $Out + " (" + $target.W + "x" + $target.Ht + ") from '" + $target.Title + "'")
