<#
.SYNOPSIS
  Jedním příkazem sestaví instalační balíček LASERO Desktop.

.DESCRIPTION
  1. spustí automatické testy (lze přeskočit -SkipTests)
  2. publikuje self-contained win-x64 build (ReadyToRun, bez trimmingu)
  3. volitelně podepíše Lasero.App.exe
  4. přeloží Inno Setup instalátor (volitelně podepsaný)
  5. spočítá SHA-256 a zapíše SHA256SUMS a RELEASE-NOTES.txt vedle instalátoru

  Podepisování (volitelné), proměnné prostředí:
    LASERO_SIGN_PFX        cesta k certifikátu .pfx
    LASERO_SIGN_PASSWORD   heslo k certifikátu
    LASERO_SIGNTOOL        cesta k signtool.exe (jinak se hledá ve Windows SDK a v PATH)
    LASERO_SIGN_TIMESTAMP  adresa časového razítka (výchozí http://timestamp.digicert.com)
  Souběžné buildy (volitelné): LASERO_BUILDLOCK = adresář používaný jako zámek (vytvoří se mkdir,
  uvolní se vždy na konci).

.EXAMPLE
  .\build-installer.ps1 -Version 0.1.0
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests,
    [string]$OutDir,
    [switch]$NoReadyToRun
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$solutionPath = Join-Path $projectRoot "LaseroDesktop.sln"
$projectPath = Join-Path $projectRoot "Lasero.App\Lasero.App.csproj"
$installerScript = Join-Path $projectRoot "installer\Lasero.iss"
$publishDir = Join-Path $projectRoot "artifacts\publish\win-x64-$Version"
if (-not $OutDir) { $OutDir = Join-Path $projectRoot "artifacts\installer" }
$OutDir = [IO.Path]::GetFullPath($OutDir)

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.]+)?$') {
    throw "Neplatná verze '$Version'. Očekává se tvar 1.2.3 nebo 1.2.3-beta1."
}
$versionNumeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

# --- Inno Setup compiler (checked first, so a missing tool fails before the long steps) -----------
$compilerCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $compiler = $cmd.Source }
}
if (-not $compiler) {
    throw ("Inno Setup 6 nebyl nalezen (ISCC.exe). Nainstalujte jej příkazem:`n" +
           "  winget install --id JRSoftware.InnoSetup -e`n" +
           "a spusťte build znovu.")
}

# --- optional code signing ------------------------------------------------------------------------
$signPfx = $env:LASERO_SIGN_PFX
$signPassword = $env:LASERO_SIGN_PASSWORD
$signTool = $null
$signTimestamp = if ($env:LASERO_SIGN_TIMESTAMP) { $env:LASERO_SIGN_TIMESTAMP } else { "http://timestamp.digicert.com" }
$signing = [bool]$signPfx
if ($signing) {
    if (-not (Test-Path -LiteralPath $signPfx)) { throw "LASERO_SIGN_PFX ukazuje na neexistující soubor: $signPfx" }
    if (-not $signPassword) { throw "LASERO_SIGN_PFX je nastaveno, ale chybí LASERO_SIGN_PASSWORD." }
    $signTool = $env:LASERO_SIGNTOOL
    if (-not $signTool) {
        $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($sdk) { $signTool = $sdk.FullName }
        else { $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue; if ($cmd) { $signTool = $cmd.Source } }
    }
    if (-not $signTool -or -not (Test-Path -LiteralPath $signTool)) {
        throw "signtool.exe nebyl nalezen. Nainstalujte Windows SDK nebo nastavte LASERO_SIGNTOOL."
    }
}
else {
    Write-Warning ("Instalátor nebude podepsán (není nastaveno LASERO_SIGN_PFX). " +
                   "Windows SmartScreen při spuštění zobrazí varování; postup pro testery je v installer\README-testers.md.")
}

foreach ($required in "assets\wizard-164.bmp", "assets\small-55.bmp", "assets\lasero-setup.ico", "assets\lasero-uninstall.ico") {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "installer\$required"))) {
        throw "Chybí grafika instalátoru installer\$required. Vygenerujte ji: powershell -File installer\tools\New-InstallerArtwork.ps1"
    }
}

# --- optional build lock --------------------------------------------------------------------------
$lockPath = $env:LASERO_BUILDLOCK
$lockHeld = $false
function Enter-BuildLock {
    if (-not $lockPath) { return }
    Write-Host "Čekám na zámek buildu $lockPath ..." -ForegroundColor DarkGray
    while ($true) {
        try { New-Item -ItemType Directory -Path $lockPath -ErrorAction Stop | Out-Null; $script:lockHeld = $true; return }
        catch { Start-Sleep -Seconds 5 }
    }
}

function Invoke-Sign([string]$file) {
    & $signTool sign /f $signPfx /p $signPassword /fd sha256 /td sha256 /tr $signTimestamp $file | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Podepsání souboru selhalo: $file" }
}

$testSummary = "přeskočeno (-SkipTests)"
try {
    Enter-BuildLock

    if (-not $SkipTests) {
        Write-Host "[1/5] Spouštím automatické testy..." -ForegroundColor Cyan
        if (-not $env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD = "Major" }
        & dotnet test $solutionPath --configuration Release | Tee-Object -Variable testLines | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Automatické testy selhaly." }
        $passed = 0
        foreach ($line in $testLines) { if ("$line" -match 'Passed:\s+(\d+)') { $passed += [int]$Matches[1] } }
        $testSummary = "úspěšně prošlo $passed testů"
    }
    else { Write-Host "[1/5] Testy přeskočeny (-SkipTests)." -ForegroundColor Yellow }

    Write-Host "[2/5] Publikuji self-contained win-x64 build..." -ForegroundColor Cyan
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    $r2r = if ($NoReadyToRun) { "false" } else { "true" }
    & dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDir `
        -p:Version=$Version `
        -p:PublishReadyToRun=$r2r `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publikování aplikace selhalo (zkuste -NoReadyToRun, pokud selhala příprava ReadyToRun)." }
}
finally {
    if ($lockHeld) { Remove-Item -LiteralPath $lockPath -Force -Recurse -ErrorAction SilentlyContinue }
}

$appExe = Join-Path $publishDir "Lasero.App.exe"
if (-not (Test-Path -LiteralPath $appExe)) { throw "Publikovaný Lasero.App.exe nebyl nalezen." }

if ($signing) {
    Write-Host "[3/5] Podepisuji Lasero.App.exe..." -ForegroundColor Cyan
    Invoke-Sign $appExe
}
else { Write-Host "[3/5] Podepisování přeskočeno (bez certifikátu)." -ForegroundColor Yellow }

Write-Host "[4/5] Sestavuji instalační balíček (Inno Setup)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$isccArgs = @("/DAppVersion=$Version", "/DAppVersionNumeric=$versionNumeric", "/DPublishDir=$publishDir", "/O$OutDir")
if ($signing) {
    $q = '$q'
    $isccArgs += ("/Slasero=" + $q + $signTool + $q + " sign /f " + $q + $signPfx + $q + " /p " + $q + $signPassword + $q +
                  " /fd sha256 /td sha256 /tr " + $signTimestamp + ' $f')
    $isccArgs += "/DSignTool=1"
}
$isccArgs += $installerScript
& $compiler @isccArgs | Tee-Object -Variable isccLines | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Sestavení instalačního balíčku selhalo." }
$warnings = @($isccLines | Where-Object { "$_" -match '^\s*Warning:' })
if ($warnings.Count -gt 0) { Write-Warning "Inno Setup ohlásil $($warnings.Count) varování, viz výpis výše." }

$setupName = "Lasero-Desktop-Setup-$Version.exe"
$setupPath = Join-Path $OutDir $setupName
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Instalační balíček nebyl vytvořen: $setupPath" }

Write-Host "[5/5] Zapisuji kontrolní součty a poznámky k vydání..." -ForegroundColor Cyan
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $setupPath).Length
$utf8Bom = New-Object System.Text.UTF8Encoding $true
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

# sha256sum-compatible (hash, two spaces, file name)
[IO.File]::WriteAllText((Join-Path $OutDir "SHA256SUMS"), "$hash  $setupName`n", $utf8NoBom)

$commit = "neznámý"; $dirty = ""
try {
    $c = (& git -C $projectRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $c) { $commit = $c.Trim() }
    $st = (& git -C $projectRoot status --porcelain 2>$null)
    if ($st) { $dirty = " (s neuloženými změnami)" }
} catch { }
$signText = if ($signing) { "podepsáno certifikátem (soubory Lasero.App.exe a instalátor)" }
            else { "NEPODEPSÁNO - Windows SmartScreen zobrazí varování, postup viz README-testers.md" }
$sizeMb = [Math]::Round($size / 1MB, 1)
$notes = @"
LASERO Desktop $Version - poznámky k vydání (testovací verze)
=================================================================

Soubor:       $setupName
Velikost:     $size bajtů ($sizeMb MB)
SHA-256:      $hash
Sestaveno:    $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Zdroj:        commit $commit$dirty
Testy:        $testSummary
Podpis:       $signText
Požadavky:    Windows 10 verze 1809 nebo novější (x64), Windows 11. Samostatná instalace .NET není nutná.

Ověření kontrolního součtu (PowerShell):
  Get-FileHash .\$setupName -Algorithm SHA256
Výsledek se musí shodovat s hodnotou výše a se souborem SHA256SUMS.

Co je ve verzi
- návrh, import SVG/PNG/JPG a G-code, vrstvy, náhled dráhy, vzorník materiálů
- ovládání gravírek s řadičem GRBL přes sériový port, simulátor stroje pro vyzkoušení bez hardwaru
- ukládání projektů .lasero, záloha rozdělané práce, historie úloh

Známá omezení (poctivě)
- Kompatibilita s konkrétními modely strojů (AlgoLaser, Ortur, Two Trees, Creality a další) NENÍ fyzicky ověřena. Pojmenované modely zatím přímé připojení odmítají; ručně zvolený obecný GRBL není tvrzením o kompatibilitě.
- Výchozí rychlost a výkon řezu nejsou ověřeny pro žádnou kombinaci stroje a materiálu. Vždy potvrdit zkouškou na vzorku.
- Instalátor zatím neprošel čistou instalací, aktualizací a odinstalací na samostatném profilu Windows s plným přístupem k nabídce Start a registru.
- Soubory LightBurn .lbrn a .lbrn2 se zatím přímo neimportují.

Bezpečnost
Před první skutečnou úlohou vyzkoušet postup na odpadním kusu materiálu a mít nasazené ochranné brýle pro příslušnou vlnovou délku laseru. Tlačítko Stop v aplikaci není nouzové zastavení stroje.

Hlášení problémů
Kontakt, od kterého byl instalátor předán, případně https://lasero.net. Hodí se popis kroků, model stroje, firmware a soubory z %LOCALAPPDATA%\Lasero\logs.
"@
[IO.File]::WriteAllText((Join-Path $OutDir "RELEASE-NOTES.txt"), ($notes -replace "`r?`n", "`r`n"), $utf8Bom)

$readme = Join-Path $projectRoot "installer\README-testers.md"
if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination (Join-Path $OutDir "README-testers.md") -Force }

Write-Host ""
Write-Host "Hotovo:" -ForegroundColor Green
Write-Host $setupPath
Write-Host "Velikost: $size bajtů ($sizeMb MB)"
Write-Host "SHA-256:  $($hash.ToUpperInvariant())"
Write-Host "Podpis:   $signText"
