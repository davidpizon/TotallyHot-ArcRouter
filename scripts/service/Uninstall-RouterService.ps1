#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the TotallyHotArcRouter Windows Service.

.DESCRIPTION
    Companion to Install-RouterService.ps1. Does not delete the published files themselves -
    only the service registration.
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
