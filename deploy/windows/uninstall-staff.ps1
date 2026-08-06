#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Uninstall Teamscop Staff Agent (TOTP gate via UninstallGuard when present).
#>
param(
    [string]$InstallRoot = "$env:ProgramData\Teamscop\Agent"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\ServiceInstallerHints.ps1"

$guard = Join-Path $InstallRoot "Teamscop.UninstallGuard.exe"
if (Test-Path -LiteralPath $guard) {
    Write-Host "Running UninstallGuard (admin TOTP required)…"
    $p = Start-Process -FilePath $guard -WorkingDirectory $InstallRoot -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "UninstallGuard failed (exit $($p.ExitCode)). Uninstall aborted."
    }
}
else {
    Write-Warning "UninstallGuard.exe not found — proceeding without TOTP gate (dev layout)."
}

Uninstall-TeamscopStaffService

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty -Path $runKey -Name "TeamscopSessionHelper" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $runKey -Name "TeamscopApp" -ErrorAction SilentlyContinue

$uninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TeamscopStaffAgent"
Remove-Item -Path $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    Write-Host "Removed $InstallRoot"
}

Write-Host "Teamscop Staff Agent uninstalled."
