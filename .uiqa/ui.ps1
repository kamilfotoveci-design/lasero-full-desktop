param([string]$Action = "tree", [string]$Name = "", [int]$Depth = 6, [string]$Window = "")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$root = [System.Windows.Automation.AutomationElement]::RootElement
$procs = @(Get-Process Lasero.App -ErrorAction SilentlyContinue)
if (-not $procs) { "NO_PROCESS"; exit 2 }
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procs[0].Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
if ($wins.Count -eq 0) { "NO_WINDOW"; exit 3 }
$win = $wins[0]
if ($Window) { foreach ($w in $wins) { if ($w.Current.Name -like "*$Window*") { $win = $w } } }
foreach ($w in $wins) { "WIN: '" + $w.Current.Name + "'" }

function Walk($el, $lvl) {
  if ($lvl -gt $Depth) { return }
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $c = $walker.GetFirstChild($el)
  while ($c -ne $null) {
    $ci = $c.Current
    if ($ci.Name -or $ci.ControlType.ProgrammaticName -notlike "*.Pane") {
      ("  " * $lvl) + "[" + $ci.ControlType.ProgrammaticName.Replace("ControlType.","") + "] '" + $ci.Name + "'" + $(if($ci.IsOffscreen){" (offscreen)"}else{""}) + $(if(-not $ci.IsEnabled){" (disabled)"}else{""})
    }
    Walk $c ($lvl + 1)
    $c = $walker.GetNextSibling($c)
  }
}

if ($Action -eq "tree") { Walk $win 0; exit 0 }

if ($Action -eq "click") {
  $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
  $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
  if (-not $el) { "NOT_FOUND: $Name"; exit 4 }
  try {
    $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke(); "CLICKED: $Name"; exit 0
  } catch {}
  try {
    $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $p.Select(); "SELECTED: $Name"; exit 0
  } catch {}
  try {
    $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $p.Toggle(); "TOGGLED: $Name"; exit 0
  } catch {}
  "NO_PATTERN: $Name"; exit 5
}
