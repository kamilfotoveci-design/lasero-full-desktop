param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$solutionPath = Join-Path $projectRoot "LaseroDesktop.sln"
$projectPath = Join-Path $projectRoot "Lasero.App\Lasero.App.csproj"
$publishDir = Join-Path $projectRoot "artifacts\publish\win-x64-$Version"
$installerScript = Join-Path $projectRoot "installer\Lasero.iss"

if (-not $SkipTests) {
    Write-Host "[1/3] Spouštím automatické testy..." -ForegroundColor Cyan
    & dotnet test $solutionPath --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Automatické testy selhaly." }
}

Write-Host "[2/3] Vytvářím self-contained Windows build..." -ForegroundColor Cyan
& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publikování aplikace selhalo." }

$compilerCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) {
    throw "Inno Setup 6 nebyl nalezen. Nainstalujte balíček JRSoftware.InnoSetup přes winget."
}

Write-Host "[3/3] Sestavuji český instalační balíček..." -ForegroundColor Cyan
& $compiler "/DAppVersion=$Version" "/DPublishDir=$publishDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Sestavení instalačního balíčku selhalo." }

$setupPath = Join-Path $projectRoot "artifacts\installer\Lasero-Desktop-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Instalační balíček nebyl vytvořen." }

$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
Write-Host "" 
Write-Host "Hotovo:" -ForegroundColor Green
Write-Host $setupPath
Write-Host "SHA-256: $($hash.Hash)"
