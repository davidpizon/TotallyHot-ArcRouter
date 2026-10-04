# 0023. Send display previews under a byte budget on the persisted-session list

**Status:** proposed
**Date:** 2026-10-03
**Deciders:** David Pizon

## Context and Problem Statement

The GUI's Sessions tab loads persisted history with one `TelemetryService.ListPersistedSessions` call
([`src/Protos/telemetry.proto`](../../src/Protos/telemetry.proto)) for the newest 500 `request_transcripts`
rows. Each `PersistedTranscript` carries the row's full `prompt_text` (field 6) and `response_text` (field 7).
The router sets no send cap, but the GUI's gRPC-Web channel keeps `Grpc.Net.Client`'s default 4,194,304-byte
receive cap. So the load fails with `ResourceExhausted` once the 500 rows average more than about 8,244
bytes of text, and `AdminStoreBase.LoadGuardedAsync` swallows the failure. The tab then shows live sessions
only and no error ([#179](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/179); measurements in
[`docs/plans/issue-179-persisted-sessions-list-size.md`](../plans/issue-179-persisted-sessions-list-size.md) §2).

This is a decision rather than a bug fix because the obvious fixes change what a field on the router/GUI
transport contract means. Fields 6 and 7 have so far meant "the stored text". After the chosen fix they mean
"a display preview of the stored text". AGENTS.md sends transport-contract changes through an ADR, and
David asked for one on 2026-10-03, before Phase 1. One more force: David's requirement of 2026-09-30.
Storage keeps the full session text, #165's export and import carry it unaltered, and the Sessions tab may
show truncated or load-on-demand text, which he prefers for performance.

## Decision Drivers

- **Bounded message**: every response must fit the client's receive cap whatever the text size, the
  script (UTF-8 bytes per character), or the row limit. A cap that one large row can break is no fix.
- **Full text stays stored**: no write path changes, and nothing on a display path can reach #165's
  export or import.
- **Honest display**: a cut value must say it was cut, and a failed load must be visible.
- **Load-on-demand ready**: #176's planned `GetTurnTexts` must be able to fetch more of a row's text
  without another contract change.
- **Small, compatible change**: additive proto fields only. The router serves the WASM GUI, so both ends
  always ship together.

## Considered Options

- A. Server-side previews plus a response byte budget, reusing fields 6 and 7 for previews
- A2. Like A, but with new preview fields beside fields 6 and 7, which would stay full text
- B. Lower the GUI's row limit
- C. Paginate the list
- D. Raise the client's `MaxReceiveMessageSize`
- E. Metadata-only list, with text loaded on demand

## Decision Outcome

Chosen option: "A. Server-side previews plus a response byte budget, reusing fields 6 and 7", because it is
the only option available now that satisfies **Bounded message** for any input. The 2,000-character preview
(`TextTruncator.DefaultMaxLength`, the same cap live telemetry uses) keeps the typical list small. The
3 MiB budget (`3 * 1024 * 1024`, counted with `CodedOutputStream.ComputeMessageSize` per row) is the hard
guarantee, and it covers CJK text and client-supplied metadata strings that a character cap alone misses.
It meets **Full text stays stored** because truncation happens only in `TelemetryGrpcService`'s mapping
of the response. Reusing fields 6 and 7 meets **Small, compatible change**: no field sits unused, and no
deployed GUI reads full text from them that a router of another version could disagree with.

The contract gains, additively:

- On `PersistedTranscript`: `transcript_id` (12), `prompt_text_length` (13), `response_text_length` (14),
  `prompt_truncated` (15), and `response_truncated` (16).
  - The lengths are SQLite `length()` character counts. They are displayed, never compared.
  - The explicit flags, not a length comparison, say whether a preview was cut.
- On `ListPersistedSessionsResponse`: `has_more` (3). It is set when the limit or the budget left out
  older rows.

`ListPersistedSessionsRequest.limit` is clamped: 0 becomes 500, and anything else is clamped to
[1, 2,000].

Option E stays the end state. Once ADR-0019's encrypted per-session files exist, the list sends metadata
only, and a session's text is read from its file when the session is opened.

### Consequences

- Good, because the Sessions tab loads any persisted history, including a single 4 MiB prompt.
- Good, because the payload a page load downloads shrinks to a few MB at most, which suits David's
  stated performance preference.
- Good, because `transcript_id`, the truncation flags, and the lengths let #176's Show more fetch the
  rest of a turn directly.
- Bad, because a persisted turn longer than 2,000 characters can't be expanded in the GUI until #176's
  `GetTurnTexts` ships. Live turns have the same limit today.
- Bad, because the budget can hide older rows on histories heavy in non-ASCII text. `has_more` and a
  visible "Showing the newest N persisted turns." line report it. In the worst case about 258 rows
  still fit.
- Bad, because the router still reads each row's full text from SQLite before cutting it. That is
  today's read cost, kept for simplicity (plan decision 9).
- Neutral, because fields 6 and 7 now carry previews. Anything that needs the stored text must use a
  bounded per-row RPC or #165's archive, never this list. The proto comments say so.

## Pros and Cons of the Options

### A. Previews plus byte budget, reusing fields 6 and 7

- Good, because the byte budget bounds the message for any text, any script, and any limit.
- Good, because persisted previews match live previews character for character.
- Good, because the change is additive on the wire, with no unused fields.
- Bad, because the meaning of two existing fields changes. That is acceptable only because router and GUI
  always ship together.

### A2. Previews in new fields, fields 6 and 7 kept as full text

- Good, because no existing field changes meaning.
- Bad, because fields 6 and 7 would have to stay empty to stay under the cap, so they'd remain on the
  contract with a meaning nothing honours.
- Bad, because it adds fields and mapping for no reader that needs them.

### B. Lower the row limit

- Good, because it is one constant in `PersistedSessionStore`.
- Bad, because one large row still fails at any limit, so it doesn't bound the message.
- Bad, because every user loses history, including those who never hit the cap.

### C. Paginate

- Good, because it would let the GUI reach older history.
- Bad, because each page still needs a byte budget to be bounded, which is option A anyway.
- Bad, because it still transfers full text the tab mostly doesn't show. It can be added on top of A later
  as an additive `before_id`.

### D. Raise `MaxReceiveMessageSize`

- Good, because it is one line in `WasmRouterChannelProvider` and `TelemetryChannelFactory`.
- Bad, because any finite cap can still be exceeded, and an unlimited cap means tens of MB deserialized on
  the browser's UI thread on every page load.
- Bad, because it raises the cap for every RPC on the shared channel. #176's plan §14 excludes it.

### E. Metadata-only list, text on demand

- Good, because it fits David's load-on-demand preference best, with a list of roughly 75 KB.
- Bad, because today's Sessions tab renders turn text from this list, so it breaks the tab until #176's
  per-turn text RPC ships.

## More Information

- Plan and decisions: [`docs/plans/issue-179-persisted-sessions-list-size.md`](../plans/issue-179-persisted-sessions-list-size.md),
  approved by David on 2026-10-03 with every §10 decision at its default.
- Storage direction and end state: [ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md).
- The GUI transport this contract runs on: [ADR-0011](0011-router-served-blazor-webassembly-gui-over-grpc-web.md).
- Related plan: [`docs/plans/issue-176-sessions-three-pane.md`](../plans/issue-176-sessions-three-pane.md),
  whose `GetTurnTexts` consumes `transcript_id`.
