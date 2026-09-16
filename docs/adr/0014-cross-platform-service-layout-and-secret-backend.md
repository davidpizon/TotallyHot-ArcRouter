# 0014. Cross-platform data paths, machine-wide service layout, and a non-Windows secret backend

**Status:** accepted <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-14
**Deciders:** David Pizon

> **Accepted 2026-09-15**, on completion of the web GUI migration plan's Phases P1-P10:
> `AppDataPaths`/cross-platform secret backend shipped in P3, and systemd/LaunchDaemon packaging plus a
> real Docker image shipped in P10 - verified end to end with `podman build`/`podman run`, including a
> restart-survives-with-secrets-intact check (see the plan doc's P10 status section).

## Context and Problem Statement

The router only runs correctly on Windows today. Three independent things assume it:

1. **Data paths.** `ManagementAccessToken` and `Router/RoutingGateStore.cs` resolve
   `Environment.SpecialFolder.CommonApplicationData`, which is `/usr/share` on Linux — not writable by
   an ordinary service account. `PriceCatalog/StorageOptions.cs` already maps `%PROGRAMDATA%` tokens to
   a per-user directory off Windows (an inconsistent, ad hoc fix), and several options classes
   (`EmbeddingOptions`, `LlmRouterOptions`) still contain literal `%LOCALAPPDATA%` tokens.
2. **Process hosting.** `Program.cs` calls `UseWindowsService()` only; there is no `UseSystemd()`
   equivalent, and the Serilog file sink path (`C:\Logs\ArcRouter\...` in `appsettings.json`) is a
   hard-coded Windows path.
3. **Secret storage.** `ProtectedSecretStore` wraps Windows DPAPI (`System.Security.Cryptography.ProtectedData`,
   `DataProtectionScope.CurrentUser`) to encrypt provider credentials and (per ADR-0012) the management
   token at rest. Off Windows, `ProtectedData` throws `PlatformNotSupportedException`; the store already
   guards this, but the practical effect is that credentials silently cannot be saved from the GUI on
   Linux/macOS today.

This ADR decides the cross-platform data-path convention, the process-hosting model per OS, and the
non-Windows secret-encryption backend, so the router (and therefore the web GUI it serves) works
correctly — not just compiles — off Windows.

## Decision Drivers

- **A GUI feature (saving a provider API key) must not silently no-op on Linux/macOS.** That is the
  concrete defect this ADR exists to fix.
- **Match the existing trust model, not exceed it.** Windows already runs the router as a LocalSystem
  service with machine-wide, not per-user, state (`%ProgramData%`). Parity, not a stronger or weaker
  guarantee, is the goal.
- **Honesty about what "protected" means off Windows.** DPAPI ties encryption to a Windows user/machine
  identity via the OS. Nothing in .NET's cross-platform BCL offers an equivalent OS-backed secret
  vault out of the box; ASP.NET Core's Data Protection stack with a file-system key provider reduces
  "protected at rest" to file permissions, and that must be stated plainly rather than implied to be
  DPAPI-equivalent.
- **One code path per concern**, selected by a pluggable backend, not scattered `OperatingSystem.IsWindows()`
  checks at every call site.

## Considered Options

- Option A — Machine-wide services everywhere: systemd system unit + dedicated `arcrouter` user on
  Linux, launchd `LaunchDaemon` + dedicated user on macOS, both under `/var/lib` / `/Library/Application
  Support` equivalents to `%ProgramData%`; ASP.NET Core Data Protection with a file-system key ring as
  the non-Windows secret backend (chosen).
- Option B — Per-user services (systemd `--user` unit / launchd `LaunchAgent`), data under the logged-in
  user's home directory.
- Option C — Keep DPAPI-only secret storage; on non-Windows, credentials remain env-var-only (today's
  actual fallback behavior), with no new encrypted-at-rest option.
- Option D — A third-party secret-management dependency (e.g. OS keychain wrappers, a vault client) as
  the non-Windows backend instead of ASP.NET Core Data Protection.

## Decision Outcome

Chosen option: "Option A", because **match the existing trust model, not exceed it** rules out Option B:
Windows already treats router state as machine-wide (LocalSystem service, `%ProgramData%`), and a
per-user Linux/macOS install would be a *different*, weaker-in-some-ways/stronger-in-others model to
document and support in parallel rather than a port of the existing one. Option C is rejected outright
because it leaves the concrete defect (**a GUI feature must not silently no-op**) unfixed — it is the
status quo, not a decision. Option D is rejected under **one code path per concern**: ASP.NET Core's
Data Protection APIs already ship in the BCL the router already depends on, need no new external
dependency or native interop, and are the same abstraction .NET itself recommends for "protect this at
rest, cross-platform" — introducing a second protection library alongside DPAPI adds complexity ADR-0012's
token relocation and this ADR's credential-storage fix don't need.

**Concretely:**
- New `AppDataPaths` resolver, one implementation, no per-call-site `OperatingSystem.IsWindows()`
  branching: Windows → `%ProgramData%\TotallyHotArcRouter`; Linux → `$STATE_DIRECTORY` (set by the
  systemd unit) falling back to `/var/lib/totallyhot-arcrouter`; macOS → `/Library/Application
  Support/TotallyHotArcRouter`; a `dev` fallback (per-user) when none of those are writable, for local
  `dotnet run` use. Adopted by `ManagementAccessToken`, `RoutingGateStore`, `StorageOptions`,
  `ProtectedSecretStore`, and `TelemetryTlsCertificate`, replacing their independent path logic.
- `ProtectedSecretStore` gets a pluggable protector interface. Windows keeps DPAPI unchanged (the
  on-disk `secrets.dat` format is preserved so existing Windows installs need no migration). Elsewhere,
  ASP.NET Core Data Protection (`PersistKeysToFileSystem(<data-dir>/keys)`), with the key directory
  created at file mode `0700`, a fixed `SetApplicationName`, and a versioned header on `secrets.dat` so
  the store can tell which protector wrote a given file. The store refuses to write a secret rather than
  degrade silently if the key directory is missing or more permissive than expected.
- systemd system unit (dedicated `arcrouter` user, `StateDirectory=`, `LogsDirectory=`,
  `ProtectSystem=strict`, `NoNewPrivileges`) and a macOS `LaunchDaemon` (dedicated user, quarantine
  attribute removal for the unsigned build) install scripts, matching the Windows service's
  LocalSystem-equivalent scope.
- `Program.cs` gains `UseSystemd()` alongside `UseWindowsService()` (a no-op outside its own host,
  exactly like the existing call). The Serilog file path becomes environment-expanded and supplied per
  platform by the unit/plist/MSI rather than hard-coded.

### Consequences

- Good, because saving a provider credential from the GUI works identically on every platform the
  router runs on — the concrete defect this ADR targets is fixed.
- Good, because one path resolver and one pluggable protector replace several independent, partially
  inconsistent `%PROGRAMDATA%`/`%LOCALAPPDATA%` mappings scattered across options classes.
- Bad, because "protected at rest" now means two different things depending on platform: DPAPI (OS
  identity-bound encryption) on Windows, versus file-permission-gated Data Protection keys elsewhere.
  This must be stated in the router's security docs (`docs/router/secrets-at-rest.md`), not left
  implicit.
- Bad, because a machine-wide install now requires root/sudo/an elevated installer on Linux and macOS
  too (a change from "just run the binary" for anyone who was doing that), matching Windows service
  install today.
- Neutral, because the Data Protection key ring must survive process restarts (tested explicitly in the
  migration plan's P3 exit criteria) or every previously-saved secret becomes permanently undecryptable
  — this is a correctness requirement for the chosen option, not a trade-off to accept.

## Pros and Cons of the Options

### Option A — Machine-wide service + Data Protection key ring (chosen)

- Good, because it mirrors the Windows model exactly, so documentation and mental model stay one thing,
  not "Windows works like X, everything else works like Y."
- Good, because it uses only BCL/ASP.NET Core primitives already in the dependency graph.
- Bad, because it requires elevated install steps on every OS, same as Windows does today.

### Option B — Per-user services

- Good, because no elevation is required to install; matches how many desktop Linux/macOS tools are
  distributed.
- Bad, because it diverges from the existing Windows model (machine-wide, always-running LocalSystem
  service) in ways that would need separate documentation, separate auto-start semantics (only while
  logged in vs. at boot), and a second data-path convention.

### Option C — DPAPI-only, no non-Windows secret storage

- Good, because it is zero new code.
- Bad, because it leaves the defect this ADR exists to fix unresolved.

### Option D — External secret-management dependency

- Good, because OS-native keychains (e.g. GNOME Keyring, macOS Keychain via a wrapper) can offer
  stronger guarantees than file-permission-gated keys in some configurations.
- Bad, because it adds a new dependency surface (native interop or a keyring daemon that may not be
  running on a headless server/container) for a router that must also run unattended in Docker, where
  no user keyring session exists at all.

## More Information

See [`docs/gui/web-gui-migration-plan.md`](../gui/web-gui-migration-plan.md) phase P3 (implementation
and exit criteria: Linux CI credential round-trip, Windows DPAPI-format compatibility fixture, key-ring
restart-survival test) and phase P10 (systemd/launchd packaging). Feeds
[ADR-0012](0012-loopback-session-auth-and-token-in-secret-store.md) (the management token's new home)
and [ADR-0013](0013-name-constrained-local-ca-for-router-tls.md) (the CA private key's storage). Related
existing doc: `docs/router/secrets-at-rest.md`.
