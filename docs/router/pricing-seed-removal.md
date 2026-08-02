# Removing the fake `Pricing` seed: "unknown" as the honest default

> **Status: Implemented.** This records a completed change and the reasoning behind it. The forward-looking
> half — the 24h routing gate and the zero-fresh-prices error log — is **specified, not built**: it lands
> with [`model-price-catalog.md`](model-price-catalog.md), which remains proposed. Where this document
> and that one disagree about price data, that one wins.

## Why

`appsettings.json`'s `Pricing` section seeded 12 models with hand-written USD rates that its own
`_comment` admitted were *"Illustrative placeholder values, not verified against current provider price
sheets."* Every cost figure the router reported — the `[SPEND]` log, `spend_log.jsonl`, the GUI's cost
column — was derived from those invented numbers.

That is worse than showing nothing. A fabricated number is indistinguishable from a real one at the
point someone reads it, and the `_comment` explaining otherwise lived in a config file no reader of the
GUI ever sees. So the seed is deleted and cost is `null` until TotallyHotArcRouter fetches real price data —
with one exception: a provider the operator marks **free** (a local Ollama runtime) has a *known* price
of zero, which is a verifiable fact about the machine rather than a guess.

**The constraint that shaped this:** the price-fetching subsystem does not exist. The recent price
commits (`e06de96`…`a4c1586`) touched `docs/` only — no SQLite dependency, no `IModelPriceCatalog`, no
ingestion worker. Removing the seed and enforcing a "must have a fresh price to be routable" rule at the
same time would have made zero models routable and stopped the proxy serving anything. Hence the split:
the seed removal and the free-provider flag are code today; the gate and the error log are documented
design.

## Decisions

| Question | Answer |
|---|---|
| Scope | Remove the seed in code now; specify the gate + error log in docs |
| Gate binds | Router auto-selection only (`AgentAsARouter` / planned `UtilityRoutingPolicy`), **not** `ModelRouteResolver.TryResolve` — the catalog is a cost oracle, not an allowlist |
| Free/local models | New `ProviderOptions.IsFree` ⇒ known price of 0; editable in Governance › Providers |
| `embedded-baseline` seed | Dropped entirely, along with the old decision D1 — unknown means unknown everywhere, including telemetry |
| Error log | Ingestion logs `Error` when a poll cycle ends with zero fresh (<24h) aggregator prices |

The `embedded-baseline` decision is the one worth re-reading if this is ever revisited. It was a
compiled-in JSON of hand-maintained prices for cold offline boots, fenced off from the router by a
provenance floor so it could feed cost *display* only. It was the same fabricated data as the deleted
table, just harder to notice, and it cost a whole design mechanism to keep it away from decisions.
Dropping it deleted that mechanism too.

## What changed

**Seed removal.** The `Pricing` block left [`appsettings.json`](../../src/TotallyHotArcRouter/appsettings.json);
`PricingOptions` and its DI registration are gone; `ProxyMiddleware` lost its pricing field and ctor
param. `ModelPrice` + `EstimateCost` survive in
[`Telemetry/ModelPrice.cs`](../../src/TotallyHotArcRouter/Telemetry/ModelPrice.cs) — the catalog will supply
their values later.

**The `IsFree` flag, end to end.** `ProviderOptions.IsFree` → `ResolvedModelRoute.IsFree` →
`ProxyMiddleware`, which reports `ModelPrice.Free.EstimateCost(...)` for a free route. It runs through
`EstimateCost` rather than a literal `0m` so the single cost formula stays single when the catalog plugs
in real rates. From there it crosses the admin API (`ProviderView` / `ProviderWriteRequest`, duplicated
on both the proxy and `Gui.Admin` sides) to a checkbox in `ProviderEditDialog` and a `Free` badge on the
provider card.

`IsFree` is nullable on the write DTO (`bool?`) so an omitted field means "keep existing" — a partial
write must not silently un-free a provider.

**Docs.** [`telemetry.md`](telemetry.md#pricing), [`model-price-catalog.md`](model-price-catalog.md),
[`utility-model-routing.md`](utility-model-routing.md),
[`agent-cost-tracking.md`](agent-cost-tracking.md),
[`../gui/governance-model-cards.md`](../gui/governance-model-cards.md), and
[`../gui/provider-management.md`](../gui/provider-management.md). The catalog's D1 was replaced in place
by the 24h freshness gate, which kept D2–D6 numbering and every anchor intact.
[`../research/technical-reference.md`](../research/technical-reference.md) was deliberately **not**
touched: its Appendix B.1 pricing table is the TotallyHotArcRouter paper's own Table 6, mirrored from the research
artifact, and has nothing to do with our config.

Deleting the seed made the docs *shorter*. `utility-model-routing.md` carried a three-layer table and a
"separation of concerns — estimation vs. decision" argument whose entire job was fencing a placeholder
table away from routing while letting telemetry use it. With the table gone, both collapse into one
sentence: there is exactly one cost signal.

## Deviation from plan: Ollama usage extraction

`UsageExtractor` dispatches on the provider key and only wired `openai` and `anthropic`, so **the
`ollama` provider reported no token usage at all**. Since the free-provider cost is computed inside the
usage-extraction gate, the `IsFree` zero could never fire for `llama3` — the exact model the flag exists
for. A test written against the plan caught it by failing.

`ollama` now shares the OpenAI parser arm (`"openai" or "ollama"`). This is a verified shape, not a
guess: [`unified-api-translation.md`](unified-api-translation.md) §4.1 documents Ollama's
OpenAI-compatible routes answering with the same `choices[].message` +
`usage.prompt_tokens`/`completion_tokens` and the same SSE framing, and `OllamaProviderTests` already
pinned it. Side effect worth knowing: `llama3` now reports token counts for the first time.

## Behavior now

| Route | `EstimatedCostUsd` |
|---|---|
| Provider flagged `IsFree`, usage extracted | `0` — a known price |
| Any other model | `null` — no price data source exists yet |
| Usage not extractable (unsupported provider, malformed body) | `null` |

## Hazards

1. **Persistence beats config.** `ProviderConfigStore` seeds from `appsettings.json` only when no
   `model-routing.json` exists; after any provider edit, that file owns the config. So `"IsFree": true`
   on `ollama` takes effect **on fresh installs only** — an existing install's persisted file has no
   `IsFree` key, loads as `false`, and `llama3` reports null cost until someone ticks the box. This is
   correct-by-design (no migration, no on-disk schema version), and it is why the `Free` badge sits on
   the provider card rather than only inside the edit dialog: the flag's state has to be visible without
   opening anything.
2. **`ModelPrice.Free` is not a catalog row.** When the catalog lands it must not overwrite a free
   provider's zero — an `IsFree` provider costs nothing regardless of what any aggregator publishes
   about the model it serves.
3. **The freshness gate has an inverse-polarity trap.** `utility-model-routing.md`'s quality gate does
   *not* drop models with `s == null` (unobserved ≠ bad), but the price rule *does* drop unpriced models
   (unpriced ≠ cost-comparable). Copying one polarity to the other by reflex breaks it. Relatedly, a
   naive reading of "no fresh price ⇒ not routable" would permanently exclude `llama3`, whose price is
   never fetched because it is never paid — which is why that exemption has its own named test in the
   catalog's test plan.

## Verification

```bash
dotnet build src/TotallyHotArcRouter/TotallyHotArcRouter.csproj   # removes a public type; build first
dotnet test  src/TotallyHotArcRouter.Tests/TotallyHotArcRouter.Tests.csproj
dotnet test  src/TotallyHotArcRouter.Gui.Admin.Tests/TotallyHotArcRouter.Gui.Admin.Tests.csproj
dotnet test  src/TotallyHotArcRouter.Gui.Tests/TotallyHotArcRouter.Gui.Tests.csproj
dotnet test  src/TotallyHotArcRouter.Sandbox.Tests/TotallyHotArcRouter.Sandbox.Tests.csproj
dotnet test  src/TotallyHotArcRouter.Gui.Telemetry.Tests/TotallyHotArcRouter.Gui.Telemetry.Tests.csproj
```

There is no `.sln`, so per-project invocation is required. `grep -rn "PricingOptions" src/` must return
nothing.

**Manual end-to-end — not yet run.** The automated tests cover each behavior below at the unit level,
but the live path has not been exercised:

1. Move aside the `model-routing.json` next to the build output, so the store re-seeds from
   `appsettings.json` and picks up `ollama.IsFree` (hazard 1).
2. `dotnet run --project src/TotallyHotArcRouter` — expect no `OptionsValidationException` at startup.
3. With `ollama serve` running, POST `{"model":"llama3", …}`: forwards, and `spend_log.jsonl` records
   **0** cost with real token counts.
4. POST `{"model":"gpt-5.4", …}` against a stub upstream: forwards, and the spend line has **null** cost
   with real token counts — the honest default, proven.
5. GUI → Governance → Providers: the ollama card shows the `Free` badge; untick → Save →
   `model-routing.json` carries `"IsFree": false` → a `llama3` request now reports null cost. That
   round-trip proves the whole chain including persistence.

