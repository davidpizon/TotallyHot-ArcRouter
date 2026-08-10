# Current Implementation Plan: Remaining Work Only

This plan excludes completed phases and tracks only unfinished work.

## Active Phases

### Phase E: Advanced Price Tier Support — **batch/cached done; multimodal deferred on evidence**
- ~~Add batch/cached tier support in schema, ingestion, and lookup estimation.~~ **Done.** The schema and
  ingestion already wrote `batch_input_price`/`batch_output_price`; what was missing was the read half.
  `ModelPrice` gained nullable batch rates, `PriceCatalogRepository` now selects them, and the Phase 4
  read surface (`IModelPriceCatalog`/`ModelPriceCatalog` + `PriceContext`, over a `ConcurrentDictionary`
  cache invalidated by the ingestion service) selects the applicable tier per request. Cached-tier support
  was already complete via `CacheRead`/`CacheWrite` rates and `EstimateCost(UsageInfo)`.
- ~~Add regression tests for tier permutations and graceful fallback behavior.~~ **Done** —
  `ModelPriceCatalogTests` covers every tier permutation plus the load-bearing null case: an unpublished
  tier falls back to the standard rate, never to zero (D7's "absent ≠ free").
- **Multimodal tier support is deferred, and this is a decision rather than a gap.** `multimodal_prices`
  exists as DDL only (`resolution_tier`, `per_step_cost`, `base_image_cost`) and was created
  speculatively. Verified 2026-08-09 against LiteLLM's published
  [pricing field spec](https://docs.litellm.ai/docs/provider_registration/add_model_pricing): it defines
  no image/vision per-token field, and **no upstream feed has a `resolution_tier` concept at all**.
  OpenRouter publishes none either. A survey of the AI-cost-tracking ecosystem (tokscale, cccost,
  claude-usage-tracker, TokenTracker, token-monitor, anthropic-usage-receiver, and the 18 repos under
  GitHub's `ai-cost-tracking` topic) found **not one project that prices multimodal input** — they all
  stop at input/output/cache-read/cache-write. Populating this table today would mean inventing rates,
  which is the fabricated-price pattern this repo already deleted once (see `model-price-catalog.md`'s
  "no hand-maintained price data" banner). Reopen when a source publishes machine-readable image pricing;
  it also needs a per-request image/step count, which `UsageInfo` does not model.
- **Follow-up noted, not scoped here:** LiteLLM publishes `output_cost_per_reasoning_token`, and
  `UsageInfo.ReasoningTokens` already exists with no corresponding price column — reasoning tokens are
  currently billed at the standard output rate.
- Exit: **met** for batch and cached tiers; multimodal explicitly out of scope per the above.

### Phase F: Deferred GUI Backlog Completion — **settings persistence and gRPC auth done; Cost Analytics metrics deferred on evidence**
- ~~Implement GUI settings persistence.~~ **Done.** `GuiSettingsStore` persists the telemetry server
  address as JSON under `%LOCALAPPDATA%\TotallyHotArcRouter\gui-settings.json` (the same per-user
  directory the telemetry certificate and management token already use), editable from a new field
  in `SettingsModal.razor`; `MauiProgram` builds `LiveDataStore` from the persisted address. Reset
  Stats/Clear History are wired to real `LiveDataStore.ClearEvents()`/`ClearLogLines()` calls instead
  of being no-ops, scoped to this session's live view (the proxy's own durable history is untouched
  by design — see `LiveDataStore.ClearEvents`'s remarks).
- ~~Harden loopback gRPC stream behavior (authentication).~~ **Done.** The telemetry gRPC endpoint
  (the `StreamEvents` stream and `PriceSourceAdminService`, which share the TLS port) is now gated
  behind the same shared per-user management token the REST `/admin/*` API and MCP endpoint already
  require — `TelemetryAuthInterceptor` server-side, `TelemetryAuthClientInterceptor` client-side —
  translating `docs/router/signalr-hub-security.md` §2's shared-secret design to a gRPC interceptor,
  as `docs/router/grpc-migration.md` anticipated. Reuses the existing `ManagementAccessToken`
  rather than a new secret file, since one already existed and gates the sibling REST/MCP surfaces
  identically.
- **Add missing telemetry fields for currently mock-backed tabs/metrics — partially done, rest
  explicitly deferred.** The Governance budget-persistence gap `docs/gui/backlog.md` described
  turned out to already be shipped (`ProviderAdminClient.SetBudgetAsync` → `ProviderBudgetStore`);
  that stale claim is now corrected. The three metrics with no live source — Routing ROI, Tool
  Steps, Context Buffer — are deliberately left mock-backed: each needs a new domain concept this
  codebase doesn't compute anywhere yet (a worst-case/baseline-cost model, a per-model
  context-window-size configuration, within-turn tool-call introspection), the same
  don't-invent-the-data reasoning as Phase E's multimodal-pricing deferral. See
  `docs/gui/backlog.md`'s Cost Analytics bullet for the full rationale and a suggested order if
  picked up later.
- Keep dialogs/modals aligned with `docs/gui/DESIGN.md`. — `SettingsModal.razor`'s new address
  field extends the existing shell/section pattern; no structural deviation.
- Reference: `docs/gui/backlog.md`.
- Exit: **met** for settings persistence (survives restart) and gRPC auth; the three Cost Analytics
  metrics remain mock-backed, explicitly out of scope per the above.

## Final Validation Gate
1. `dotnet build` passes with zero warnings/errors.
2. Relevant tests pass for each changed phase.
3. Documentation is updated to match delivered behavior.
4. Any intentionally deferred items are explicitly documented with rationale.
