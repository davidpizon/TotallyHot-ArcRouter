#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers the TotallyHotArcRouter Windows Service from a self-contained win-x64 publish.

.DESCRIPTION
    Manual/documented install path for the Router backend. Matches the registration the
    auto-update Updater helper performs itself in C# (ServiceInstaller.cs) when the GUI drives a
    first-run install - see docs/... auto-update plan. The service name here ("TotallyHotArcRouter")
    must match Program.cs's UseWindowsService(options => options.ServiceName = "TotallyHotArcRouter").

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
