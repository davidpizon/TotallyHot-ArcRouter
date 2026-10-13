# 0025. Serve persisted turn details and a session aggregate on a metadata-only RPC

**Status:** accepted (accepted by David Pizon on 2026-10-13)
**Date:** 2026-10-13
**Deciders:** David Pizon

## Context and Problem Statement

The Sessions tab's three-pane layout (#176 Phase 1) shows persisted history from one call,
`TelemetryService.ListPersistedSessions` ([`src/Protos/telemetry.proto`](../../src/Protos/telemetry.proto)).
That call returns the newest 500 `request_transcripts` rows across all sessions, with a handful of columns each
(models, cost, tokens, `memory_entry_id`, text lengths). It cannot supply what the details pane needs for a
persisted turn: classification (`dimension`, `difficulty`, `language`), score and scorer version, exploration
propensity, the untrained-baseline fields, or the turn's `taxonomy_comparisons` row (baseline cost, estimated
savings, regret). It also stops at 500 rows, so a long session's older turns and its true totals are unreachable.

Adding a second RPC changes the gRPC contract, which `AGENTS.md` sends through an ADR ("Fold, don't fork").
The approved plan ([`docs/plans/issue-176-sessions-three-pane.md`](../plans/issue-176-sessions-three-pane.md)
§5.2) already named two RPCs, but it was written before #165 moved conversation text into encrypted per-session
files ([ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md)) behind a passkey grant
([ADR-0020](0020-require-passkey-verification-for-conversation-content.md)). Today `prompt_text` and
`response_text` are NULL in SQLite, `GetTurnTexts` already exists and returns 2,000-character previews
([ADR-0023](0023-send-display-previews-under-a-byte-budget-on-the-persisted-session-list.md)), and the stored
`prompt_text_length` / `response_text_length` columns exist. So the plan's second RPC (a text RPC with a
262,144-character cap) is obsolete, and only the metadata RPC remains to be decided. David chose on 2026-10-13
to leave text out of Phase 2.

## Decision Drivers

- **No new kind of data leaves the router**: the new RPC must carry no conversation text, so it needs no
  passkey grant beyond what `ListPersistedSessions`' metadata already has (ADR-0020).
- **Bounded message**: a response must fit the GUI's 4 MiB gRPC receive cap for any stored value, as in ADR-0023.
- **Honest totals**: session figures must say they cover retained rows only, because retention deletes old
  `request_transcripts` rows.
- **Small, compatible change**: additive proto only; nothing existing is renumbered or reinterpreted.
- **One consistent snapshot**: counts and sums on the aggregate must come from the same database state.

## Considered Options

- A. One new RPC, `GetSessionTurnDetails`: keyset-paged metadata for one session plus a first-page aggregate
- B. Widen `ListPersistedSessions` and `PersistedTranscript` with the extra columns
- C. Compute everything in the GUI from the newest-500 list
- D. A second text RPC as well (the approved plan's §5.2 design)

## Decision Outcome

Chosen option: "A. One new RPC, `GetSessionTurnDetails`", because it is the only option that meets
**No new kind of data leaves the router** and **Honest totals** together: the aggregate must be computed in SQL
over all retained rows of a session, which neither a widened list (B) nor the GUI (C) can do, and text stays out
(D is dropped).

The contract gains, additively, one RPC and these messages: `GetSessionTurnDetailsRequest`,
`GetSessionTurnDetailsResponse`, `SessionTurnDetail`, `BaselineComparison`, `SessionAggregate`,
`SessionModelUsage`. Field-level shapes, caps (1,024-character free-form strings, 2 MiB page budget) and the
paging rule are specified in [`docs/plans/issue-176-phase2-persisted-turn-details.md`](../plans/issue-176-phase2-persisted-turn-details.md) §4.
`ListPersistedSessions`, `GetTurnTexts`, and every existing field are unchanged.

### Consequences

- Good, because the details pane shows classification, score, and baseline comparison for persisted turns, and a
  session reaches past the newest 500 rows by paging.
- Good, because the aggregate is one read transaction, so `compared_turns` can never exceed `persisted_turns`.
- Good, because text lengths ride the existing stored columns, so a row can say "2,000 of 18,432 characters"
  without a text call.
- Bad, because `SqliteTranscriptStore` gains a read that also touches `taxonomy_comparisons`, the one cross-table
  read in that store.
- Bad, because `taxonomy_comparisons` has no foreign key to `request_transcripts`, so the aggregate joins on
  `transcript_id` to ignore orphans; the join cost grows with session size.
- Neutral, because full-text Show more stays at today's 2,000-character previews; a larger on-demand cap is a
  later decision with its own ADR.

## Pros and Cons of the Options

### A. One new RPC, `GetSessionTurnDetails`

- Good, because metadata is paged and size-bounded independently of the 500-row list.
- Good, because the aggregate and the first page share one call and one snapshot.
- Bad, because it adds a public RPC and a GUI client method to maintain.

### B. Widen `ListPersistedSessions`

- Good, because no new RPC.
- Bad, because the list is capped at 500 rows across all sessions, so older turns and true totals stay unreachable.
- Bad, because every extra column spends the 3 MiB budget ADR-0023 set for the rail's data.

### C. Compute everything in the GUI from the newest-500 list

- Good, because no router change.
- Bad, because the list lacks the classification and comparison columns, and totals would silently understate long sessions.

### D. A second text RPC as well

- Good, because Show more could reach the full body.
- Bad, because it reads the encrypted session files, which is the passkey-gated path of ADR-0020 and a separate decision.
- Bad, because the plan's column-based design (`substr(prompt_text, …)`) no longer works: those columns are NULL.

## More Information

- Plan: [`docs/plans/issue-176-phase2-persisted-turn-details.md`](../plans/issue-176-phase2-persisted-turn-details.md)
- Approved parent plan: [`docs/plans/issue-176-sessions-three-pane.md`](../plans/issue-176-sessions-three-pane.md) §5.2–§5.4
- Issue: [#176](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/176)

## Amendment 1 (2026-10-13): record whether a reply carried tool calls

**Status:** proposed

The Sessions details pane cannot tell a tool-call-only reply from an unreadable row. A stored
`response_text_length` of `0` covers both, and nothing on the `request_transcripts` row flags tool calls. Showing
"—" for both hides a common, legitimate case (agent turns that only call tools). David chose on 2026-10-13 to
record it.

**Decision.** Add a nullable `has_tool_calls INTEGER` column to `request_transcripts` (migrated with
`AddColumnIfMissing`, no backfill), write it when the response is captured, and carry it on
`SessionTurnDetail` as `optional bool has_tool_calls`. NULL means unknown: rows captured before this change,
imported turns, providers with no `ProviderRegistration`, and bodies that could not be parsed. It is never
guessed as `false`.

**Detection.** `RequestTelemetryPublisher.PersistTranscriptAsync` receives the flag from the response parsers
that already read the buffered body (`OpenAiResponseTextParser`, `AnthropicResponseTextParser`). Every translated
provider (`gemini`, `ollama`, the Bedrock providers) reaches the OpenAI-shaped parser because its translator
converts the response before capture, and native `anthropic` reaches the Anthropic parser, so two parsers
cover all registered providers.

**Consequences.**

- Good, because the details pane can label "Tool call (no text)" accurately.
- Bad, because this is a write on the request path next to `ProxyMiddleware`, so it needs the golden-path smoke
  and static Serilog templates (AGENTS.md safety rules).
- Bad, because rows captured earlier stay unknown for good: the response body is not in SQLite.
- Neutral, because it adds one column and one optional proto field, both additive.

The plan's §4.2 and §4.4 specify the files and labels.
