param([int]$W=1366,[int]$H=768,[int]$X=0,[int]$Y=0)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class R { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool rp); }
"@
$p = Get-Process Lasero.App | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
[void][R]::MoveWindow($p.MainWindowHandle,$X,$Y,$W,$H,$true)
"RESIZED to ${W}x${H}"
