Add-Type @"
using System;using System.Runtime.InteropServices;
public class CZ {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@
$p = Get-Process Lasero.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "NO_WINDOW"; exit 2 }
[void][CZ]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 200
# Ctrl down
[CZ]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[CZ]::keybd_event(0x5A,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[CZ]::keybd_event(0x5A,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[CZ]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
"CTRL+Z sent"
