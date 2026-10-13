param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$WinAppPath = "winapp",
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot "TestResults\UI")
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()

New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$windowTree = (& $WinAppPath ui inspect -a $AppPid --json 2>$null | Out-String) | ConvertFrom-Json
$appWindow = @($windowTree.windows) | Where-Object { $_.title -eq "Lasero Desktop" } | Select-Object -First 1
if ($null -eq $appWindow) { throw "Lasero Desktop window was not found for PID $AppPid." }
$appHwnd = [long]$appWindow.hwnd

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = "PASS" }
        } else {
            $script:fail++
            $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
        }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

function Find-AutomationElement {
    param($Node, [string]$AutomationId)
    if ($null -eq $Node) { return $null }
    if ($Node.automationId -eq $AutomationId) { return $Node }
    foreach ($child in @($Node.children)) {
        $match = Find-AutomationElement $child $AutomationId
        if ($null -ne $match) { return $match }
    }
    return $null
}

Test-UI "Home navigation exists" {
    & $WinAppPath ui wait-for "NavHome" -w $appHwnd -t 5000
}
Test-UI "Home is visible" {
    & $WinAppPath ui wait-for "HomeHeading" -w $appHwnd -t 3000
}
Test-UI "Project picker action is accessible" {
    & $WinAppPath ui wait-for "OpenProjectButton" -w $appHwnd -t 3000
}

& $WinAppPath ui invoke "NavHome" -w $appHwnd 2>$null
& $WinAppPath ui screenshot -w $appHwnd -o (Join-Path $ArtifactDirectory "01-home.png") --focus 2>$null

Test-UI "Open editor" {
    & $WinAppPath ui invoke "NavDesign" -w $appHwnd
}
Test-UI "Editor canvas is accessible" {
    & $WinAppPath ui wait-for "DesignerCanvas" -w $appHwnd -t 3000
}
Test-UI "Rectangle tool draws a Core object" {
    # Mouse gestures require the target window to own the foreground. A real click both selects the
    # tool and establishes that ownership before the drag, matching the user's interaction path.
    & $WinAppPath ui click "ToolRectangle" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }

    $tree = (& $WinAppPath ui inspect -w $appHwnd --json 2>$null | Out-String) | ConvertFrom-Json
    $canvas = $null
    foreach ($window in @($tree.windows)) {
        foreach ($element in @($window.elements)) {
            $canvas = Find-AutomationElement $element "DesignerCanvas"
            if ($null -ne $canvas) { break }
        }
        if ($null -ne $canvas) { break }
    }
    if ($null -eq $canvas) { throw "DesignerCanvas bounds were not found." }

    $startX = [int]($canvas.x + $canvas.width * 0.30)
    $startY = [int]($canvas.y + $canvas.height * 0.30)
    $endX = [int]($canvas.x + $canvas.width * 0.50)
    $endY = [int]($canvas.y + $canvas.height * 0.50)
    & $WinAppPath ui drag "$startX,$startY" "$endX,$endY" -w $appHwnd --dwell-ms 150
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "SelectedObjectName" -w $appHwnd --value "Obdélník" -t 3000
}
Test-UI "Editor zoom works" {
    & $WinAppPath ui invoke "ZoomReset" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui invoke "ZoomIn" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "ZoomReset" -w $appHwnd --value "120%" -t 3000
}

& $WinAppPath ui screenshot -w $appHwnd -o (Join-Path $ArtifactDirectory "02-editor.png") --focus 2>$null

Test-UI "Leave editor through its rail" {
    & $WinAppPath ui invoke "EditorHome" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "HomeHeading" -w $appHwnd -t 3000
}

Test-UI "Open materials" {
    & $WinAppPath ui invoke "NavMaterials" -w $appHwnd
}
Test-UI "Materials page loaded" {
    & $WinAppPath ui wait-for "MaterialsHeading" -w $appHwnd -t 3000
}
Test-UI "Material filters are accessible" {
    & $WinAppPath ui wait-for "TechnologyFilter" -w $appHwnd -t 3000
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "PowerFilter" -w $appHwnd -t 3000
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "OperationFilter" -w $appHwnd -t 3000
}
Test-UI "Material search accepts text" {
    & $WinAppPath ui set-value "MaterialsSearch" "Překližka" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "MaterialsSearch" -w $appHwnd --value "Překližka" -t 3000
}

& $WinAppPath ui screenshot -w $appHwnd -o (Join-Path $ArtifactDirectory "03-materials.png") --focus 2>$null

Test-UI "Return home" {
    & $WinAppPath ui invoke "NavHome" -w $appHwnd
    if ($LASTEXITCODE -ne 0) { return }
    & $WinAppPath ui wait-for "HomeHeading" -w $appHwnd -t 3000
}

& $WinAppPath ui screenshot -w $appHwnd -o (Join-Path $ArtifactDirectory "04-home-return.png") --focus 2>$null

$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "test-results.json")
Write-Host "Passed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
    Write-Host "FAIL: $($_.name) - $($_.detail)"
}

if ($fail -gt 0) { exit 1 }
