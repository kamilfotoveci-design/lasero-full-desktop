param([string]$Key)
Add-Type @"
using System;using System.Runtime.InteropServices;
public class KP {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@
$p = Get-Process Lasero.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "NO_WINDOW"; exit 2 }
[void][KP]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 200
$vk = switch ($Key) {
  "Delete" { 0x2E }
  "Escape" { 0x1B }
  "Enter" { 0x0D }
  default { 0 }
}
[KP]::keybd_event([byte]$vk,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 50
[KP]::keybd_event([byte]$vk,0,2,[UIntPtr]::Zero)
"KEY $Key sent"
