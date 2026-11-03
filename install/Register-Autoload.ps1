<#
.SYNOPSIS
  Make BricsCAD load CSD-Bridge automatically at startup (current Windows user only).

.EXAMPLE
  .\Register-Autoload.ps1 -DllPath "C:\Tools\CsdBridge\V25\CsdBridge.BricsCAD.dll" -BricsCADVersion 25

.EXAMPLE
  .\Register-Autoload.ps1 -BricsCADVersion 25 -Remove

.NOTES
  Writes HKCU\Software\Bricsys\BricsCAD\V<ver>x64\<language>\Applications\CsdBridge
  with LOADER (DLL path), LOADCTRLS = 2 (load on startup) and MANAGED = 1 (.NET).
  This is the demand-load key format in the BricsCAD developer reference. Check the
  exact version key in regedit if your install differs (e.g. V26x64).
  Until then, NETLOAD the DLL by hand.
#>
param(
    [string]$DllPath,
    [int]$BricsCADVersion = 25,
    [string]$Language = "en_US",
    [switch]$Remove
)

$key = "HKCU:\Software\Bricsys\BricsCAD\V${BricsCADVersion}x64\$Language\Applications\CsdBridge"

if ($Remove) {
    if (Test-Path $key) { Remove-Item $key -Recurse; Write-Host "Removed $key" }
    else { Write-Host "Nothing to remove at $key" }
    return
}

if (-not $DllPath -or -not (Test-Path $DllPath)) {
    throw "Pass -DllPath with the full path to CsdBridge.BricsCAD.dll"
}

$parent = Split-Path $key
if (-not (Test-Path $parent)) {
    Write-Warning "$parent does not exist. Has BricsCAD V$BricsCADVersion ($Language) been started at least once for this user?"
}

New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name "LOADER" -Value (Resolve-Path $DllPath).Path -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name "DESCRIPTION" -Value "CSD-Bridge (Claude drafting assistant)" -PropertyType String -Force | Out-Null
Write-Host "Registered $key. Restart BricsCAD."
