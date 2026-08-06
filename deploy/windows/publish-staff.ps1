<#
.SYNOPSIS
  Publish staff agent binaries into one folder for install-staff.ps1 / WiX harvest.
#>
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\..\..\artifacts\staff-agent"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$rid = "win-x64"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Remove-Item -Path (Join-Path $OutputDir "*") -Recurse -Force -ErrorAction SilentlyContinue

$projects = @(
    "agent\Teamscop.StaffService\Teamscop.StaffService.csproj",
    "agent\Teamscop.SessionHelper\Teamscop.SessionHelper.csproj",
    "agent\Teamscop.App\Teamscop.App.csproj",
    "agent\Teamscop.UsbApproval\Teamscop.UsbApproval.csproj",
    "agent\Teamscop.UninstallGuard\Teamscop.UninstallGuard.csproj"
)

foreach ($rel in $projects) {
    $proj = Join-Path $repo $rel
    Write-Host "Publishing $rel …"
    dotnet publish $proj -c $Configuration -r $rid --self-contained false -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $rel" }
}

Write-Host "Staff package ready: $OutputDir"
Write-Host "Install with (admin): .\install-staff.ps1 -SourceDir `"$OutputDir`""
