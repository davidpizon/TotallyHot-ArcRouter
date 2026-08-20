# TotallyHotArcRouter.Gui: Not-Yet-Implemented Work

A backlog of gaps between what the GUI docs describe/design and what `src/TotallyHotArcRouter.Gui/`
actually does today. Originally sourced from explicit statements in [`dashboard.md`](dashboard.md)'s
"Known gaps" section, [`src/TotallyHotArcRouter.Gui/README.md`](../../src/TotallyHotArcRouter.Gui/README.md)'s
"Current limitations," and deferred/optional items in
[`livestream-redesign-plan.md`](livestream-redesign-plan.md).

## Open

### 1. Extend live telemetry to the rest of the dashboard

Live Stream and Cost Analytics' Token Compounding chart now read live data (see "Recently
completed" below and [`../router/telemetry.md`](../router/telemetry.md)); everything else still
reads `MockData` because no telemetry source exists for it yet. Most of the items below (cache hit
rate, Model Distribution, the header ticker, functional time filters) are scheduled with concrete
data sources in
[`../router/token-tracking-implementation-plan.md`](../router/token-tracking-implementation-plan.md):

- **Cost Analytics — real values behind the metric explorer.** The tab is a metric explorer (see
  "Recently completed" below) whose corpus is now real, rollup-backed history
  (`UsageStore.LoadRollupAsync`, Phase 4 §5.15) merged with live turns — it survives a GUI restart —
  falling back to the mock corpus only when there is neither (§5.15's exit criterion). Cache Hit Rate
  is live for real turns (`RoutingTelemetryEvent.CacheCreationTokens`/`CacheReadTokens` →
  `LiveConversationMapper` → `CostChartBuilder.CacheHitRate`, see `../router/telemetry.md`).
  **Routing ROI is now live** — see the resolved item below. The remaining two,
  Tool Steps and Context Buffer, still have **no live source** and are populated by mock history only
  when it's in use; the rollup table has no per-turn breakdown for either dimension. Wiring them live
  means adding the corresponding fields to `RoutingTelemetryEvent`/the proto and the mapper chain (see
  the field table in `../router/telemetry.md`). **Deliberately deferred**, not a small follow-up: each
  needs a domain concept nothing in the codebase computes today — a per-model context-window-size
  configuration for Context Buffer, and within-turn tool-call introspection for Tool Steps — plus its
  own proto field and `LiveConversationMapper` wiring. Populating either without that data would mean
  inventing numbers, the same fabricated-data trap `model-price-catalog.md` and `src/PLAN.md`'s Phase E
  multimodal-pricing decision already declined. Of the two, Tool Steps is the smaller: it only requires
  parsing response bodies already captured by `ResponseTextExtractor`, where Context Buffer needs a
  config data source that does not exist yet.
- ~~**Routing ROI — needs a baseline cost nothing computes.**~~ **Done**
  ([`../router/self-organizing-classification-plan.md`](../router/self-organizing-classification-plan.md)
  Phase T4). Resolved by **redefining the baseline** rather than by building the worst-case-cost model
  this item originally asked for: the chart now measures against what the frozen nine-dimension
  `dim_best` voter alone would have chosen, which the router already decides on every request and now
  persists (`request_transcripts.dim_best_model`). `TaxonomyComparisonService` prices that
  counterfactual from the baseline model's own observed-average token counts and stores the net figure;
  the tab polls `GET /admin/usage/routing-roi` every 30 seconds. Two fabricated inputs were deleted with
  it — the baseline reconstructed from a cost-reduction percentage, and the invented `$2.50/M`
  remediation rate for turns reporting no ROI. Every figure is labeled an estimate in its own tooltip,
  since the counterfactual model's real token count for a given request is never observed; a turn with
  no counterfactual is skipped rather than drawn at zero. Note this did **not** wire per-turn
  `ConversationTurn.RoutingRoi` (the Live Stream turn card's percentage), which stays at 0 for live
  turns — that surface reads a different, still-unsourced number.
- ~~**Model Distribution** — real `TokenBucket`/`ModelShare` data.~~ **Done** (Phase 4 §5.15):
  `ModelDistribution.razor` fetches real buckets via `UsageStore.LoadRollupAsync`, grouped by day for
  the histogram and by model for the donut; the Day/Month/3-Month/6-Month/Year filter bar and the
  From/To inputs now actually refilter, falling back to `MockData` only when there's nothing live to
  show.
- ~~**Governance** — real per-provider budget/spend data.~~ **Done.** Budget Cap edits persist
  through `ProviderAdminClient.SetBudgetAsync` → the proxy's `/admin/providers/{key}/budget`
  endpoint → `ProviderBudgetStore` (SQLite `provider_budgets`/`provider_spend` tables, with
  per-provider `BudgetWindow` support), tested by `ProviderBudgetStoreTests`/
  `ProviderAdminEndpointsTests` — they survive a refresh and a GUI restart. (This corrects an
  earlier version of this bullet, which described the input as purely client-side; that was already
  stale by the time of writing.) [`../router/agent-cost-tracking.md`](../router/agent-cost-tracking.md)'s
  deeper per-request usage ledger (`UsageLedger`/`IUsageLedger`) is also implemented, separately
  from this budget/spend path. A first cut of
  [`governance-model-cards.md`](governance-model-cards.md)'s per-model pricing/spend section now
  exists (Governance > Models) with real spend from `UsageStore`, but every card still reads "Price
  unavailable" — that doc's dependency #1, a live model price catalog channel to the GUI, is still
  unbuilt, so the price half of the card is not yet done.
- ~~**Header ticker** (Total Saved / System Tokens / Avg. Cost Reduction) — still three hardcoded
  numbers~~. **Partially done** (Phase 4 §5.15): System Tokens is now real, from
  `UsageStore.LoadSummaryAsync("all")`. Total Saved and Avg. Cost Reduction stay mock and are still
  labeled "(demo)". The blocker named here originally — the missing baseline-cost concept — is now
  resolved by Phase T4's `dim_best` counterfactual (see the resolved Routing ROI item above), so these
  two could be sourced from `GET /admin/usage/routing-roi` the same way the chart is. Phase T4 did not
  do it: the ticker is a separate surface with its own aggregation, and repointing it was outside that
  phase's scope. This is now a small, unblocked follow-up rather than a deferred one.
- ~~**Dynamic chart axis ranges**~~ **Done** for Model Distribution's token histogram
  (`GroupedBarsModel.DynamicYMax`, Phase 4 §5.15) — computed from the actual data with headroom
  instead of a hardcoded 6M ceiling. The $0–$160 Cost Analytics savings scale is unaffected (that
  chart is unrelated to this plan) and remains pinned to the mock data's range.
- ~~**Settings modal actions**~~ **Done.** Reset Stats calls `LiveDataStore.ClearEvents()`; Clear
  History also clears the log buffer (`ClearLogLines()`). Both act on this session's live view only —
  the proxy's own durable history is untouched by design (see `LiveDataStore.ClearEvents`'s remarks).
- ~~**Configurable telemetry server address**~~ **Done.** `GuiSettingsStore` persists the address as
  JSON under `%LOCALAPPDATA%\TotallyHotArcRouter\gui-settings.json` (the same per-user directory the
  telemetry certificate and management token already use), editable from a new field in
  `SettingsModal.razor`; `MauiProgram` builds `LiveDataStore` from the persisted address.

### ✅ 2. Authenticate the telemetry gRPC stream

Encryption in transit is real (`Telemetry/TelemetryTlsCertificate.cs`, a self-signed cert on a
dedicated port - see [`../router/grpc-migration.md`](../router/grpc-migration.md)'s section 2), fixing
half of what [`../router/signalr-hub-security.md`](../router/signalr-hub-security.md) originally
proposed, via a different, gRPC-native mechanism rather than that doc's SignalR-era sketch.
**Authentication is now done too.** `Telemetry/TelemetryAuthInterceptor` gates every call to the
telemetry gRPC endpoint (`TelemetryGrpcService`'s `StreamEvents` and `PriceSourceAdminGrpcService`,
which share the TLS port) behind the shared per-user management token, presented in the
`x-admin-token` metadata entry and verified against `ManagementAccessToken` — the same shared secret
that already gates the REST `/admin/*` API and the MCP endpoint, so every management surface uses one
identical check. It overrides all four gRPC call shapes (unary, server-streaming, client-streaming,
duplex), not just the ones this service currently uses, so a future streaming admin RPC doesn't
silently reopen the gap. `TelemetryAuthClientInterceptor` attaches the token client-side;
`TelemetryChannelFactory.Authenticated` builds the channel with it wired in, used by both
`LiveDataStore` and `PriceSourceAdminClient`. This is `signalr-hub-security.md` §2's shared-secret
design translated from a SignalR `AccessTokenProvider` to a gRPC interceptor/call credential, per that
doc's own status banner.

### ✅ 3. Anthropic Reported Usage section (per-provider card, non-enterprise accounts) — code shipped, currently blocked on account tier

**Shipped** — see
[`docs/router/anthropic-reported-usage-plan.md`](../router/anthropic-reported-usage-plan.md) for the
implemented design. That plan sources accurate usage from data the proxy already has on the wire - the
Messages API `usage` object (parsed cache-aware) and the `anthropic-ratelimit-*` response headers - so no
Admin key is required. The rest of this entry is kept for the distinct, additive Admin-API-backed card
described below, which
[`docs/router/secrets-at-rest-plan.md`](../router/secrets-at-rest-plan.md) audited and corrected two
stale premises on:

1. **A sourcing mechanism already shipped.** `CostTracking:Reconciliation:Providers:anthropic:AdminApiKeyEnvVar`
   names an environment variable; `TryResolveAdminApiKey`
   ([`ServiceCollectionExtensions.cs`](../../src/TotallyHotArcRouter/Hosting/ServiceCollectionExtensions.cs))
   resolves it; and
   [`AnthropicCostReconciler`](../../src/TotallyHotArcRouter/Telemetry/AnthropicCostReconciler.cs)
   already calls `GET /v1/organizations/cost_report` with it. The "env var convention vs. a dedicated
   provider-editor field" choice this entry used to call open was made and shipped.
2. **The gate is "not an individual account", not "enterprise".** Per Anthropic's [Usage and Cost
   API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) docs, any **Claude Console**
   organization member with the **admin** role can create an Admin key; converting an individual
   Console account into an organization is a self-serve Console → Settings → Organization step, not an
   enterprise contract.

Governance > Providers cards previously had no way to show Anthropic's own authoritative usage/cost
numbers — the "Monthly Budget" section
([`ProvidersAdmin.razor`](../../src/TotallyHotArcRouter.Gui/Components/ProvidersAdmin.razor))
renders bar charts, but they're driven entirely by the proxy's own internal request tally
(`ProviderBudgetStore`), not by Anthropic. **Shipped**, per `secrets-at-rest-plan.md` Phases 4-6:

- **What**: a new "Reported Usage (Anthropic)" section on a provider card
  (`ProvidersAdmin.razor`), shown for every `ProviderType == Anthropic` provider - "No reported usage
  fetched yet" until an Admin key resolves and the first cycle runs, then daily token bars grouped by
  model. No separate "enterprise account" flag; see `secrets-at-rest-plan.md` §2.
- **Data source**: Anthropic's [Usage & Cost Admin
  API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) via
  `Telemetry/AnthropicUsageReportClient.cs` — token usage over a trailing 30-day window, refreshed by
  `AnthropicUsageReportService` on the existing hourly `CostReconciliationHostedService` cycle (not on
  card load - Anthropic documents a one-request-per-minute polling ceiling).
- **Remaining prerequisite is account-level, not code: an Admin key must exist for the `anthropic`
  provider's Console account.** As of this repository's last check (2026-08-10), that account is an
  **individual Console account** - the Admin API answers "unavailable for individual accounts" until it
  is converted to an organization (self-serve, ~5 minutes), after which any org admin can create an
  `sk-ant-admin01-…` key. It can be entered either via the provider card's "Admin API Key (cost
  reconciliation, optional)" field or `CostTracking:Reconciliation:Providers:anthropic:AdminApiKeyEnvVar`
  - the stored secret takes priority (`agent-cost-tracking.md` §4). Two related exclusions: **Bedrock
  has no programmatic Usage/Cost API**, so `bedrock-anthropic` can never populate this card regardless
  of key; and **Claude Enterprise** carries a *different* credential (an Analytics API key,
  `sk-ant-api01-…`) against a different endpoint family (`/v1/organizations/analytics/…`), not
  interchangeable with an Admin key.
- **Display**: daily token bars grouped by model, reusing the existing `EChart`/`ChartJson`
  `GroupedBarsModel` pattern from the Monthly Budget section, plus a "Fetched" timestamp footer —
  Anthropic's reported numbers are only trustworthy as of the moment they were pulled. This is
  additive, not a replacement: Anthropic's API has no endpoint to read back a spend *limit*, so it
  can't drive the existing local $/token cap-utilization bars, which stay exactly as they are.
- **Secret handling**: the Admin key itself is written through `PUT /admin/secrets/{name}`
  (`ManagementFacade.SetSecret`, restricted to `reconciliation:{openai|anthropic}:admin-key`) into the
  protected secret store (`docs/router/secrets-at-rest.md`) rather than `model-routing.json` - there is
  deliberately no `GET` counterpart on that route; `GET /admin/providers` exposes only a
  `HasStoredAdminKey` boolean.

## Recently completed

### ✅ Cost Analytics bespoke per-metric charts + whole-app move to Apache ECharts

The Cost Analytics metric explorer's single combo chart was rebuilt into **seven bespoke per-metric
charts** implementing [`cost-analytics-visualization-spec.md`](cost-analytics-visualization-spec.md):
dual-directional ROI bars, stepped cumulative cost (area recolored per model), a token-runaway area
(hatched zone + rippling alert), segmented tool-step bars, a cache-rate gradient line, a TTFT line over
per-model background zones with pinned spikes, and a context line with a pulsing 90% breach threshold —
all with "bold" entrance/alert animations. In the same change the **whole dashboard moved off
ApexCharts to Apache ECharts**: `echarts.min.js` is vendored under `wwwroot/lib/echarts` (Apache-2.0)
and driven by `wwwroot/js/echarts-interop.js` through the reusable `Components/EChart.razor` host;
`Blazor-ApexCharts-MAUI`, its `AddApexChartsMaui()` registration, and its CSS are gone, and Model
Distribution's grouped-bar + donut charts were ported too. The pure, unit-tested
`TotallyHot.ArcRouter.Gui.Charts.CostChartBuilder` (+ `CostChartBuilderTests`) builds each chart model and
derives every rich tooltip figure (baseline cost, per-step model split, cached/uncached tokens,
context token counts, cold-start split) from the turn's existing fields; `ChartJson` serializes it and
`ChartJsonTests` guards the C#↔JS field contract; `ChartPalette` (which `ColorUtils` now delegates to)
supplies deterministic per-model colors. `MockData.BuildMetricHistory` gained fixed exemplar events (a
runaway, a TTFT spike, a fallback, context breaches) so each chart shows its special state offline.
This supersedes the previous metric-explorer combo chart (the `MetricTimeSeries` engine and its tests
were removed); `TokenCompoundingSeries` still backs the `ConversationSummary` sparkline.

### ✅ Cost Analytics metric explorer (7 ranked metrics, time ranges, session scope)

`CostAnalytics.razor` was rewritten from three fixed panels (mock Cumulative Savings line, mock
ROI-by-Agent bar, live Token-Compounding line) into a **metric explorer**: a ranked selector for the
seven Perf/$ metrics (Routing ROI → Context Buffer), a Hour/Day/Week/Month/All time-range control, and
an `All Sessions`/per-session scope that defaults to the Live Stream tab's selection. (The combo chart
this shipped with was later replaced by the bespoke per-metric charts above.) A real
`DateTimeOffset TimestampUtc` was added to `ConversationTurn` (passed through by
`LiveConversationMapper` from `LiveConversationTurn.TimestampUtc`) to place turns onto the time axis.

### ✅ Telemetry transport migrated from SignalR to gRPC

Fully specified in [`../router/grpc-migration.md`](../router/grpc-migration.md) and now implemented
(minus that doc's `GetModelSpend` RPC and `ModelListEvent` stream case, deliberately descoped - see
the doc's status banner): `Telemetry/TelemetryHub.cs` is deleted, `TelemetryGrpcService`/
`TelemetryBroadcaster` replace it server-side, and `LiveDataStore.cs` now speaks gRPC via a
`GrpcChannel` instead of a SignalR `HubConnection`. `src/Protos/telemetry.proto` is the shared
contract, compiled independently into both `TotallyHotArcRouter` and `TotallyHot.ArcRouter.Gui.Telemetry` (not
`TotallyHot.ArcRouter.Gui` itself - .NET MAUI's `SingleProject` build doesn't reliably run Grpc.Tools'
codegen), closing the hand-synced-DTO drift risk that motivated this. See
[`../router/telemetry.md`](../router/telemetry.md)'s "Transport: gRPC" section for the full mechanism.

### ✅ Turn card request/response text

`TurnCard.razor`'s Request/Response sections now show real data: `Telemetry/RequestTextExtractor.cs`
pulls the newest user message out of the request body's `messages` array (not the whole resent
history), and the new `IResponseTextExtractor`/`ResponseTextExtractor` (mirroring `UsageExtractor`'s
provider-dispatch design) extracts the assistant's reply text for both OpenAI and Anthropic,
streaming and non-streaming. Both are truncated to 2,000 characters (`TextTruncator`) before being
placed on `RoutingTelemetryEvent` and broadcast. `Services/LiveConversationMapper.cs` now passes these
straight through instead of hardcoding `null`. See [`../router/telemetry.md`](../router/telemetry.md)'s
"Request/response text extraction" section for the full pipeline - and the item below for the
security gap this makes more pressing.

### ✅ New "Console" tab: streaming log viewer

Fully specified in [`console-tab-plan.md`](console-tab-plan.md) and now implemented: a fifth tab
showing a real-time, color-coded (`DEBUG`/`INFO`/`WARN`/`ERROR`/`FATAL`) log stream with a
toggleable auto-scroll (and smart-disengage on manual scroll-up), a copy-all-to-clipboard action
(via MAUI's native `Clipboard`, not the browser `navigator.clipboard`, since clipboard-write from a
WebView2 page can be blocked by permission prompts), and a clear-buffer action. The missing
proxy-side source noted here previously is closed by
`src/TotallyHotArcRouter/Telemetry/TelemetryLogEventSink.cs` (renamed from `SignalRLogEventSink.cs` when
the transport migrated to gRPC - see the item above), a custom Serilog `ILogEventSink` that forwards
every log event (additively, alongside the existing `Console` sink -
`serilog-logging-guide.md` is otherwise unchanged) as a `LogLineEvent` over the same telemetry gRPC
stream routing telemetry already uses. `TotallyHot.ArcRouter.Gui.Console` (+
`.Tests`) hosts the reusable, unit-tested pieces (`LogLevelColorMapper`, `LogBuffer`), mirroring the
`TotallyHot.ArcRouter.Gui.Charts` pattern; `LiveDataStore` and `Components/ConsoleTab.razor` wire it into
the dashboard.

### ✅ Wire the dashboard to live TotallyHotArcRouter proxy telemetry, with real-time push updates

`src/TotallyHotArcRouter/Telemetry/` now captures per-request session/turn tracking, OpenAI/Anthropic
token usage (streaming and non-streaming), and estimated cost, and pushes each request as a
`RoutingTelemetryEvent` over a gRPC stream (`TelemetryService.StreamEvents`) as soon as it's
forwarded — no polling. `TotallyHot.ArcRouter.Gui`'s `Services/LiveDataStore.cs` consumes this live, and the
Live Stream tab plus Cost Analytics' Token Compounding chart now render real conversations instead of
`MockData`. Full pipeline, field-by-field data provenance, and what's still honestly defaulted
(Routing ROI, Tool Steps, Context Buffer) vs. real (Time to First Token, Cache Hit Rate,
Request/Response text) is in [`../router/telemetry.md`](../router/telemetry.md). This closes out both
former "Open" headline items (live wiring and real-time push) in one implementation, since server
push (originally SignalR, now gRPC - see the item above) was the chosen transport from the start
rather than adding polling first.

### ✅ Token-compounding line chart in Cost Analytics *(superseded by the metric explorer above)*

Originally implemented in `CostAnalytics.razor` as a "Token Compounding by Conversation" panel: a
conversation picker plus a two-series line chart (cumulative prompt tokens, cumulative completion
tokens) per turn, built via `TotallyHot.ArcRouter.Gui.Charts.TokenCompoundingSeries.Build`. This is now the
`Tokens` metric (single-session scope) of the metric explorer; `TokenCompoundingSeries` itself
remains in use for the `ConversationSummary` sparkline.

### ✅ Token-compounding sparkline on the conversation summary card

Implemented in `ConversationSummary.razor`: a compact inline SVG polyline ("Trend" stat) showing
per-turn total tokens, built via `TotallyHot.ArcRouter.Gui.Charts.TokenCompoundingSeries.BuildSparkline`
and `SparklineLayout.Normalize`.

### ✅ Keyboard-accessible tooltips

`wwwroot/js/tooltips.js` now shows/hides on `focusin`/`focusout` (in addition to hover), dismisses
on Escape, and the shared tooltip element is hidden via opacity rather than `display:none` so it
stays in the accessibility tree. Every `data-tip` element not nested inside a `<button>` carries
`tabindex="0"` and `aria-describedby="ls-tooltip"`. The handful nested inside a `<button>` (e.g. a
`TurnCard` header's sub-badges) intentionally skip `tabindex` — nesting a focusable element inside a
button is an ARIA anti-pattern — and the outer button carries a comprehensive `aria-label` instead.
Smoke-tested against a standalone HTML harness with Playwright/Chromium (see `dashboard.md`'s
"Verification limitation" note for why that, rather than a full app build, was the verification
method available in this environment).

## Minor / cosmetic (low priority)

- **Chart tooltips** are custom dark-themed HTML built in `wwwroot/js/echarts-interop.js` to match the
  card styling, rather than matching the original React design pixel-for-pixel. `dashboard.md` calls
  this a "minor visual difference," not a functional gap.

