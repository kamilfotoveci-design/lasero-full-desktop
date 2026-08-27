param([int]$X1,[int]$Y1,[int]$X2,[int]$Y2)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class M2 {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
$p = Get-Process Lasero.App | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$r = New-Object M2+RECT; [void][M2]::GetWindowRect($p.MainWindowHandle,[ref]$r)
[void][M2]::SetForegroundWindow($p.MainWindowHandle); Start-Sleep -Milliseconds 250
[void][M2]::SetCursorPos(($r.L+$X1),($r.T+$Y1)); Start-Sleep -Milliseconds 150
[M2]::mouse_event(0x02,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 120
for ($i=1; $i -le 12; $i++) {
  $x = $r.L + $X1 + [int](($X2-$X1)*$i/12); $y = $r.T + $Y1 + [int](($Y2-$Y1)*$i/12)
  [void][M2]::SetCursorPos($x,$y); Start-Sleep -Milliseconds 35
}
Start-Sleep -Milliseconds 120
[M2]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
"DRAG done"
