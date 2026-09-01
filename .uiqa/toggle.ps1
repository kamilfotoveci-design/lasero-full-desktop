param([string]$Name, [string]$Window = "")
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
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
"found $($all.Count) element(s) named '$Name'"
foreach ($el in $all) {
  $type = $el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")
  $r = $el.Current.BoundingRectangle
  "  [$type] x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height)"
  foreach ($pattern in @('SelectionItem', 'Toggle', 'Invoke')) {
    try {
      switch ($pattern) {
        'SelectionItem' { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
        'Toggle'        { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        'Invoke'        { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
      }
      "  -> $pattern OK on [$type]"
      exit 0
    } catch {}
  }
}
"NO_ACTIONABLE_ELEMENT"
exit 4
