# 0024. Protect the machine-shared data directory with one inherited DACL, and make deletion final

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-10-03
**Deciders:** David Pizon

## Context and Problem Statement

The router keeps all of its state in one machine-shared directory: `%ProgramData%\TotallyHotArcRouter`
on Windows, `/var/lib/totallyhot-arcrouter` (with logs in `/var/log/totallyhot-arcrouter`) on Linux,
`/Library/Application Support/TotallyHotArcRouter` on macOS, and `/data` in Docker
(`AppDataPaths.ResolveMachineSharedDirectory`, ADR-0014). On 2026-09-30, investigating #184 showed
that this directory is open to every local account:

- **Readable.** On Windows it inherits `%ProgramData%`'s ACL, so every file except `secrets.dat`
  carries `BUILTIN\Users:(I)(RX)`. That includes `transcripts.db` and its `-wal`, which hold prompt and
  response text, the `.pfx` files, the other databases, and the logs. At the shipped `Debug` level,
  the logs hold request and response excerpts. On Linux and macOS the installers leave 0755
  directories and a 022 umask.
- **Writable.** Any local account can create entries in the root, `logs\` and `models\`, all of which
  the `LocalSystem` service then reads. The worst case is `appsettings.local.json`, an optional
  overlay that is reloaded on change ([#193](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/193)).
- **Per-file ACLs don't hold.** SQLite deletes `-wal` and `-shm` on last close and re-creates them with
  the folder's ACL, so ADR-0015's per-file treatment cannot be extended to databases.
- **Deletion isn't final.** Rows removed by retention or by Clear stay readable in the WAL and in the
  database file's free pages. In a disposable copy, 298 of 300 cleared rows were still in
  `transcripts.db` after a restart. Pragmas such as `secure_delete` and `synchronous` are per
  connection, so setting them once in `EnsureCreated` has no lasting effect.
- **Uninstall keeps everything**, conversations included (`Package.wxs` and both `uninstall.sh`
  scripts, on purpose).

This is a decision rather than a fix because it changes a security boundary. It changes who can read
and write the machine-shared directory, it revises an accepted rule in ADR-0015 (the writing
account's ACE on `secrets.dat`), and it changes operator workflows: editing the overlay or reading
logs by hand now needs elevation. ADR-0019 already decides how conversation text is stored once it
moves off SQLite, and lists this problem as "tracked separately". This ADR is that item. The full
design, its evidence and its phasing are in the approved plan,
[`docs/plans/issue-184-transcript-data-protection.md`](../plans/issue-184-transcript-data-protection.md)
(approved 2026-10-03; its §7 records each decision).

## Decision Drivers

- **David's requirement (2026-09-30):** conversation data must not be open to "any application that
  can make an HTTP call". That includes any application running as the operator's own account, so
  no individual account may hold an ACE on the store.
- **Cover what SQLite re-creates.** The protection must reach `-wal`, `-shm`, future files, and
  ADR-0019's session folder without per-file work.
- **No planted input.** The `LocalSystem` service must never load a file that a standard account
  could have created: the config overlay, model files, or anything else in the tree.
- **Deletion means gone.** Retention, Clear and uninstall must leave no recoverable text, whether in
  the WAL, in freed pages, or in logs.
- **Fail closed.** A root that cannot be verified must stop the service, never be silently re-created
  or used.
- **Keep the tray and clients working.** The unelevated tray reads `web-interface.json`, and clients
  import the public CA certificate.
- **Every platform alike.** Windows, Linux, macOS and Docker get the same boundary.

## Considered Options

- A. One protected, inheritable DACL on the whole directory (owner-only modes on Linux and macOS),
  created atomically and verified before first use
- B. ADR-0015's per-file ACL on `transcripts.db`, `-wal` and `-shm`
- C. Protect only ADR-0019's session folder
- For deletion: D. `secure_delete=ON` on every connection, plus `wal_checkpoint(TRUNCATE)` after
  each delete, versus E. `VACUUM` after each purge

## Decision Outcome

Chosen: **option A for the boundary, and option D for deletion, with `VACUUM` (E) used only for a
one-time scrub.** A is the only option that meets **"Cover what SQLite re-creates"** and **"No planted
input"** together. Inheritance gives the side files and every future file the protected ACL, and only
administrators can then add entries. D is the only per-delete scheme that left zero residue in the
harness while still meeting **"Deletion means gone"**. `FAST` mode, or `ON` set once, left 296–298 of
300 rows.

The decision has these rules:

1. **The boundary.** The root has a protected DACL: no inheritance from `%ProgramData%`, and full
   control with `(OI)(CI)` for `SYSTEM` and `Administrators` only. **No individual account gets an
   ACE**, not even the account that created it (plan decision 4). Its owner must be `SYSTEM` or
   `Administrators`, because an owner can always rewrite its DACL. On Linux and macOS the state and
   logs directories are `0700`, owned by the service account, with `UMask=0077` (unit) or `Umask 077`
   (plist and Docker entrypoint).
2. **Atomic creation, verified before first use.** The root is created with its DACL
   (`DirectoryInfo.Create(DirectorySecurity)`), never created and then restricted. Verification runs
   in `AppDataPaths.ResolveMachineSharedDirectory` before its write probe, and so before
   `appsettings.local.json` is registered. A root passes only if its owner is trusted, it is not a
   reparse point, and no broad group or individual account has an ACE.
3. **Migration, not in-place repair.** A legacy root (the old broad ACL, owned by `SYSTEM`,
   `Administrators`, the service account, or the installing user) is migrated by an elevated
   `--migrate-data-directory` command into a new protected tree. That command is run by the MSI and
   `install.sh` with the router stopped. Migration never follows a link, never adopts a hard link, a
   file with an untrusted owner, or `appsettings.local.json`, and leaves nothing reachable in the
   quarantine. A root owned by any other account is a squat: it is renamed aside untouched. The
   service fails closed on any root that is neither protected nor migrated, naming the command to
   run. An unelevated process falls back to the per-user directory, as it already does.
4. **Exceptions.** `web-interface.json` keeps an explicit `Users:R` ACE, because it holds only a URL
   and a thumbprint and the tray opens it by full path. The public CA certificate is republished to a
   separate public directory (`%ProgramData%\TotallyHotArcRouter-Public`, `/run/totallyhot-arcrouter`,
   `/Library/Application Support/TotallyHotArcRouter-Public`, or `/public` in Docker). That directory
   is readable by all, writable only by the service and administrators, and subject to the same squat
   checks.
5. **The secret store's writer (revises ADR-0015).** Machine-wide writes of `secrets.dat`
   (`SecureFile.WriteMachineShared`) grant only `SYSTEM` and `Administrators`. They no longer grant
   the writing account. The per-user fallback keeps its current-user-only ACL.
6. **Model files are pinned.** The BGE embedding files get a pinned SHA-256 in `EmbeddingOptions`.
   Migration adopts an embedding file only if it matches, and every download is checked against the
   same hash.
7. **Secure deletion on every database** (plan decision 6). `OpenConnection` sets
   `PRAGMA secure_delete=ON` on every connection, for every router database. Every delete path ends
   with `wal_checkpoint(TRUNCATE)` and treats a `busy` result as "not yet final": retention retries
   on its next cycle, Clear retries a few times and reports a non-final deletion, and startup
   truncates once before anything else opens the database. A one-time `VACUUM` and `TRUNCATE` scrub
   cleans pages freed before the upgrade. Its temp copy goes to disk inside the protected root, after
   a free-space preflight, and it is deferred, never skipped. These are also the implementation rules
   for ADR-0019's key deletion.
8. **`synchronous=NORMAL` on every connection** (plan decision 8). It moves into `OpenConnection` for
   the same per-connection reason.
9. **Logs.** The default level becomes `Information`. The four conversation-bearing templates go
   behind an opt-in switch, to their own body files, obscured and filtered out of every other sink.
   Clear and uninstall delete those files, and the upgrade rewrites pre-upgrade logs.
10. **Uninstall crypto-shreds conversations** (plan decision 7). A `--shred-conversations` command runs
    on every platform's genuine uninstall, with no opt-in. It destroys conversations, and keeps the
    spend databases and settings. Its master-key step waits for ADR-0019, and until then it clears
    `request_transcripts` and runs the scrub.
11. **API reads are out of scope here.** Conversation text over the API is gated by ADR-0020 (#185).
    This ADR's boundary is its hard prerequisite: ADR-0020 relies on only `SYSTEM` and
    `Administrators` being able to change the secret store.

### Consequences

- Good, because the WAL, the `-shm` file, logs, models, and ADR-0019's session folder are all
  protected by inheritance, with no per-file code to forget.
- Good, because a planted `appsettings.local.json` or model file is never loaded (#193).
- Good, because deleted conversation text is actually gone from the WAL, the free pages and the logs,
  and uninstall no longer leaves conversations behind.
- Good, because one verification helper serves the root and, later, ADR-0019's session folder.
- Bad, because editing `appsettings.local.json`, reading `logs\`, or deleting the folder by hand now
  needs elevation. The Console tab still streams logs.
- Bad, because an unelevated dev run can no longer share the service's directory. It uses the
  per-user fallback, which every app of that user can read.
- Bad, because migration is a large, crash-safe, multi-platform procedure (journal, quarantine,
  writer checks), and the installers must now stop the service reliably before it runs.
- Bad, because an existing overlay is set aside on upgrade. Its settings stop applying until an
  administrator reviews it and copies it back.
- Bad, because `secure_delete` and the checkpoints add write I/O, and `synchronous=NORMAL` can lose
  the last few commits on a power cut (it cannot corrupt the database).
- Neutral, because ADR-0015's writing-account rule is revised, not superseded. The rest of ADR-0015
  (`LocalMachine` DPAPI and the administrator-only ACL) stands.
- Neutral, because the uninstall master-key step and the second scrub wait for ADR-0019.

## Pros and Cons of the Options

### A. One protected, inheritable DACL on the whole directory

- Good, because SQLite's re-created side files and every future file inherit it (verified in the
  probe).
- Good, because it closes the squatting in the root, `logs\` and `models\`, as well as the reads.
- Good, because it maps directly to owner-only modes on Linux and macOS.
- Bad, because the existing tree cannot be trusted, so it needs a real migration, and the tray's and
  clients' public files need explicit exceptions.

### B. ADR-0015's per-file ACL on the database files

- Good, because it reuses an existing, proven pattern.
- Bad, because SQLite re-creates `-wal` and `-shm` with the folder's ACL, so the text-bearing WAL is
  readable again after every last close (verified in the probe).
- Bad, because it leaves the logs, `.pfx` files and other databases readable, and the overlay
  squatting open.

### C. Protect only ADR-0019's session folder

- Good, because it is smaller, and ADR-0019 already plans it.
- Bad, because `transcripts.db` (which holds text until #165 phase 2), the logs, `.pfx` files and the
  overlay all stay open.
- Bad, because a standard account can create the session folder before the router does, since the
  root is writable.

### D. `secure_delete=ON` on every connection, plus `TRUNCATE` after each delete

- Good, because it left zero residue in every harness scenario.
- Good, because ADR-0019 already requires `secure_delete` for wrapped keys, so this only makes it
  per connection.
- Bad, because `ON` cannot clean pages freed before it was enabled, which needs a one-time scrub.
- Bad, because a checkpoint can be `busy`, so every caller must handle a deletion that is not yet
  final.

### E. `VACUUM` after each purge

- Good, because it also cleans pages freed earlier.
- Bad, because it rewrites the whole database every 5-minute retention cycle, and spills a full copy
  through a temp file, which needs twice the database's size in free space.
- Used only for the one-time scrub, with its temp copy in the protected root.

## More Information

- Plan and evidence: [`docs/plans/issue-184-transcript-data-protection.md`](../plans/issue-184-transcript-data-protection.md),
  §1 (findings F1–F12 and the residue table), §3 (design), §5 (phasing) and §7 (decisions).
- Issues: [#184](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/184),
  [#193](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/193) (overlay planting),
  [#185](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/185) (ADR-0020's gate).
- Related: [ADR-0012](0012-loopback-session-auth-and-token-in-secret-store.md),
  [ADR-0014](0014-cross-platform-service-layout-and-secret-backend.md),
  [ADR-0015](0015-machine-scoped-protection-for-the-shared-secret-store.md) (writer rule revised
  here), [ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md),
  [ADR-0020](0020-require-passkey-verification-for-conversation-content.md).
- Phasing: phase 1 (the boundary, rules 1–6) lands before #165 phase 1, so ADR-0019's session folder
  inherits the protected root. Phases 2–4 cover rules 7–10.
