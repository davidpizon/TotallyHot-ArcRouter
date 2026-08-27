#Requires -RunAsAdministrator
<#
.SYNOPSIS
    DEV-ONLY: Stops and removes the TotallyHotArcRouter Windows Service.

.DESCRIPTION
    Companion to Install-RouterService.ps1 - see that script's header for why this is a dev-only path
    now that the signed MSI (src/TotallyHotArcRouter.Installer/) is the real uninstall path (its
    ServiceControl/MajorUpgrade elements handle service stop and removal on a real uninstall). Does not
    delete the published files themselves - only the service registration.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$ServiceName = "TotallyHotArcRouter"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not registered; nothing to do."
    return
}

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
}

sc.exe delete $ServiceName | Out-Null

Write-Host "Removed service '$ServiceName'."
