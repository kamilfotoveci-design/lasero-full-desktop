Add-Type @"
using System;using System.Runtime.InteropServices;
public class RM {
 [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@
foreach ($vk in 0x11,0x10,0x12,0x5B) { [RM]::keybd_event([byte]$vk,0,2,[UIntPtr]::Zero) }
"MODS RELEASED"
