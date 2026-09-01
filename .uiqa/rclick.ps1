param([int]$X,[int]$Y)
# Right-click at window-relative coordinates. Popups and context menus are separate top-level
# windows, so PrintWindow (shot.ps1) cannot see them - capture those with screen.ps1.
Add-Type @"
using System;using System.Runtime.InteropServices;
public class MR {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$p = Get-Process Lasero.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "NO_WINDOW"; exit 2 }
$h = $p.MainWindowHandle
$r = New-Object MR+RECT
[void][MR]::GetWindowRect($h,[ref]$r)
[void][MR]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 250
$sx = $r.L + $X; $sy = $r.T + $Y
[void][MR]::SetCursorPos($sx,$sy)
Start-Sleep -Milliseconds 200
[MR]::mouse_event(0x08,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[MR]::mouse_event(0x10,0,0,0,[IntPtr]::Zero)
"RIGHT-CLICK AT $sx,$sy (win $($r.L),$($r.T))"
