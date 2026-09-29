param([int]$X,[int]$Y)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class MD {
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
$r = New-Object MD+RECT
[void][MD]::GetWindowRect($h,[ref]$r)
[void][MD]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 250
$sx = $r.L + $X; $sy = $r.T + $Y
[void][MD]::SetCursorPos($sx,$sy)
Start-Sleep -Milliseconds 150
[MD]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 40
[MD]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[MD]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
Start-Sleep -Milliseconds 40
[MD]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
"DBLCLICK AT $sx,$sy"
