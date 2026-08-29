<#
.SYNOPSIS
    DEV-ONLY: Publishes both apps and builds the MSI with an auto-bumped build number.

.DESCRIPTION
    The one command to run when you want the installed application to actually change. It exists because
    two separate things silently produce an MSI that installs nothing new:

      1. The installer harvests <RouterPublishDir>/<GuiPublishDir> (src/TotallyHotArcRouter.Installer/
         TotallyHotArcRouter.Installer.wixproj), which point at each project's `Service` publish profile
         output - bin\Publish\Service. A plain `dotnet build` never writes those directories, so the MSI
         happily packages whatever publish output was left there last time.
      2. Package.wxs' <MajorUpgrade> has no AllowSameVersionUpgrades, so it only detects versions strictly
         BELOW ProductVersion. Rebuilding at the same version means the previous install is never detected
         or removed, and the new MSI installs side-by-side under a fresh auto-generated ProductCode.

    This script closes both: it always publishes before packaging, and it always hands the build a
    ProductVersion higher than the last one, so RemoveExistingProducts uninstalls the previous build before
    the new files are laid down.

    NOT the release path. Releases are tag-triggered through .github/workflows/release.yml, which verifies
    that the "v<Version>" tag matches Directory.Build.props' <Version> exactly. That committed value is
    therefore left untouched here - major.minor are read from it and only the build field is overridden, on
    the command line, for this local build.

.PARAMETER Configuration
    The build configuration to publish and package (default: Release, matching what release.yml builds).

.PARAMETER Version
    An explicit three-field version to build instead of bumping. Use this to reproduce a specific release
    build locally; the counter is left alone. Must be major.minor.build with each field inside Windows
    Installer's limits (see the bump logic below).

.EXAMPLE
    .\scripts\build-installer.ps1

    Publishes both apps and builds the MSI as 1.0.<next>, where <next> is the incremented .build-number.

.EXAMPLE
    .\scripts\build-installer.ps1 -Version 1.0.0

    Rebuilds exactly what the v1.0.0 release tag would produce, without touching the counter.

.NOTES
    Installing these builds leaves ProductVersion ahead of the real release line (a local 1.0.37 outranks a
    published 1.0.1), and Package.wxs' DowngradeErrorMessage will then refuse the genuine release MSI.
    Uninstall the local build first when you switch back to a released version.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$propsPath = Join-Path $repoRoot "src\Directory.Build.props"
$counterPath = Join-Path $repoRoot ".build-number"

<#
.SYNOPSIS
    Runs a dotnet command and turns a non-zero exit code into a terminating error.
.DESCRIPTION
    $ErrorActionPreference does not apply to native executables, so without this a failed publish would
    fall through to the MSI build - which is precisely the failure mode this script exists to prevent,
    since the packaging step would then silently harvest the previous run's publish output.
#>
function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "==> $Description" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if ($PSBoundParameters.ContainsKey("Version")) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "-Version must be major.minor.build (three fields), got '$Version'."
    }
    $buildVersion = $Version
}
else {
    # major.minor come from the release single source of truth; only the build field is bumped locally.
    if (-not (Test-Path $propsPath)) {
        throw "Could not find $propsPath."
    }

    # SelectSingleNode per PropertyGroup rather than release.yml's shorter
    # `$props.Project.PropertyGroup.Version`: that dotted form relies on member enumeration over an array
    # whose first element (the TreatWarningsAsErrors group) has no Version child, which Set-StrictMode
    # turns from a silent $null into a terminating error.
    $props = [xml](Get-Content $propsPath)
    $baseVersion = $props.Project.PropertyGroup |
        ForEach-Object { $_.SelectSingleNode("Version") } |
        Where-Object { $_ } |
        Select-Object -First 1 |
        ForEach-Object { $_.InnerText.Trim() }

    if ($baseVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Directory.Build.props' <Version> is '$baseVersion'; expected major.minor.build."
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]

    # Windows Installer compares ONLY the first three fields of ProductVersion - a bumped fourth field is
    # ignored outright and would leave MajorUpgrade just as dead as not bumping at all - and it caps them
    # at 255.255.65535. So the counter has to live in the build field, and has to stay inside that range.
    if ($major -gt 255 -or $minor -gt 255) {
        throw "Version '$baseVersion' exceeds Windows Installer's 255.255 limit on major.minor."
    }

    $build = 0
    if (Test-Path $counterPath) {
        $existing = (Get-Content $counterPath -Raw).Trim()
        if ($existing -notmatch '^\d+$') {
            throw "$counterPath contains '$existing'; expected a whole number. Delete it to restart the count."
        }

        $build = [int]$existing
    }

    $build++
    if ($build -gt 65535) {
        throw "Build number $build exceeds Windows Installer's ProductVersion limit of 65535. Bump <Version>'s minor in $propsPath and delete $counterPath."
    }

    # Written before the build rather than after it: the counter only has to be monotonic, and burning a
    # number on a failed build is harmless, whereas reusing one after a partial build is not.
    Set-Content -Path $counterPath -Value $build -NoNewline
    $buildVersion = "$major.$minor.$build"
}

Write-Host "Building TotallyHot ArcRouter $buildVersion ($Configuration)" -ForegroundColor Green

# Both publishes must run before the installer builds: the .wixproj harvests their output directories
# rather than the projects themselves, so it has no dependency that would trigger them on its own.
Invoke-DotNet -Description "Publish Router" -Arguments @(
    "publish"
    (Join-Path $repoRoot "src\TotallyHotArcRouter\TotallyHotArcRouter.csproj")
    "-c", $Configuration
    "-p:PublishProfile=Service"
    "-p:Version=$buildVersion"
)

Invoke-DotNet -Description "Publish GUI" -Arguments @(
    "publish"
    (Join-Path $repoRoot "src\TotallyHotArcRouter.Gui\TotallyHotArcRouter.Gui.csproj")
    "-c", $Configuration
    "-p:PublishProfile=Service"
    "-p:Version=$buildVersion"
)

# The .wixproj derives <ProductVersion> from $(Version), so overriding it here is what makes MajorUpgrade
# detect and remove the previously installed build.
Invoke-DotNet -Description "Build MSI" -Arguments @(
    "build"
    (Join-Path $repoRoot "src\TotallyHotArcRouter.Installer\TotallyHotArcRouter.Installer.wixproj")
    "-c", $Configuration
    "-p:Version=$buildVersion"
)

$msiPath = Join-Path $repoRoot "src\TotallyHotArcRouter.Installer\bin\x64\$Configuration\TotallyHotArcRouter.Installer.msi"
if (-not (Test-Path $msiPath)) {
    throw "The build reported success but no MSI was found at $msiPath."
}

Write-Host ""
Write-Host "Built $buildVersion" -ForegroundColor Green
Write-Host "  $msiPath"
Write-Host ""
Write-Host "Install with:" -ForegroundColor Cyan
Write-Host "  msiexec /i `"$msiPath`""
