#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install Teamscop Staff Agent: Windows Service + SessionHelper + App (Run at logon).
.DESCRIPTION
  Copies a publish folder into %ProgramData%\Teamscop\Agent, registers SCM auto-start
  with failure restart, and adds HKCU Run keys for SessionHelper + Teamscop.App.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [string]$InstallRoot = "$env:ProgramData\Teamscop\Agent",

    [string]$ApiBaseUrl = "https://teamscop.com"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\ServiceInstallerHints.ps1"

if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "SourceDir not found: $SourceDir"
}

$serviceExe = Join-Path $InstallRoot "Teamscop.StaffService.exe"
$helperExe = Join-Path $InstallRoot "Teamscop.SessionHelper.exe"
$appExe = Join-Path $InstallRoot "Teamscop.App.exe"

Write-Host "Installing Teamscop Staff Agent → $InstallRoot"
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

# Stop existing service if present
& sc.exe stop $script:TeamscopServiceName 2>$null | Out-Null
Start-Sleep -Seconds 2

Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallRoot -Recurse -Force

if (-not (Test-Path -LiteralPath $serviceExe)) {
    throw "Missing Teamscop.StaffService.exe in $InstallRoot (publish staff package first)."
}

# appsettings override for API base
$cfgPath = Join-Path $InstallRoot "appsettings.Production.json"
@{
    Agent = @{
        ApiBaseUrl = $ApiBaseUrl
        SyncSeconds = 30
    }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $cfgPath -Encoding UTF8

Install-TeamscopStaffService -BinaryPath $serviceExe

# Run at logon (current user installing — re-run per staff profile if needed)
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path -LiteralPath $helperExe) {
    Set-ItemProperty -Path $runKey -Name "TeamscopSessionHelper" -Value "`"$helperExe`""
    Write-Host "Registered Run: TeamscopSessionHelper"
}
if (Test-Path -LiteralPath $appExe) {
    Set-ItemProperty -Path $runKey -Name "TeamscopApp" -Value "`"$appExe`""
    Write-Host "Registered Run: TeamscopApp"
}

# Uninstall helper entry (ARP-style pointer)
$uninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TeamscopStaffAgent"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "Teamscop Staff Agent"
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "Teamscop"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $InstallRoot
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\uninstall-staff.ps1`" -InstallRoot `"$InstallRoot`""
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value $uninstallCmd

Write-Host "Staff agent installed. Service=$script:TeamscopServiceName"
Write-Host "Register a staff account (Teamscop.App) so agent-state.json is written under ProgramData."
