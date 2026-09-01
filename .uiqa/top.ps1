param([switch]$Off)
# Pin the app window above everything for the duration of a QA pass. Without this, a browser or an
# update toast steals the foreground between the click that opens a popup and the CopyFromScreen that
# was meant to capture it, and you get a screenshot of the wrong application.
Add-Type @"
using System;using System.Runtime.InteropServices;
public class T {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint f);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$p = Get-Process Lasero.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "NO_WINDOW"; exit 2 }
$after = if ($Off) { [IntPtr](-2) } else { [IntPtr](-1) }   # HWND_NOTOPMOST : HWND_TOPMOST
[void][T]::SetWindowPos($p.MainWindowHandle, $after, 0, 0, 0, 0, 0x0001 -bor 0x0002)  # NOSIZE|NOMOVE
[void][T]::SetForegroundWindow($p.MainWindowHandle)
if ($Off) { "TOPMOST OFF" } else { "TOPMOST ON" }
