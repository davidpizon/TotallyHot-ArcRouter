# 0029. Encrypt question vectors with the session master key

**Status:** proposed
**Date:** 2026-11-24
**Deciders:** David Pizon (proposal; not yet accepted)

## Context and Problem Statement

`memory_entries.embedding` is a raw float32 BLOB. Those vectors are the questions the router has already
seen, and a neighbor search over them recovers which questions were asked. Conversation text for the same
turns is AES-256-GCM under the session master key ([ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md))
and leaves the process only with a passkey grant ([ADR-0020](0020-require-passkey-verification-for-conversation-content.md)).
The embedding column did not follow that storage change. A copy of the SQLite file still answers "what was
this user asking about" without the master key.

This is a storage-format change. kNN still has to run on every routed request, so the format also has to
say where decryption is allowed to happen. [#306](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/306).

## Decision Drivers

- A disk copy of `memory_entries` must not be readable as question vectors once the session master key is gone.
- kNN stays on the in-memory copy. Decrypting inside the request path, or opening a session file per neighbor, is not acceptable.
- No new public method on `ManagementFacade`.
- Existing numeric rows stay. The change is the blob format, not a new table.

## Considered Options

- Encrypt the BLOB with the existing session master key, and decrypt only while `EmbeddingMemory.InitializeAsync` and the trainers that already load rows are building their in-memory copy.
- Leave `memory_entries.embedding` as a raw float32 BLOB.
- Move each vector into the encrypted session file for that turn and read it back from the file at search time.

## Decision Outcome

Chosen option: "Encrypt the BLOB with the existing session master key", because the driver that a disk copy
must not yield question vectors is the one a raw BLOB fails, and the driver that kNN stay in memory is the
one reading session files at search time fails. The master key already exists for session text
(`ISessionMasterKeyStore`); this decision does not add a second key.

### Consequences

- Good, because deleting or rotating the session master key makes the vectors unreadable in the same way it makes session text unreadable.
- Bad, because a rotation that does not re-encrypt the blobs in the same pass leaves `EmbeddingMemory` empty until the rows are rewritten. The implementation plan has to include that pass.
- Neutral, because kNN math does not change. It still runs on the plaintext floats after load. `ManagementFacade`'s public methods stay as they are.

## Pros and Cons of the Options

### Encrypt the BLOB with the existing session master key

Decrypt inside `EmbeddingMemory.InitializeAsync` and inside the trainers that already load `memory_entries` rows. The request path searches the in-memory copy and does not touch the blob.

- Good, because one key wraps session text and question vectors, and the hot path does not decrypt.
- Bad, because startup and each trainer load pay a decrypt per row, and a master-key rotation must rewrap or drop the rows.

### Leave the BLOB as raw float32

- Good, because nothing in the load path or the rotation path changes.
- Bad, because a copied database still exposes the question vectors after the conversation text has been made unreadable.

### Store the vectors in the encrypted session files

- Good, because the bytes would live in the store ADR-0019 already specified, with no second ciphertext format.
- Bad, because neighbor search would open session files, which is the cost ADR-0019 kept off the routing path by leaving SQLite as an index.

## More Information

Implementation waits for sign-off. The plan is
[docs/plans/issue-306-encrypt-question-vectors.md](../plans/issue-306-encrypt-question-vectors.md).
This pull request is the ADR and that plan. It does not change the blob.
