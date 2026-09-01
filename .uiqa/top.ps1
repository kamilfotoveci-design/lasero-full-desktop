param([switch]$Off)
# Bring the app window to the front and pin it there for a QA pass.
#
# Two things are needed and neither is optional. SetWindowPos with HWND_TOPMOST keeps a maximised
# browser from covering the window between the click that opens a popup and the CopyFromScreen meant
# to capture it. SetForegroundWindow on its own is refused when the calling process is not already in
# the foreground, which is exactly our case - the tapped ALT keystroke first is the documented way to
# make Windows honour it. Without the front-most part, shot.ps1's PrintWindow can also come back
# blank for an occluded window, which looks like a broken UI rather than a broken screenshot.
Add-Type @"
using System;using System.Runtime.InteropServices;
public class T {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint f);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk,byte scan,uint flags,IntPtr extra);
}
"@
$p = Get-Process Lasero.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "NO_WINDOW"; exit 2 }
$h = $p.MainWindowHandle
$after = if ($Off) { [IntPtr](-2) } else { [IntPtr](-1) }   # HWND_NOTOPMOST : HWND_TOPMOST
[void][T]::SetWindowPos($h, $after, 0, 0, 0, 0, 0x0001 -bor 0x0002)  # NOSIZE|NOMOVE
if (-not $Off) {
  [void][T]::ShowWindow($h, 9)          # SW_RESTORE
  [T]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)        # ALT down
  [T]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)        # ALT up
  [void][T]::BringWindowToTop($h)
  [void][T]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 350
}
if ($Off) { "TOPMOST OFF" } else { "FRONT + TOPMOST" }
