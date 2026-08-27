# Packaging and Distribution

Status: **Decided and implemented (2026-08-26).** One signed[^signing] MSI, built with WiX Toolset v7,
replaces the three-executable Router↔Updater↔GUI design [`auto-update-plan.md`](auto-update-plan.md)
originally shipped. This document records why: the MSIX packaging path that was evaluated and rejected, the
MSI decision itself, the WiX v7 licensing note, and the GUI-elevated-vs-Router-launched investigation this
project could not empirically settle, so none of it gets silently re-litigated later.

[^signing]: "Signed" is the target, not the current state — no code-signing certificate exists yet. See §4.

## 1. Why packaging needed revisiting at all

The original design (`auto-update-plan.md` Phase 2) had the Router download and checksum-verify a release
zip, then hand off to a separate `TotallyHotArcRouter.Updater.exe` helper process to stop the Windows
Service, swap the install directory, and restart it — because a running process cannot overwrite its own
files, so *something else* has to do the swap. That something else was hand-rolled: `Updater.exe`'s own
stop/backup/extract/restore/restart sequence, with its own timeout constants, its own rollback-on-failure
logic, and its own CLI contract to the Router that launched it.

Windows Installer already does exactly this, as a built-in primitive: `ServiceControl` stops a service
before a file swap and restarts it after, and a failed MSI transaction rolls back automatically. Every line
of `UpdaterService`'s hand-rolled sequence is something Windows Installer would do anyway, more robustly (a
real transaction, not a best-effort rename-and-restore), for free, without a bespoke helper project to
maintain and test. That is the whole reason this decision exists.

## 2. MSIX: evaluated and rejected

MSIX (the modern Windows app-packaging format) was considered as an alternative to a raw MSI and rejected
before implementation began. The disqualifying finding, specific to this project's shape (an always-on
Windows Service, not just a foreground app):

- A packaged Windows Service under MSIX requires **Windows 10 2004+**, the **`desktop6:Service`**
  manifest extension, and a **restricted capability** (`packagedServices`/`localSystemServices`) that is
  not available to an ordinary unrestricted package — it is gated to accounts running as `LocalSystem`,
  `LocalService`, or `NetworkService` only.
- MSIX's story for updating a package while it has running processes is exactly the failure mode an
  always-on service hits every single update: `Add-AppxPackage`'s options are
  `-DeferRegistrationWhenPackagesAreInUse` (defers registration until nothing in the package is running —
  **never fires** for a service that is, by design, always running), `-ForceApplicationShutdown` (a hard
  kill of the running service process, mid-request, with no graceful stop), or outright failure. In
  practice, updating a packaged always-on service tends to require a **reboot** to clear the file locks
  MSIX's deployment stack won't force-clear itself.

None of those three outcomes (defer-forever, hard-kill, or require-a-reboot) is acceptable for a router
that is, definitionally, meant to be routing traffic continuously. A raw MSI's `ServiceControl` — an
explicit, graceful stop *before* the file operation, not a race against whatever state the process happens
to be in — has no equivalent problem, because it isn't trying to reconcile a package-integrity model
designed around foreground apps with an always-on background service. This finding is specific to the
service-packaging story; MSIX remains a fine format for services-free desktop apps, and nothing here argues
otherwise.

## 3. The MSI decision

- **Delete the Updater project entirely** rather than keep it as a fallback path. `src/TotallyHotArcRouter.Updater/`
  and `src/TotallyHotArcRouter.Updater.Tests/` are gone; `scripts/service/Install-RouterService.ps1`/
  `Uninstall-RouterService.ps1` are kept only as a clearly-marked dev-only path for a developer who wants a
  real Windows Service on a dev machine without building the MSI.
- **One MSI installs both the Router and the GUI**, to `%ProgramFiles%\TotallyHotArcRouter\Router\` and
  `\Gui\` respectively — no `\Updater\` directory. `%LOCALAPPDATA%\TotallyHot.ArcRouter\` (per-user
  config/state either app writes at runtime) is never referenced by the installer and is therefore
  untouched by install, upgrade, or uninstall.
- **`ServiceInstall`/`ServiceControl`** register the `TotallyHotArcRouter` Windows Service (`LocalSystem`,
  auto-start, matching `Program.cs`'s `UseWindowsService` call exactly) and stop it before / start it after
  the file swap on install, upgrade, and uninstall.
- **A real major upgrade** (`MajorUpgrade`, WiX's documented pattern — see
  [`src/TotallyHotArcRouter.Installer/Package.wxs`](../../src/TotallyHotArcRouter.Installer/Package.wxs)):
  installing v*N*+1 over v*N* cleanly replaces it — one Add/Remove Programs entry, not two side-by-side
  installs — because `RemoveExistingProducts` runs inside the same transaction, ahead of the new files
  being laid down.
- **`ProductVersion` is derived from `Directory.Build.props`' `<Version>`** via MSBuild property
  passthrough into the `.wixproj` — never a second, hand-typed version (see
  [`version-compatibility.md`](version-compatibility.md) §1).
- **Verified for real, empirically, in this repository**: the installer project was built against the
  Router's and GUI's actual `Service`-publish-profile output (self-contained `win-x64`) — 1,082 files
  packaged, `ProductVersion` correctly read back as `1.0.0` from the built MSI's `Property` table, the
  Router's exe present exactly once (no duplicate between the harvested file group and its dedicated
  `ServiceInstall` component), and the `ServiceControl` table populated with the expected `Name`/`Wait`
  values. A real `msiexec /i ... /qn` install/uninstall cycle could **not** be verified in this environment
  — this development session has no admin/UAC-capable interactive Windows session (confirmed: attempting to
  register even an unrelated test Windows Service returns `OpenSCManager FAILED 5: Access is denied`), so
  the actual service registration and file-swap behavior have not been exercised end to end. That remains a
  real, outstanding manual verification step before shipping the first MSI-based release.

## 4. Open prerequisite: code signing

**No code-signing certificate exists for this project.** The MSI the release workflow
(`.github/workflows/release.yml`) produces today is unsigned. This has two concrete consequences:

- Windows SmartScreen will warn on install ("Windows protected your PC" / unknown publisher) until a
  certificate is obtained and a `signtool sign` step is added to the release workflow (marked with a `TODO`
  at the point it would go).
- `MsiUpdateApplier` (`TotallyHot.ArcRouter.Gui.Telemetry`) verifies the downloaded MSI's SHA256 against the
  value the release published, but does **not** verify an Authenticode signature — there is nothing to
  verify yet. The seam is marked with a `TODO(signing)` comment at the exact point a
  `WinVerifyTrust`-based check would be added, once a certificate exists.

Trust today is therefore: HTTPS (to GitHub) + GitHub Releases' own integrity (the asset an operator or the
GUI downloads is the one the release workflow uploaded) + the SHA256 checksum published alongside it in
`checksums.txt` on the same release. This is the same trust model `auto-update-plan.md` shipped with
originally (see that document's "Deferred/future phases" — code-signing was already an open item before the
MSI switch) — the switch changes *what* gets downloaded and verified, not the trust model around it.

## 5. WiX Toolset v7 and the Open Source Maintenance Fee

`src/TotallyHotArcRouter.Installer/` targets **WiX Toolset v7** (`WixToolset.Sdk` version `7.0.0`). WiX v7
introduced build-time enforcement of the **Open Source Maintenance Fee (OSMF)** — a sponsorship requirement
for organizations above a revenue threshold — that fails the build with error `WIX7015` unless the EULA is
explicitly accepted. The `.wixproj` sets:

```xml
<PropertyGroup>
  <AcceptEula>wix7</AcceptEula>
</PropertyGroup>
```

**The repo owner has explicitly authorized accepting this EULA/sponsorship commitment for this project**
(2026-08-26 decision, made alongside the MSI packaging decision itself). This is a real financial/
organizational commitment, not a build-configuration technicality — see
[the FireGiant OSMF page](https://docs.firegiant.com/wix/osmf/) for what the fee actually obligates, and do
not remove or bypass `AcceptEula` to silence a `WIX7015` build failure without re-confirming that
authorization still stands.

## 6. GUI-elevated vs. Router-launched: the investigation

Before implementation, two designs for *where the elevated `msiexec` launch happens* were considered:

1. **Router-launched, detached.** The always-on Router (running as `LocalSystem` under the Windows Service)
   launches `msiexec` itself, detached, requiring no interactive session and no UAC prompt at all — because
   `LocalSystem` is already maximally privileged.
2. **GUI-elevated.** The interactive GUI downloads and verifies the MSI, then launches `msiexec` elevated
   (`UseShellExecute = true`, `Verb = "runas"`), which triggers one ordinary UAC prompt, then exits
   immediately so the MSI can replace its own files.

Option 1 was the original intent — no interactive session needed at all is an attractive property for an
unattended server-like deployment. It was **not chosen**, for a specific, stated reason: **it could not be
empirically verified in this project's development environment.** The open question was whether a `msiexec`
process launched detached by a running Windows Service survives that service being stopped mid-transaction
by the very same MSI it's running — a real Windows process-lifetime/job-object question with a knowable
answer, but one that requires an actual installed Windows Service and admin rights to observe. This
environment has neither (confirmed: `sc.exe create` returns `OpenSCManager FAILED 5: Access is denied` in
every session that attempted it; a Windows-container fallback was also checked and ruled out — the
available container backend on the development machine is a Linux/WSL2 backend, which cannot host a real
Windows Service either).

Rather than ship a design nobody had actually watched work, the repo owner decided: **build the GUI-elevated
fallback.** It is simpler and more conventional besides — one ordinary UAC prompt is what virtually every
Windows desktop installer already asks for, no special claim about service/job-object survival is required,
and it needed no untestable assumption to ship. `MsiUpdateApplier`'s XML doc remarks record this choice at
the point in the code it affects most directly (the GUI-owns-the-launch design), and
[`version-compatibility.md`](version-compatibility.md) §2 describes the resulting swap sequence.

One consequence of choosing GUI-elevated over Router-launched: the "bounded SDDL so a non-elevated process
can control the Windows Service" idea that was floated during the Router-launched investigation has **no
caller anymore** and was deliberately not built — it existed only to let a not-yet-elevated Router-side
apply request service control without full admin rights, and there is no Router-side apply left to need it.

## Related

- [`version-compatibility.md`](version-compatibility.md) — how the Router and GUI relate by version now
  that there is no Updater, and the resulting swap sequence.
- [`auto-update-plan.md`](auto-update-plan.md) — the original Router-self-update design (detection half
  still current; the Updater-based apply half is superseded by this document).
- [`../../AGENTS.md`](../../AGENTS.md) — the repository-wide rules every change validates against.
