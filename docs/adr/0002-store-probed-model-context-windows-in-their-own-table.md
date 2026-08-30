# 0002. Store probed model context windows in their own table

**Status:** proposed
**Date:** 2026-08-29
**Deciders:** David Pizon

## Context and Problem Statement

To answer Ollama's `POST /api/show` with a real `model_info.context_length`, the router needs to persist a
context window per `(provider, model)` — see
[ollama-show-capabilities-plan.md](../router/ollama-show-capabilities-plan.md). That key is already the
primary key of `model_tool_capabilities`, and the value is sourced from the same upstream probe
(`ModelDialectResolver`) that populates that table, so adding a `context_length` column to it is the
obvious first move.

It is also unsafe. The two pieces of data share a key and a producer but have different *write
lifecycles*, and `model_tool_capabilities` is written from two paths that know nothing about a context
window. Whether they share a row is a storage-format decision: once installed databases carry rows, moving
the column later means a migration plus a backfill from a re-probe.

## Decision Drivers

- A write from one subsystem must not destroy data owned by another — the request path writes tool-call
  observations continuously and has no context-window value to supply.
- An operator action must not have unrelated destructive side effects.
- Upgrade safety on existing installs: the schema change must apply identically to fresh and existing
  databases.
- Reversal cost is high once shipped, so the corruption paths must be ruled out by structure rather than
  by careful coding.

## Considered Options

- Add a `context_length` column to `model_tool_capabilities`
- Add the column, and defend it with `COALESCE` on every write that doesn't supply it
- A separate `model_context_windows` table keyed the same way

## Decision Outcome

Chosen option: "A separate `model_context_windows` table", because of the first two drivers — two concrete
corruption paths exist in current code, and only table separation removes them structurally rather than by
convention:

- [`ToolCallObservationRecorder.cs:84`](../../src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallObservationRecorder.cs)
  constructs a **fresh** `ModelToolCapability` from the request path — with no knowledge of any context
  window — and `ToolCallCapabilityRepository.TryUpsertModelCapability` overwrites the columns it names. The
  first live tool-call observation for a model would null out its probed context length.
- `ToolCallCapabilityStore.ClearModelCapability` issues `DELETE FROM model_tool_capabilities`. An operator
  resetting a dialect override back to automatic — a deliberately narrow action — would silently destroy an
  unrelated probed value.

The shared key and shared producer argue for one table; the divergent write lifecycle argues for two, and
the write lifecycle is what actually decides a table boundary.

A second, smaller consideration reinforced it: a brand-new table needs **no** additive-column migration. The
`MigrateCacheWriteInputPriceColumn` pattern (`PriceCatalogDatabase.cs:161-171`) exists only because
`CREATE TABLE IF NOT EXISTS` cannot add a column to a table that already exists. One `SchemaSql` entry
covers fresh and upgraded databases identically, satisfying the third driver for free.

The same write-lifecycle reasoning settles a related question: `DetectionConfidence` does **not** gate
context-window writes. That ladder ranks *how a classification was learned* so a filename guess cannot
overwrite a template read. Context length has no such ladder — Ollama's `model_info` and LM Studio's
`max_context_length` are peers, and nothing guesses a context length from a model id. A gate would also be
harmful: a model reloaded under a different `num_ctx` genuinely has a different window, and a `>=` gate
would freeze the first reading forever. Writes are therefore unconditional, last-write-wins, with one
invariant — a probe that read nothing writes nothing, so a failed re-probe never clears a known value. This
mirrors `ToolCallCapabilityStore.SetProviderCapabilities`, whose remarks make the same argument for
endpoint flavors.

### Consequences

- Good, because neither a live tool-call observation nor an operator dialect reset can touch a probed
  context window — the isolation is structural, so a future contributor cannot reintroduce the bug by
  adding a column to an existing upsert.
- Good, because no schema migration is needed; `EnsureCreated` is unchanged.
- Bad, because two tables now share a `(provider_key, model_name)` key and are populated from one probe,
  which reads as redundant to anyone who hasn't hit the corruption paths. The table needs a comment
  pointing here, and this ADR is the reason it exists.
- Bad, because a scan now performs two writes and two `Reload()` passes per model instead of one. At
  personal scale against local SQLite this is immaterial; it would need batching if the model count grew by
  an order of magnitude.
- Neutral, because it locks in "unknown context length is the absence of a row" as the representation.
  That is what lets `/api/show` omit `model_info` rather than fabricate a default, and it is why
  `ModelContextWindow.ContextLength` is a non-nullable `int`.

## Pros and Cons of the Options

### Add a `context_length` column to `model_tool_capabilities`

- Good, because one table, one key, one read — the smallest possible change, and the two values genuinely
  do come from the same probe.
- Good, because the existing `Reload()` snapshot would carry it with no new dictionary.
- Bad, because `ToolCallObservationRecorder`'s request-path write would null it on the first observed tool
  call. This is not hypothetical; that path constructs the record from scratch today.
- Bad, because `ClearModelCapability`'s `DELETE` would destroy it as a side effect of an unrelated
  operator action.
- Bad, because it requires an additive-column migration on existing databases.

### Add the column, defended with `COALESCE` on every partial write

- Good, because it keeps the single-table simplicity while blocking the null-out path.
- Bad, because it is preserve-on-write by convention: correctness depends on every future writer
  remembering the `COALESCE`, and the compiler cannot enforce it.
- Bad, because it makes the value unclearable — there is then no way to express "this probe found
  nothing" as distinct from "this writer had nothing to say".
- Bad, because it does not address `ClearModelCapability`'s `DELETE` at all; that path would still need a
  separate carve-out.

### A separate `model_context_windows` table

- Good, because both corruption paths become impossible by construction rather than by discipline.
- Good, because a new table needs no migration — `CREATE TABLE IF NOT EXISTS` covers fresh and existing
  databases identically.
- Good, because it leaves the meaning of `model_tool_capabilities` intact: it stays a record of tool-call
  dialect detection, not a general per-model metadata bag.
- Bad, because it adds a third snapshot dictionary to `ToolCallCapabilityStore` and a second write per
  scanned model.
- Bad, because the duplicated key looks like a normalization mistake without this record.

## More Information

Full design, including the probe restructuring that makes the context window survive all six
`ModelDialectResolver.ResolveAsync` exit paths, is in
[ollama-show-capabilities-plan.md](../router/ollama-show-capabilities-plan.md). The wire-format side of the
same change — what `/api/show` declares for a model whose tool support is emulated or unknown — is
[ADR-0003](0003-declare-tool-support-for-emulated-and-unclassified-models.md).
