param([int]$X,[int]$Y,[string]$Move="")
Add-Type @"
using System;using System.Runtime.InteropServices;
public class M {
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
$r = New-Object M+RECT
[void][M]::GetWindowRect($h,[ref]$r)
[void][M]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 250
$sx = $r.L + $X; $sy = $r.T + $Y
[void][M]::SetCursorPos($sx,$sy)
Start-Sleep -Milliseconds 200
if ($Move -ne "hover") {
  [M]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [M]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
}
"AT $sx,$sy (win $($r.L),$($r.T)) $Move"
