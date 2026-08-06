# Shared SCM contract (mirrors Teamscop.Engine.Lifecycle.ServiceInstallerHints)

$script:TeamscopServiceName = "TeamscopStaff"
$script:TeamscopServiceDisplayName = "Teamscop Staff Agent"
$script:TeamscopServiceDescription = "Teamscop background agent. Managed by company admin."

function Install-TeamscopStaffService {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinaryPath
    )

    $quoted = "`"$BinaryPath`""
    & sc.exe create $script:TeamscopServiceName binPath= $quoted start= auto DisplayName= "$script:TeamscopServiceDisplayName"
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1073) {
        # 1073 = service already exists — reconfigure
        & sc.exe config $script:TeamscopServiceName binPath= $quoted start= auto DisplayName= "$script:TeamscopServiceDisplayName"
    }

    & sc.exe description $script:TeamscopServiceName "$script:TeamscopServiceDescription"
    & sc.exe failure $script:TeamscopServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
    & sc.exe start $script:TeamscopServiceName
}

function Uninstall-TeamscopStaffService {
    & sc.exe stop $script:TeamscopServiceName 2>$null | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $script:TeamscopServiceName 2>$null | Out-Null
}
