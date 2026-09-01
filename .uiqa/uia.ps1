param(
  [string]$Contains = "",
  [switch]$List
)
# UI Automation click by substring of the accessible name, matched with diacritics folded away.
# Two reasons this exists next to ui.ps1: PowerShell mangles Czech characters passed on the command
# line from a non-Windows shell, so -Name "Obnovit projekt" never matched; and UIA reaches windows
# that are not in the foreground, which coordinate clicks and CopyFromScreen cannot.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Fold([string]$s) {
  if (-not $s) { return "" }
  $n = $s.Normalize([Text.NormalizationForm]::FormD)
  $sb = New-Object Text.StringBuilder
  foreach ($ch in $n.ToCharArray()) {
    if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch) -ne 'NonSpacingMark') { [void]$sb.Append($ch) }
  }
  return $sb.ToString().ToLowerInvariant()
}
$procs = Get-Process Lasero.App -ErrorAction SilentlyContinue
if (-not $procs) { "NO_PROCESS"; exit 2 }
$root = [Windows.Automation.AutomationElement]::RootElement
$cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $procs[0].Id)
$windows = $root.FindAll([Windows.Automation.TreeScope]::Children, $cond)
$target = Fold $Contains
$hits = 0
foreach ($w in $windows) {
  $all = $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    $name = $e.Current.Name
    if ($List) {
      if ($name) { "{0,-22} {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $name }
      continue
    }
    if (-not $name -or (Fold $name) -notlike "*$target*") { continue }
    $hits++
    foreach ($pattern in @([Windows.Automation.InvokePattern]::Pattern, [Windows.Automation.SelectionItemPattern]::Pattern, [Windows.Automation.TogglePattern]::Pattern)) {
      $p = $null
      if ($e.TryGetCurrentPattern($pattern, [ref]$p)) {
        if ($p -is [Windows.Automation.InvokePattern]) { $p.Invoke() }
        elseif ($p -is [Windows.Automation.SelectionItemPattern]) { $p.Select() }
        else { $p.Toggle() }
        "INVOKED [$($e.Current.ControlType.ProgrammaticName)] $name"
        exit 0
      }
    }
  }
}
if ($List) { exit 0 }
if ($hits -gt 0) { "FOUND_BUT_NO_PATTERN ($hits)"; exit 3 }
"NOT_FOUND"; exit 4
