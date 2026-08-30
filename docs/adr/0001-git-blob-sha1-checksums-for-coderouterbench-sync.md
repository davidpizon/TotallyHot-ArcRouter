# 0001. Use git blob SHA-1, not MD5, to verify synced CodeRouterBench files

**Status:** accepted
**Date:** 2026-08-29
**Deciders:** David Pizon

## Context and Problem Statement

CodeRouterBench's task/result corpus is synced on demand from Hugging Face into a local SQLite
database rather than checked into the repo (see
[coderouterbench-sqlite-migration-plan.md](../router/coderouterbench-sqlite-migration-plan.md)).
Every sync needs an integrity check: a way to confirm a downloaded file matches what upstream
published, and a way to detect staleness at startup without re-downloading the ~12 MB corpus. The
original design called for comparing MD5 hashes, the most common choice for this kind of check.

## Decision Drivers

- The check must be cheap enough to run at every application startup (no full-corpus download
  just to learn whether it's current).
- The reference hash must come from an API upstream actually publishes — computing our own
  reference value defeats the purpose of an independent check.
- No new bash/external-tool dependency, consistent with this being a Windows-first application.

## Considered Options

- MD5, computed locally from a full download and compared against a separately obtained reference
- Git blob SHA-1, computed locally and compared against the tree API's published `oid`
- Skip integrity verification; trust the download and rely on row-count assertions alone

## Decision Outcome

Chosen option: "Git blob SHA-1", because it is the only option that is both independently
published *and* cheap to check. Neither Hugging Face nor GitHub publishes MD5 for these files, so
obtaining a reference MD5 would mean downloading the full corpus — costing exactly what the check
is meant to avoid. The git blob SHA-1, by contrast, is returned for every file in one call to
Hugging Face's tree API (`GET /api/datasets/{id}/tree/main`), is content-derived so it changes
whenever the file changes, and is recomputable locally from raw bytes as
`SHA1("blob " + length + "\0" + bytes)`. This was verified end to end against the live dataset
before adoption (see the migration plan's [Checksums](../router/coderouterbench-sqlite-migration-plan.md#checksums-what-we-compare-and-why)
section for the worked example).

### Consequences

- Good, because the startup staleness probe is a single HTTP request regardless of file count,
  and never requires downloading the corpus just to check it.
- Good, because the reference value is upstream's own published hash, not something this project
  computes and could get out of sync with.
- Bad, because every column, field, log message, and UI label naming this hash must say
  "SHA-1" or "checksum," never "MD5" — the original spec's naming had to be corrected everywhere
  it appeared.
- Neutral, because it ties the integrity check to git's blob-hashing scheme specifically; this is
  fine as long as Hugging Face and the GitHub mirror both expose it, which they do today.

## Pros and Cons of the Options

### MD5, computed locally from a full download

- Good, because it is the most familiar checksum scheme.
- Bad, because no reference MD5 is published anywhere for these files — obtaining one would
  require downloading the ~12 MB corpus, which is the exact cost the check exists to avoid.

### Git blob SHA-1

- Good, because it is already published, for every file, in one HTTP call.
- Good, because it is cheap to recompute locally from raw bytes with a well-known formula.
- Bad, because it is git-specific — an upstream host that doesn't expose blob SHA-1s (via a tree
  API or otherwise) wouldn't support this scheme.

### Skip verification, rely on row-count assertions alone

- Good, because it is the simplest option and requires no hashing at all.
- Bad, because row counts alone don't catch content corruption or a stale-but-same-size file;
  the existing `fetch-coderouterbench.sh` already relied on row-count assertions and that alone
  was judged insufficient.

## More Information

Implemented in `TotallyHot.ArcRouter.CodeRouterBench.PublishedChecksumHasher` and
`BenchmarkChecksumProbe`, following the same pattern already in production for router-model
checksums (`LlmRouterModelChecksumProbe`). See
[coderouterbench-sqlite-migration-plan.md](../router/coderouterbench-sqlite-migration-plan.md) for
the full sync design this decision is part of.
