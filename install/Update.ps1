<#
.SYNOPSIS
  Update CSD-Bridge in BricsCAD: get the latest code, build it, and install it.

.DESCRIPTION
  Run from the repo folder:

    powershell -ExecutionPolicy Bypass -File install\Update.ps1

  1. git pull (fast-forward only) in the repo this script lives in.
  2. Builds the add-in for the newest BricsCAD installed (or -BricsCADVersion).
     BricsCAD can stay open while this runs.
  3. If the installed add-in is in use, waits until you close BricsCAD (Windows
     locks a loaded add-in), then copies the build to -InstallDir.

  Not touched: your API key and settings.json (%APPDATA%\CsdBridge). If the
  installed office-standards.md, road-drafting.json or turn-paths.json was edited and differs from
  the repo copy, the installed one is saved as .bak before it is replaced.

.EXAMPLE
  .\install\Update.ps1

.EXAMPLE
  .\install\Update.ps1 -BricsCADVersion 25 -RegisterAutoload

.EXAMPLE
  .\install\Update.ps1 -Unattended
  For scheduled runs (see Register-AutoUpdate.ps1): never prompts, does nothing if
  already up to date, skips the update if BricsCAD has the add-in loaded (it will
  try again next time), and logs to %LOCALAPPDATA%\CsdBridge\logs\update.log.
#>
[CmdletBinding()]
param(
    # Where BricsCAD loads the add-in from.
    [string]$InstallDir = "C:\CsdBridge",

    # Build for this BricsCAD version instead of the newest one installed.
    [int]$BricsCADVersion,

    # BricsCAD install folder, if it is not under C:\Program Files\Bricsys.
    [string]$BricsCADDir,

    # Build and install what is already checked out, without git pull.
    [switch]$SkipPull,

    # Also make BricsCAD load the add-in at startup (runs Register-Autoload.ps1).
    [switch]$RegisterAutoload,

    # For scheduled runs: no prompts, skip if up to date or if BricsCAD is using the add-in.
    [switch]$Unattended
)

$ErrorActionPreference = "Stop"

# A fresh .NET SDK prints a one-time welcome banner; keep it out of the output we parse.
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$repo = Split-Path -Parent $PSScriptRoot
$stampName = "installed-commit.txt"

if ($Unattended) {
    $logRoot = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [IO.Path]::GetTempPath() }
    $logDir = [IO.Path]::Combine($logRoot, "CsdBridge", "logs")
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    Start-Transcript -Path (Join-Path $logDir "update.log") -Append | Out-Null
    Write-Host "CSD-Bridge unattended update, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
}
$project = [IO.Path]::Combine($repo, "src", "CsdBridge.BricsCAD", "CsdBridge.BricsCAD.csproj")

function Write-Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

function Stop-Update([string]$text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Red
    exit 1
}

function Stop-Quietly([string]$text) {
    Write-Host $text
    exit 0
}

# The commit the repo is on, or $null if this isn't a git checkout.
function Get-Commit {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $commit = & git -C $repo rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $commit) { return ([string]$commit).Trim() }
    }
    catch {
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return $null
}

function Test-FileLocked([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Close()
        return $false
    }
    catch {
        return $true
    }
}

if (-not (Test-Path $project)) { Stop-Update "Can't find $project. Run this script from the CSD-Bridge repo." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Stop-Update "The .NET SDK (dotnet) is not installed or not on PATH." }

# Extra build settings passed through to the project.
$buildProps = @()
if ($BricsCADVersion) { $buildProps += "-p:BricsCADVersion=$BricsCADVersion" }
if ($BricsCADDir) { $buildProps += "-p:BricsCADDir=$BricsCADDir" }

# ---------------------------------------------------------------- 1. pull
if (-not $SkipPull) {
    Write-Step "Getting the latest code"
    $branch = (& git -C $repo rev-parse --abbrev-ref HEAD)
    & git -C $repo pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        Stop-Update "git pull failed (see above). If you changed files in the repo, commit or undo them, then run this again. Your installed add-in is unchanged."
    }
    Write-Host "Branch: $branch"
}

$commit = Get-Commit
$installedDll = Join-Path $InstallDir "CsdBridge.BricsCAD.dll"
$stamp = Join-Path $InstallDir $stampName
if ($Unattended) {
    if ($commit -and (Test-Path $installedDll) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $commit)) {
        Stop-Quietly "Already up to date ($($commit.Substring(0, 7)))."
    }

    if (Test-FileLocked $installedDll) {
        Stop-Quietly "BricsCAD has the add-in loaded, so it can't be replaced now. Will try again next time."
    }
}

# ---------------------------------------------------------------- 2. build
Write-Step "Finding BricsCAD"
$evaluation = (& dotnet msbuild $project -nologo "-getProperty:BricsCADVersion" "-getProperty:BricsCADDir" @buildProps) | Out-String
if ($LASTEXITCODE -ne 0) { Stop-Update "Could not read the project settings:`n$evaluation" }
# Parse only the JSON part, in case anything else was printed before or after it.
$jsonStart = $evaluation.IndexOf("{")
$jsonEnd = $evaluation.LastIndexOf("}")
if ($jsonStart -lt 0 -or $jsonEnd -lt $jsonStart) { Stop-Update "Could not read the project settings:`n$evaluation" }
$found = ($evaluation.Substring($jsonStart, $jsonEnd - $jsonStart + 1) | ConvertFrom-Json).Properties
$version = $found.BricsCADVersion
if ($found.BricsCADDir) { Write-Host "BricsCAD V$version at $($found.BricsCADDir)" }

Write-Step "Building for BricsCAD V$version"
& dotnet build $project -c Release -nologo @buildProps
if ($LASTEXITCODE -ne 0) { Stop-Update "Build failed (see the errors above). Nothing was copied; your installed add-in is unchanged." }

$outDir = [IO.Path]::Combine($repo, "src", "CsdBridge.BricsCAD", "bin", "Release", "V$version")
if (-not (Test-Path (Join-Path $outDir "CsdBridge.BricsCAD.dll"))) {
    Stop-Update "The build finished but CsdBridge.BricsCAD.dll is not in $outDir."
}

# ---------------------------------------------------------------- 3. install
Write-Step "Installing to $InstallDir"
if ($Unattended -and (Test-FileLocked $installedDll)) {
    Stop-Quietly "BricsCAD was started during the build, so the add-in can't be replaced now. Will try again next time."
}

while (Test-FileLocked $installedDll) {
    Write-Host "The add-in is in use. Close BricsCAD, then press Enter (Ctrl+C to stop)." -ForegroundColor Yellow
    [void](Read-Host)
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

foreach ($pair in @(@("office-standards.md", "standards_file"), @("road-drafting.json", "road_drafting_file"), @("turn-paths.json", "turn_paths_file"))) {
    $file = $pair[0]
    $installed = Join-Path $InstallDir $file
    $new = Join-Path $outDir $file
    if ((Test-Path $installed) -and (Test-Path $new) -and
        ((Get-FileHash $installed).Hash -ne (Get-FileHash $new).Hash)) {
        Copy-Item $installed "$installed.bak" -Force
        Write-Host "The installed $file was different from the repo copy, so it was saved as $file.bak." -ForegroundColor Yellow
        Write-Host "To keep your edits across updates, put them in src\CsdBridge.BricsCAD\$file in the repo, or set $($pair[1]) in settings.json." -ForegroundColor Yellow
    }
}

try {
    Copy-Item -Path (Join-Path $outDir "*") -Destination $InstallDir -Recurse -Force
}
catch {
    Stop-Update "Copying failed: $($_.Exception.Message). If BricsCAD is open, close it and run this again."
}

if ($commit) {
    Set-Content -Path $stamp -Value $commit
}

if ($RegisterAutoload) {
    Write-Step "Registering autoload"
    & (Join-Path $PSScriptRoot "Register-Autoload.ps1") -DllPath $installedDll -BricsCADVersion $version
}

Write-Host ""
Write-Host "Done: CSD-Bridge for BricsCAD V$version is installed in $InstallDir." -ForegroundColor Green
Write-Host "Start BricsCAD. If it doesn't load automatically, run NETLOAD and pick $installedDll"
