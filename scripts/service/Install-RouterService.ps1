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

# The display name carries the version, matching the MSI's ServiceInstall/@DisplayName. Read from the
# exe being registered rather than from Directory.Build.props, so it describes the binary actually
# installed even when the publish output is older than the working tree. ProductVersion maps to
# AssemblyInformationalVersion, onto which the SDK appends "+<git-sha>" in a checkout; strip it the same
# way GitHubReleaseCheckClient and the GUI's AppVersion do.
$ProductVersion = (Get-Item $ExePath).VersionInfo.ProductVersion
$Version = if ([string]::IsNullOrWhiteSpace($ProductVersion)) { "0.0.0" } else { ($ProductVersion -split '\+')[0] }

New-Service `
    -Name $ServiceName `
    -BinaryPathName $ExePath `
    -DisplayName "TotallyHot Arc Router v$Version" `
    -Description "TotallyHot Arc Router LLM routing proxy." `
    -StartupType Automatic

Start-Service -Name $ServiceName

Write-Host "Installed and started '$ServiceName' from '$ExePath'."
