# 0015. Machine-scoped protection and an administrator-only ACL for the shared secret store

**Status:** accepted <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-17
**Deciders:** David Pizon

> **Accepted 2026-09-17**, on a reproduced installer failure. Cause confirmed from the crash logs and
> the file's ACL; fix verified end to end — the MSI that previously aborted now installs cleanly, and the
> `LocalSystem` service reads the machine-scoped store. See [Verification](#verification).

## Context and Problem Statement

Installing the MSI failed with:

> Service 'TotallyHot Arc Router v0.1.0' (TotallyHotArcRouter) failed to start. Verify that you have
> sufficient privileges to start system services.

The message is Windows Installer's generic text for any `StartServices` failure and names neither the
real cause nor the file involved. Privileges were never the issue. The service registered correctly as
`LocalSystem`, the SCM did start it, and the process aborted with `ExitCode 1067`
(`ERROR_PROCESS_ABORTED`) six times at ~36-second intervals. The router's own log gave the actual
reason:

```
[ERR] Hosting failed to start
System.UnauthorizedAccessException: Access to the path
'C:\ProgramData\TotallyHotArcRouter\secrets.dat' is denied.
```

`secrets.dat` had a protected DACL carrying exactly one rule — `THEATRE-PC\david: FullControl`. The
parent directory grants `NT AUTHORITY\SYSTEM` full control, but the file blocked inheritance, so
`LocalSystem` had no access at all.

Three separate defects combined to turn a configuration mismatch into a failed install:

1. **The ACL.** `ProtectedSecretStore.WriteAtomically` wrote through
   `SecureFile.WriteRestricted`, whose DACL grants only the creating user. The store was created by a
   developer running the router directly, which locked out the service.
   `SecureFile.WriteMachineShared` and `RestrictToMachineAccountsWindows` already existed for exactly
   this case and had **zero callers** — their own remarks predicted this failure.
2. **The DPAPI scope.** The store was sealed with `DataProtectionScope.CurrentUser`, which ties
   decryption to one user profile's master key. Fixing only the ACL moves the crash from
   `UnauthorizedAccessException` to `CryptographicException`.
3. **No graceful degrade.** `LoadMapWindows`' documentation already claimed *"A missing or unreadable
   file is never distinguished from an empty store"*, but the code only handled *missing*. An
   undecryptable store threw out of host startup, so a recoverable condition aborted the process and
   surfaced as a privileges message.

This is drift from [ADR-0014](0014-cross-platform-service-layout-and-secret-backend.md), not a new
question. That ADR's own decision drivers say *"Match the existing trust model, not exceed it. Windows
already runs the router as a LocalSystem service with machine-wide, not per-user, state
(`%ProgramData%`)"*, and its outcome promises non-Windows backends a *"LocalSystem-equivalent scope"*.
But the same ADR also says *"Windows keeps DPAPI unchanged"*, and web GUI migration Phase P3 moved
`secrets.dat` into the machine-shared directory while leaving its protection bound to a single user.
The location became machine-wide; the protection did not follow.

## Decision Drivers

- **The installed service must be able to read the store it depends on.** It runs as `LocalSystem`; a
  developer or operator also runs the same exe directly as themselves. Both must work, and a store
  written by either must be readable by the other.
- **A recoverable condition must never fail host startup.** Every secret in this store is
  machine-generated and re-derivable. An unreadable store is worth a warning and a regeneration, never
  an aborted process behind a misleading SCM message.
- **Do not widen the boundary further than the fix requires.** `WriteMachineShared` as written also
  granted `BUILTIN\Users: Read`. Whether that is still needed is part of this decision, not a given.
- **Do not destroy existing secrets.** Three of the four stored values are the passwords decrypting
  `router-ca.pfx`, `router-leaf.pfx` and `telemetry-cert.pfx`. Losing them orphans those files, forces
  a CA regeneration, and makes every client re-trust the new root.
- **`secrets-at-rest.md` must keep describing what the code does.** It documents `CurrentUser` scope
  today.

## Considered Options

- **Option A — `LocalMachine` DPAPI scope + `WriteMachineShared` unchanged** (SYSTEM + Administrators
  full control, plus `BUILTIN\Users: Read`).
- **Option B — `LocalMachine` DPAPI scope + a tightened `WriteMachineShared`** (SYSTEM +
  Administrators + the writing account; no `Users` rule) **(chosen)**.
- **Option C — Keep `CurrentUser` scope; run the service as the interactive user** instead of
  `LocalSystem`.
- **Option D — Keep `CurrentUser` scope; give the service its own store** separate from the
  interactive user's.

## Decision Outcome

**Option B.** `ProtectedSecretStore` seals the store with `DataProtectionScope.LocalMachine` and writes
it through `SecureFile.WriteMachineShared`, which now grants full control to `LocalSystem`, the local
administrators group, and the writing account — and nothing else. `LoadMapWindows` quarantines a store
it cannot decrypt instead of throwing.

The `BUILTIN\Users: Read` rule is dropped because the reader it existed for is gone.
`WriteMachineShared`'s remarks justified it as the interactive-user GUI needing to read the shared
management token, but [ADR-0012](0012-loopback-session-auth-and-token-in-secret-store.md) replaced that
handoff with a loopback session cookie: `TrayApplicationContext` now authenticates with *"no credential
of its own to manage"*, and every remaining reader — `ProviderCredentialResolver`,
`BuildCostReconcilers`, `TelemetryTlsCertificate`, `LocalCertificateAuthority` — runs inside the
`LocalSystem` router process. Keeping the ACL narrow matters more under `LocalMachine` scope than it did
before: `Entropy` is compiled into the binary and is not a secret, so any account that can read the
bytes can decrypt them. **The ACL, not the encryption, is now the boundary between local accounts.**

Migration needs no separate step. `UnprotectWindows` tries the machine scope first and falls back to
`CurrentUser`, so a legacy store still decrypts for the user who wrote it and the next write re-seals
the whole map under the machine scope, preserving every secret in place. When the caller is *not* that
user — the service, as `LocalSystem` — both attempts fail and the file is moved aside to a timestamped
`.unreadable-*` name rather than deleted, because this account cannot read it but the account that
wrote it may still be able to.

### Consequences

- Good, because the reported installer failure is fixed at its cause, and the ACL regression that
  caused it is now asserted by a test rather than left to a dead code path nobody called.
- Good, because today's effective boundary *narrows*: `secrets.dat` was readable by one user account
  and is now readable by administrators only, where `WriteMachineShared` would have made it readable by
  every local account.
- Good, because an unreadable store degrades to a warning and a regeneration instead of a crash loop
  behind an SCM message that names the wrong cause.
- Bad, because a local administrator can now decrypt the store without being the user who wrote it.
  This is close to a non-change on Windows, where administrator access is already
  game-over — an administrator can read another account's DPAPI secrets by other means — but it is a
  real widening from "one specific user" and is stated rather than implied.
- Bad, because an existing store is *not* migrated when the service reaches it first: the secrets are
  quarantined and regenerated, which for the local CA means clients must trust the new certificate. An
  operator upgrading an install whose store was created interactively should run the router once as that
  user before starting the service, which converts the store in place. Documented in
  `docs/router/secrets-at-rest.md`.
- Neutral, because the on-disk format is byte-shaped exactly as before — a raw DPAPI blob with no
  version header. Only the scope recorded inside it changes, so no format version bump is needed.

### Verification

Reproduced on `THEATRE-PC`. The cause is observed, not inferred: the service was registered correctly as
`LocalSystem` and exited `1067` (`ERROR_PROCESS_ABORTED`) six times at ~36-second intervals, the router's
own log named `UnauthorizedAccessException` on `secrets.dat`, and that file's DACL was
inheritance-protected with a single `THEATRE-PC\david: FullControl` rule — no `SYSTEM` access at all,
despite the parent directory granting it.

Confirmed:

- The pre-existing store held four secrets (`telemetry:cert-password`, `router-ca:cert-password`,
  `router-leaf:cert-password`, `management:token`) sealed under `CurrentUser`. Re-sealing under
  `LocalMachine` preserved all four — the decrypted plaintext compared byte-for-byte equal — and the
  resulting ACL is `SYSTEM` + `Administrators` + the writing account, with no `BUILTIN\Users` rule.
- The fixed binary reads that migrated store and recovers the same management token, setting no
  `.unreadable-*` file aside, so the read path takes its success branch rather than the quarantine branch.
- 2893 unit tests pass, including three added here: the ACL regression that caused this failure, the
  in-place migration of a legacy per-user store, and the quarantine path.
- The MSI builds clean against the fixed publish output.

**Closed 2026-09-17 — the `LocalSystem` read is demonstrated, not assumed.** The fixed MSI was built and
installed elevated on this machine, against the migrated store:

- `msiexec` exited **0**. The `StartServices` action that previously aborted the install completed, and it
  cannot complete unless the service reaches `Running` — `ServiceControl` carries `Wait="yes"`. Sampling
  the SCM during a later start observed `State=Running` directly.
- The service's recorded `ExitCode` is **0**, where the failing install left **1067**
  (`ERROR_PROCESS_ABORTED`) after six aborted starts.
- **No `secrets.dat.unreadable-*` file was produced.** This is the decisive evidence: had `LocalSystem`
  been unable to decrypt the store, `QuarantineUnreadableStore` would have moved it aside and logged a
  warning. `secrets.dat` remains byte-for-byte the file the migration wrote, with its original timestamp,
  and the service's log contains no `UnauthorizedAccessException` and no `CryptographicException`.
- The service's own startup work confirms it read the store rather than merely starting: it opened the
  existing pricing database, ensured the transcript database, and initialized router memory — and its
  `--install-certificate` path did not re-mint the local CA, which it would have had the CA password been
  lost to a quarantine.

So the DPAPI expectation previously recorded here — that a `LocalMachine` blob is sealed with the machine
key `LocalSystem` holds, where a `CurrentUser` blob is not — is now confirmed by observation.

**A separate, unrelated defect surfaced during this verification and is not caused by this change.** The
installed service reaches `Running`, completes all of its startup work, and then shuts down gracefully
because its two inner hosts cannot bind their listeners:

```
[WRN] The MCP endpoint could not start: Failed to bind to address https://127.0.0.1:47103: address already in use.
[ERR] The proxy could not start:       Failed to bind to address https://127.0.0.1:47101: address already in use.
```

What is established: it is deterministic across three consecutive starts; exactly **one** router process
exists while it happens; no process on the machine holds 47101, 47103 or 47104 in any TCP state before,
during, or after; neither port falls in a Windows excluded/reserved range; and the *outer* host starts
normally, so this is the two inner hosts failing, not the process failing to come up. What is **not**
established is the cause — a same-process self-collision on those ports is the obvious candidate given
the evidence, but it has not been confirmed against the listener configuration, and no claim is made here.
It is tracked separately rather than in this ADR, which concerns only the secret store; the secret-store
crash loop this ADR fixes is gone, and the remaining failure is a listener-binding problem with a clean,
self-diagnosing shutdown rather than an abort.

One property is deliberately not unit-tested: that a blob really carries the machine scope. Windows
exposes no managed way to read a blob's scope back — `Unprotect` takes the scope as an argument but
reads the real one out of the blob and largely ignores what it was passed — and the only behavioral
difference is whether a *different* OS account can decrypt it, which a single-account test process
cannot exercise. The tests assert the ACL that admits that other account plus the data-preserving
migration; the scope itself is covered by the end-to-end service start above.

## Pros and Cons of the Options

### Option A — `LocalMachine` scope, `WriteMachineShared` unchanged

- Good, because it is the smallest change: it activates a path that was already designed, documented
  and reviewed, without revisiting a recorded decision.
- Good, because it cannot break a reader that turns out to still need `Users` access.
- Bad, because combined with `LocalMachine` scope the `Users: Read` rule makes the cert passwords and
  management token readable *and decryptable* by every local account. Under the old `CurrentUser` scope
  that rule only exposed ciphertext nobody else could open; the scope change is what turns it into a
  real disclosure.
- Bad, because it keeps a grant whose only consumer ADR-0012 removed.

### Option B — `LocalMachine` scope, tightened `WriteMachineShared` (chosen)

- Good, because it fixes the failure and narrows the boundary in the same change.
- Good, because the grant matches the actual reader set, which is verifiable from the call graph rather
  than from a stale comment.
- Bad, because it revises a documented decision, so the reasoning for dropping `Users` has to live
  somewhere durable — this ADR and the method's remarks.
- Bad, because a non-elevated developer is now covered only by the writing-account rule; if that rule
  were ever dropped, `SetAccessRuleProtection` would lock the writer out of the file it is creating.
  Called out in `RestrictToMachineAccountsWindows`' remarks.

### Option C — Keep `CurrentUser`; run the service as the interactive user

- Good, because no secret-storage change at all, and DPAPI stays bound to one profile.
- Bad, because a Windows service cannot depend on a specific interactive user: it starts at boot before
  anyone logs in, and `Package.wxs` would have to collect and store account credentials.
- Bad, because it contradicts ADR-0014's machine-wide state model and would make `%ProgramData%` the
  wrong location for every other file there.

### Option D — Keep `CurrentUser`; give the service its own store

- Good, because each account's secrets stay sealed to that account, the strongest boundary of the four.
- Bad, because the two stores silently diverge: a provider key saved from the dashboard would be
  invisible to a developer run, and each store would mint its own CA and management token.
- Bad, because it multiplies the CA problem — two roots, both trusted machine-wide, with no way to tell
  which a client should pin.

## More Information

- [ADR-0012](0012-loopback-session-auth-and-token-in-secret-store.md) — the loopback session cookie that
  removed the interactive-user reader this ADR's tightening depends on.
- [ADR-0014](0014-cross-platform-service-layout-and-secret-backend.md) — machine-wide state and the
  `LocalSystem`-equivalent scope this ADR brings the Windows path in line with.
- [ADR-0013](0013-name-constrained-local-ca-for-router-tls.md) — the local CA whose password lives in
  this store, and the reason quarantine preserves rather than deletes.
- `docs/router/secrets-at-rest.md` — the operator-facing description of what "protected" means, updated
  alongside this ADR.
