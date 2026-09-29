param([string]$Name, [string]$Window = "Lasero Desktop")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$root = [System.Windows.Automation.AutomationElement]::RootElement
$procs = @(Get-Process Lasero.App -ErrorAction SilentlyContinue)
if (-not $procs) { "NO_PROCESS"; exit 2 }
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procs[0].Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$win = $null
foreach ($w in $wins) { if ($w.Current.Name -like "*$Window*") { $win = $w } }
if (-not $win) { "NO_WINDOW: $Window"; exit 3 }

$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$els = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
"FOUND $($els.Count) named '$Name' in '$($win.Current.Name)'"
foreach ($el in $els) {
  if ($el.Current.IsOffscreen -or -not $el.Current.IsEnabled) { continue }
  try {
    $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
    "INVOKED [$($el.Current.ControlType.ProgrammaticName.Replace('ControlType.',''))] '$Name'"
    exit 0
  } catch { }
}
"NO_INVOKABLE: $Name"; exit 5
