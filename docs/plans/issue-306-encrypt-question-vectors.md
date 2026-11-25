# Plan: encrypt question vectors (#306)

**Status:** Approved by David on 2026-11-24 (Cursor chat: address the open issues, then sign-off of ADR-0029 and this file). Implementation follows this file.

**Issue:** [#306](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/306).

## Decision

`memory_entries.embedding` is encrypted with the session master key (`ISessionMasterKeyStore`, ADR-0019), using AES-256-GCM. kNN keeps running on the in-memory plaintext copy. Decryption happens only in `EmbeddingMemory.InitializeAsync` and in the trainers that already load those rows. No new public method is added to `ManagementFacade`.

## What the implementation pull request will do, after sign-off

- Write new and rewritten blobs as ciphertext. A row whose blob is still raw float32 still loads, so existing databases keep their neighbors until each row is rewritten.
- Rewrap or drop the blobs in the same pass that rotates the session master key. A rotation that skipped them would leave `EmbeddingMemory` empty.
- Keep the request path on the in-memory copy. It does not decrypt, and it does not open session files to search.
- Add tests that a fresh row is not readable as float32 without the master key, that `InitializeAsync` still returns the same neighbors, and that a rotation without the rewrap leaves no usable vectors.

## Out of scope

- A new public method on `ManagementFacade`.
- Decrypting on the request path, or opening a session file to search.
