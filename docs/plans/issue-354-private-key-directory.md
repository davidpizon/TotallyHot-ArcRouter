# Plan: #354 secret-key directory permission race

**Status:** Approved by David on 2026-11-24 (Cursor chat: address all open issues).

**Issue:** [#354](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/354).

**Observed cost:** One failure in 50 local loop runs of the grader epoch classes. `EnsureKeyDirectorySecure` created the shared key directory and chmod'd it to `0700`. A second caller read mode `0755` in that gap.

## Decision

The reported failure is test isolation. Each grader world already uses its own `secrets.dat`. The Unix key ring followed the process-wide data directory instead, so every world shared one `keys` folder.

Production callers use the default store, `secrets.dat` in the machine-shared directory, so their ring stays `<data-dir>/keys`. The mode policy is unchanged: create at `0700`, refuse a directory that is already broader. A process-local lock covers the create-then-chmod gap inside one process. A second process can still lose that race; that is not the failure this issue recorded.

## Change

`ProtectedSecretStore.KeyRingDirectory` returns a `keys` directory beside the store file. `CreateUnixProtector` uses it. `KeyRingDirectory_FollowsTheStoreFile` locks the split.
