#Requires -RunAsAdministrator
<#
.SYNOPSIS
    DEV-ONLY: Registers the TotallyHotArcRouter Windows Service from a self-contained win-x64 publish.

.DESCRIPTION
    NOT the real install path anymore. The signed MSI built from src/TotallyHotArcRouter.Installer/
    (docs/router/packaging-and-distribution.md) registers the service via WiX ServiceInstall/ServiceControl
    and is what end users and CI-produced releases use. This script is kept only for a developer who wants
    to run/debug the Router as a real Windows Service on a dev machine without building the MSI. The
    service name here ("TotallyHotArcRouter") must match Program.cs's
    UseWindowsService(options => options.ServiceName = "TotallyHotArcRouter") and the MSI's ServiceInstall.

.PARAMETER PublishDir
    Directory containing the published TotallyHotArcRouter.exe (default: this script's
    ..\..\src\TotallyHotArcRouter\bin\Publish\Service, matching Service.pubxml's PublishDir).

.EXAMPLE
    dotnet publish src\TotallyHotArcRouter\TotallyHotArcRouter.csproj -p:PublishProfile=Service
    .\scripts\service\Install-RouterService.ps1
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\..\src\TotallyHotArcRouter\bin\Publish\Service")
)

$ErrorActionPreference = "Stop"

$ServiceName = "TotallyHotArcRouter"
$ExePath = Join-Path (Resolve-Path $PublishDir) "TotallyHotArcRouter.exe"

if (-not (Test-Path $ExePath)) {
    throw "Router executable not found at '$ExePath'. Publish it first with -p:PublishProfile=Service."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' is already registered. Run Uninstall-RouterService.ps1 first."
}

New-Service `
    -Name $ServiceName `
    -BinaryPathName $ExePath `
    -DisplayName "TotallyHot ArcRouter" `
    -Description "TotallyHot ArcRouter LLM routing proxy." `
    -StartupType Automatic

Start-Service -Name $ServiceName

Write-Host "Installed and started '$ServiceName' from '$ExePath'."
