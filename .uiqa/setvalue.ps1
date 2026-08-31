param([string]$Name, [string]$Value, [string]$Window = "")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$root = [System.Windows.Automation.AutomationElement]::RootElement
$procs = @(Get-Process Lasero.App -ErrorAction SilentlyContinue)
if (-not $procs) { "NO_PROCESS"; exit 2 }
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procs[0].Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
if ($wins.Count -eq 0) { "NO_WINDOW"; exit 3 }
$win = $wins[0]
if ($Window) { foreach ($w in $wins) { if ($w.Current.Name -like "*$Window*") { $win = $w } } }
$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
if (-not $el) { "NOT_FOUND: $Name"; exit 4 }
$vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($Value)
"SET '$Name' = '$Value'"
