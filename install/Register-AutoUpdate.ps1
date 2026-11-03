<#
.SYNOPSIS
  Keep CSD-Bridge up to date automatically (current Windows user only).

.DESCRIPTION
  Creates a Windows scheduled task, "CSD-Bridge auto-update", that runs
  Update.ps1 -Unattended when you log in and once a day. Each run:
    - pulls the latest code; does nothing more if nothing changed
    - skips the update if BricsCAD has the add-in loaded (tries again next run)
    - otherwise builds and installs it; BricsCAD uses it from its next start
  Output goes to %LOCALAPPDATA%\CsdBridge\logs\update.log.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install\Register-AutoUpdate.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install\Register-AutoUpdate.ps1 -DailyAt 07:30

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install\Register-AutoUpdate.ps1 -Remove
#>
param(
    # Must match where the add-in is installed.
    [string]$InstallDir = "C:\CsdBridge",

    # Time of the daily run (24-hour). Runs when the PC is next on if it was off.
    [string]$DailyAt = "07:30",

    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$taskName = "CSD-Bridge auto-update"

if ($Remove) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Removed the scheduled task '$taskName'."
    }
    else {
        Write-Host "There is no scheduled task called '$taskName'."
    }

    return
}

$updateScript = Join-Path $PSScriptRoot "Update.ps1"
if (-not (Test-Path $updateScript)) { throw "Can't find $updateScript" }

$user = "$env:USERDOMAIN\$env:USERNAME"
$arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$updateScript`" -Unattended -InstallDir `"$InstallDir`""
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments -WorkingDirectory (Split-Path -Parent $PSScriptRoot)
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User $user),
    (New-ScheduledTaskTrigger -Daily -At $DailyAt)
)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
    -Description "Pulls, builds and installs the latest CSD-Bridge add-in for BricsCAD." -Force | Out-Null

Write-Host "Scheduled '$taskName': at log on and daily at $DailyAt."
Write-Host "Log: $env:LOCALAPPDATA\CsdBridge\logs\update.log"
Write-Host "Run it now with:  Start-ScheduledTask -TaskName '$taskName'"
