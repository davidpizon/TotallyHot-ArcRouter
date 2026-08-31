# Sessions Tab: Persisted, Training-Linked Transcripts

Makes the GUI's Sessions tab (`src/TotallyHotArcRouter.Gui/Components/LiveStream.razor`, renamed from
"Live Stream") show the router's actual training corpus, not just whatever happened to stream over gRPC
while the GUI was open. Today the tab is fed exclusively by `LiveDataStore`'s in-memory buffer of the live
`TelemetryService.StreamEvents` RPC — nothing persisted survives a GUI restart, and there is no way to see
which sessions actually fed `EmbeddingMemory`'s live-learning corpus. This plan adds that visibility, and
fixes a layout bug found along the way: the unselected session-card list renders as a multi-column grid
instead of full-width cards.

**Status:** shipped — all four phases complete.

**Phase 4 implementation notes:**
- The bulk of Phase 4's originally-scoped item (unit test coverage for every new symbol) landed
  incrementally alongside Phases 1-3 rather than as a separate pass — see each phase's test counts below.
  What remained for this phase was purely the doc cross-linking: [`../gui/dashboard.md`](../gui/dashboard.md)'s
  Sessions-tab section was rewritten to describe the actual double-click-split-view behavior (it still
  described a permanently-open two-panel layout, stale since before this plan), the merged live+persisted
  data sources, the training badge, and the `TranscriptOptions.Enabled` prerequisite; it also picked up an
  unrelated but adjacent correction — `TurnCard.razor` is not actually instantiated anywhere in the current
  Sessions tab (superseded by `SessionConversationPane.razor`) and the doc previously described it as if it
  were. [`live-feedback-learning-plan.md`](live-feedback-learning-plan.md) got a short section pointing
  here, since that plan owns `memory_entries`'s consumers and this feature's badge is defined in terms of
  `request_transcripts.memory_entry_id IS NOT NULL`, the same linkage that plan's `EmbeddingBackfillService`
  section stamps.

**Phase 2 implementation notes:**
- Persisted history is a **separate** GUI store (`Services/PersistedSessionStore.cs`), not folded into
  `LiveDataStore.Conversations` — that list also backs Cost Analytics, which has no use for persisted
  sessions or the training-data flag, so only `Dashboard.razor`'s new `MergedSessionConversations()` merges
  the two (live wins on session-id collision) before handing the combined list to `LiveStream.razor`;
  `LiveStream.razor` itself is unchanged.
- Split across projects the same way the live path already is: `TotallyHotArcRouter.Gui.Telemetry` gained
  `PersistedTranscriptDto`, `PersistedSessionAggregator` (groups by session, parses turn number from the
  correlation-id suffix client-side — no proto change needed), and `PersistedSessionsClient`
  (`IPersistedSessionsClient`) wrapping the RPC — all unit-testable in CI without MAUI. The
  Windows-only `TotallyHotArcRouter.Gui` project got `PersistedSessionMapper` (parallel to
  `LiveConversationMapper`) and `PersistedSessionStore` (parallel to `RoutingModeStore`'s
  singleton/Changed-event/reachability-tolerant shape).
- `Conversation.IsUsedForTraining` is a new session-level flag (true if any turn's `MemoryEntryId` is set),
  badged on `ConversationCard.razor` next to the existing fallback badge.
- A persisted turn's honestly-defaulted fields (ROI, tool steps, cache-hit rate, TTFT, context %, fallback
  flag, distinct agent name) follow the exact same "explicit default, not fabricated" convention
  `LiveConversationMapper` documents, since `request_transcripts` doesn't capture any of them.

**Phase 1 implementation notes:**
- `ListPersistedSessions` was added to the existing `TelemetryService` rather than a new admin service —
  the GUI already holds an open `TelemetryServiceClient` channel for `StreamEvents`, and this read needs no
  new channel, DI wiring, or `:5002` admin-endpoint registration to reuse it. A deliberate deviation from
  this codebase's usual "one admin surface per feature" convention (`RouterSettingsAdminService`,
  `ClusterModelAdminService`, etc.), noted here per AGENTS.md's deviation rule.
- `request_transcripts.session_id` is a real, indexed, backfilled column (`TranscriptDatabase.MigrateSessionIdColumn`),
  not a `TranscriptRecord` field — the read path (`SqliteTranscriptStore.ListSessionsAsync`) returns a new,
  narrower `SessionTranscript` type instead. This avoided touching `TranscriptRecord`'s ~33 existing call
  sites just to add a field only the new read path needs.
- `ITranscriptStore.ListSessionsAsync` is a C# default interface method (returns empty by default) so the
  eight pre-existing test fakes implementing `ITranscriptStore` didn't all need a new stub added.

## Why

`request_transcripts` (`src/TotallyHotArcRouter/Transcripts/TranscriptDatabase.cs`) already stores every
captured request's prompt/response text, score, cost, and — critically — `memory_entry_id`, which
`SqliteTranscriptStore.LinkMemoryEntryAsync` stamps precisely when that transcript's embedding gets folded
into `EmbeddingMemory.AddEntryAsync` (`memory_entries`, the live-learning corpus per
[`live-feedback-learning-plan.md`](live-feedback-learning-plan.md)). A row with `memory_entry_id IS NOT
NULL` is, by construction, "a saved session transaction ultimately used as live training data." Nothing
today exposes this to the GUI: the GUI has no channel to the proxy's `TranscriptDatabase` at all, only to
the live telemetry stream, and `request_transcripts` itself has no `session_id` column — only
`correlation_id`, formatted `"{sessionId}:{turnNumber}"` — so grouping rows into session cards isn't
possible without parsing that convention (already done once, ad hoc, by
`TaxonomyComparisonService.SessionIdOf`).

## Scope decisions made before implementation

- **Sessions tab shows all persisted `request_transcripts` rows** (subject to `TranscriptOptions.Enabled`
  being on — capture is opt-in), not only training-linked ones. Rows with `memory_entry_id IS NOT NULL`
  are flagged (e.g. a badge on `ConversationCard.razor`, alongside the existing fallback-routing badge) as
  "used for live training," rather than being the only rows shown. This was chosen over showing *only*
  training-linked rows so operators can also see recent traffic that hasn't (yet, or ever) been folded into
  memory — useful context for diagnosing why a session didn't contribute.
- **`session_id` becomes a real, indexed column** on `request_transcripts` rather than parsing
  `correlation_id` on every read. It is populated by the same parsing logic `SessionIdOf` already uses —
  both at insert time and via a one-time backfill migration for existing rows — so no new plumbing is
  needed to thread `SessionId` through the insert call sites; the duplicate parsing logic in
  `TaxonomyComparisonService` should be consolidated into one shared helper both callers use.
- **The live gRPC stream and the persisted store are merged, not replaced.** A session still in flight is
  more current from the live stream (turns arrive incrementally); once persisted, the same session is
  available across GUI restarts. The GUI dedupes by session id, live data winning for any session present
  in both sources.
- **A new unary gRPC RPC is in scope** for this plan (not deferred) — the GUI needs a query surface against
  `TranscriptDatabase` that doesn't exist today.

## Phase 1 — Proxy: schema + query surface

1. **Schema migration** on `TranscriptDatabase`
   (`src/TotallyHotArcRouter/Transcripts/TranscriptDatabase.cs`): add `session_id TEXT NOT NULL DEFAULT
   ''` to `request_transcripts` plus `ix_request_transcripts_session_id`, following the existing
   `MigrateDimBestModelColumn`/`MigrateScorerVersionColumn` pattern. Backfill existing rows by parsing
   `correlation_id` the same way `SessionIdOf` does.
2. **Shared session-id-from-correlation-id helper.** Extract `TaxonomyComparisonService.SessionIdOf`
   (`src/TotallyHotArcRouter/Transcripts/TaxonomyComparisonService.cs:606`) into a small static utility
   both it and `SqliteTranscriptStore.InsertAsync` call, rather than keeping two copies of the same
   `LastIndexOf(':')` parse.
3. **`SqliteTranscriptStore.InsertAsync`**: compute and write `session_id` from `record.CorrelationId` at
   insert time using the shared helper.
4. **New read query** on `ITranscriptStore` / `SqliteTranscriptStore`: a `ListSessionsAsync(...)`-shaped
   method returning transcripts grouped by `session_id`, oldest-first (matching `LiveStream.razor`'s
   current sort), each row carrying `memory_entry_id` so the caller can compute the training-data flag.
   Respect `TranscriptOptions.Enabled` the same live-gate way every other `SqliteTranscriptStore` method
   does.
5. **New unary gRPC RPC** on `TelemetryService` (`src/Protos/telemetry.proto`,
   `src/TotallyHotArcRouter/Telemetry/TelemetryGrpcService.cs`) — e.g. `ListPersistedSessions` — calling
   the new store query and returning session/turn DTOs shaped consistently with the existing
   `RoutingTelemetryEventDto`. When `TranscriptOptions.Enabled` is false, return an explicit
   "capture disabled" signal (not a silent empty list) so the GUI can render an accurate empty state.

## Phase 2 — GUI: load persisted sessions into the Sessions tab

6. **New GUI service**, parallel to `LiveDataStore`/`LiveConversationMapper`
   (`src/TotallyHotArcRouter.Gui/Services/`), that calls `ListPersistedSessions` on tab activation (and on
   an explicit refresh action) and maps rows into the existing `Conversation`/`ConversationTurn` models
   (`src/TotallyHotArcRouter.Gui/Models/DashboardData.cs`), plus a new `IsUsedForTraining` flag sourced from
   `memory_entry_id`.
7. **`LiveStream.razor`**: merge persisted sessions with the existing live in-memory stream, deduping by
   session id with live-data precedence for any session present in both.
8. **`ConversationCard.razor`**: add a "used for live training" badge/indicator alongside the existing
   fallback-routing badge, driven by the new flag.

## Phase 3 — Full-width cards when nothing is selected

9. **CSS fix**, independent of Phases 1–2: `.ls-sessions-grid`
   (`src/TotallyHotArcRouter.Gui/wwwroot/css/app.css:1438`) is currently
   `grid-template-columns: repeat(auto-fill, minmax(260px, 1fr))` — a multi-column grid — despite the
   comment above it already claiming a "full-width card list." Change it to a single-column layout so each
   session card spans the full width of the list before a session is opened via double-click. No markup
   change needed in `LiveStream.razor` beyond this.

## Phase 4 — Tests & docs

10. Unit tests: session-id parsing/backfill migration, the new store query, the new RPC (mirroring
    `TelemetryGrpcServiceTests.cs`), GUI mapping (mirroring `LiveConversationMapperTests.cs`), and a
    `ConversationCardTests`/`LiveStreamTests` check for the training badge and the full-width layout class.
11. Update this doc's status line as phases land, and cross-link from
    [`live-feedback-learning-plan.md`](live-feedback-learning-plan.md) and
    [`../gui/dashboard.md`](../gui/dashboard.md) once the Sessions tab surfaces training-linked transcripts,
    noting the `TranscriptOptions.Enabled` prerequisite.

## Implementation note

Use the **CodeGraph MCP** (`codegraph_explore`) before editing any file in this plan — pull current
verbatim source, call paths, and blast radius rather than grepping/reading cold. (This plan itself was
built that way: discovering `SessionIdOf` already existed avoided designing a redundant
`SessionId`-threading change.) Use **Serena MCP** for the actual symbol-level edits (`find_symbol`,
`replace_symbol_body`, `insert_after_symbol`, etc.) once a target is located, rather than raw text edits,
to keep changes anchored to symbol boundaries across the `.razor`/`.cs` mix touched here.
