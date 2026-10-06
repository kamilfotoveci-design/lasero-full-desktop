<#
.SYNOPSIS
  Regenerates every brand-motion asset from the single vector source (Lasero.App/Controls/Motion).

.DESCRIPTION
  The animation is defined once, as code: IntroRenderer / PulseRenderer in Lasero.App/Controls/Motion
  draw any frame as a pure function of time. This script builds tools/motion/Lasero.MotionTool, which
  links those same sources, and uses it to render:

    Lasero.App/Assets/Motion/lasero-intro-1080.mp4     1920x1080 30 fps, 8 s, H.264 yuv420p faststart
    Lasero.App/Assets/Motion/lasero-intro-720.mp4      1280x720 lightweight variant (shipped with the app)
    Lasero.App/Assets/Motion/lasero-loop-1080.mp4      5 s seamless idle loop (breathing dot, travelling line)
    Lasero.App/Assets/Motion/lasero-loop-720.mp4       720p loop
    Lasero.App/Assets/Motion/lasero-intro-poster.png   hold frame, 1920x1080
    installer/assets/anim/*.bmp                        Inno Setup flip-book frames (8-bit)
    installer/assets/wizard-164|192|246.bmp            static wizard panel = the intro's hold frame

  Frames are piped straight into ffmpeg: nothing is written to disk besides the outputs. Needs ffmpeg
  (winget install Gyan.FFmpeg) and the .NET 8 SDK. Deterministic: same sources, same frames.

  Size budget: every committed video stays under 6 MB; the shipped 720p intro is about 1 MB. Raise
  -Crf to shrink, lower it for quality.

.EXAMPLE
  .\tools\motion\Render-Intro.ps1
  .\tools\motion\Render-Intro.ps1 -Only video       # skip installer frames
  .\tools\motion\Render-Intro.ps1 -Crf 22 -TempDir E:\tmp-motion
#>
param(
    [ValidateSet('all', 'video', 'installer', 'trace')][string]$Only = 'all',
    [int]$Crf = 14,
    [string]$Ffmpeg,
    [string]$TempDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $PSScriptRoot 'Lasero.MotionTool\Lasero.MotionTool.csproj'
$motion = Join-Path $root 'Lasero.App\Assets\Motion'
$tool = Join-Path $PSScriptRoot 'Lasero.MotionTool\bin\Release\net8.0-windows\Lasero.MotionTool.exe'

# Keep build intermediates and any scratch on the drive the caller chooses (drive C: can be nearly full).
if ($TempDir) {
    New-Item -ItemType Directory -Force $TempDir | Out-Null
    $env:TEMP = $TempDir; $env:TMP = $TempDir
}
$env:DOTNET_ROLL_FORWARD = 'Major'

if (-not $SkipBuild) {
Write-Host "Building motion tool..."
& dotnet build $project -c Release --nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
}

function Invoke-Tool([string[]]$ToolArgs) {
    if ($Ffmpeg) { $ToolArgs += @('--ffmpeg', $Ffmpeg) }
    # The tool is a WinExe (so the preview window has no console); Start-Process -Wait keeps the call synchronous.
    $out = Join-Path ([IO.Path]::GetTempPath()) ("motion-" + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process -FilePath $tool -ArgumentList $ToolArgs -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Get-Content $out; Get-Content "$out.err" -ErrorAction SilentlyContinue | ForEach-Object { Write-Warning $_ }
    Remove-Item $out, "$out.err" -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0) { throw "motion tool failed ($($p.ExitCode)): $($ToolArgs -join ' ')" }
}

if ($Only -in 'all', 'trace') {
    # Only needed when Assets/LaseroWordmark.png changes. Regenerates Controls/Motion/WordmarkData.cs.
    if ($Only -eq 'trace') {
        Invoke-Tool @('trace-wordmark', '--png', (Join-Path $root 'Lasero.App\Assets\LaseroWordmark.png'),
                      '--out', (Join-Path $root 'Lasero.App\Controls\Motion\WordmarkData.cs'))
    }
}

if ($Only -in 'all', 'video') {
    New-Item -ItemType Directory -Force $motion | Out-Null
    Invoke-Tool @('render-video', '--kind', 'intro', '--out', (Join-Path $motion 'lasero-intro-1080.mp4'), '--width', '1920', '--height', '1080', '--fps', '30', '--crf', $Crf)
    Invoke-Tool @('render-video', '--kind', 'intro', '--out', (Join-Path $motion 'lasero-intro-720.mp4'), '--width', '1280', '--height', '720', '--fps', '30', '--crf', ($Crf + 1))
    Invoke-Tool @('render-video', '--kind', 'loop', '--out', (Join-Path $motion 'lasero-loop-1080.mp4'), '--width', '1920', '--height', '1080', '--fps', '30', '--crf', $Crf)
    Invoke-Tool @('render-video', '--kind', 'loop', '--out', (Join-Path $motion 'lasero-loop-720.mp4'), '--width', '1280', '--height', '720', '--fps', '30', '--crf', ($Crf + 1))
    Invoke-Tool @('render-poster', '--out', (Join-Path $motion 'lasero-intro-poster.png'), '--width', '1920', '--height', '1080')
    Get-ChildItem $motion | ForEach-Object { '{0,-28} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB) } | Out-Host
}

if ($Only -in 'all', 'installer') {
    Invoke-Tool @('render-installer',
                  '--assets', (Join-Path $root 'installer\assets'),
                  '--logo', (Join-Path $root 'Lasero.AppAssetsasero.png'))
}

Write-Host 'Done.'
