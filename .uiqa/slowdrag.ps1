param([int]$X1,[int]$Y1,[int]$X2,[int]$Y2,[int]$Steps=30,[int]$DelayMs=25)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class M3 {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; } }
"@
$p = Get-Process Lasero.App | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$r = New-Object M3+RECT; [void][M3]::GetWindowRect($p.MainWindowHandle,[ref]$r)
[void][M3]::SetForegroundWindow($p.MainWindowHandle); Start-Sleep -Milliseconds 400
[void][M3]::SetCursorPos(($r.L+$X1),($r.T+$Y1)); Start-Sleep -Milliseconds 400
[M3]::mouse_event(0x02,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 300
for ($i=1; $i -le $Steps; $i++) {
  $x = $r.L + $X1 + [int](($X2-$X1)*$i/$Steps); $y = $r.T + $Y1 + [int](($Y2-$Y1)*$i/$Steps)
  [void][M3]::SetCursorPos($x,$y); Start-Sleep -Milliseconds $DelayMs
}
Start-Sleep -Milliseconds 300
[M3]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
"SLOWDRAG $X1,$Y1 -> $X2,$Y2"
