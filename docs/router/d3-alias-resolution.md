# D3 Alias Resolution: Mapping Aggregator Model Names onto the Router Key

> **Status: auto-match implemented; explicit overrides + UI still open.** This is the implementation plan
> for [`model-price-catalog.md`](model-price-catalog.md)'s [D3](model-price-catalog.md#d3-an-internal-model-registry-decouples-aggregator-naming-from-the-router-key).
> **Done:** the exact auto-match resolver (`ConfigModelIdentityResolver`), its ingest wiring in
> `PriceCatalogRepository.UpsertPrices`, and the runtime cost lookup repoint onto the client-facing
> `ModelName` (`ProxyMiddleware`) — Slices 1–3 and 5 below. So a model whose configured `ProviderModelId`
> equals the aggregator's public model id now resolves a real per-request cost, and two sources naming one
> model differently collide into a single cell (first real exercise of the priority gate). **Still open:**
> Slice 4 (explicit operator overrides for what auto-match can't reach) and the
> [UI to manage them](#future-ui-managed-overrides). No schema migration was required: the `models`,
> `model_aliases`, and `model_prices` tables already had the right shape.

## Why this exists

The per-request cost path is wired (`ProxyMiddleware` → `IModelPriceLookup` → `PriceCatalogRepository.GetFreshPrice`),
but it resolves a price only when the lookup key happens to equal a source's own raw model key. In
practice that is rare, so most paid models report `EstimatedCostUsd = null`. Closing D3 is what turns that
`null` into a real number for every model the operator has configured **and** an aggregator prices.

## The problem, precisely

Three naming systems have to be reconciled, and today none of them are:

| System | Model key example | Provider key example | Source |
|---|---|---|---|
| **LiteLLM** source | `gpt-4o` | `openai` (from the `litellm_provider` field) | `LiteLlmPriceSourceClient` |
| **OpenRouter** source | `openai/gpt-4o` | `openai` (prefix of the `provider/model` id) | `OpenRouterPriceSourceClient` |
| **Router config** (`ModelRouting:ModelList`) | `ModelName` = `gpt-5.4`; `ProviderModelId` = e.g. `gpt-4o-2026-01` | `Provider` = `openai` | `ModelRouteEntry` |

Both source clients emit a `NormalizedPrice(ModelIdentifier, Provider, …)`. Today `PriceCatalogRepository.UpsertPrices`
stores each source's raw `ModelIdentifier` verbatim as `models.model_identifier`, so:

- LiteLLM's `gpt-4o` and OpenRouter's `openai/gpt-4o` land in **two different `models` rows** for the same
  real model. The `priority_score` gate under
  [Phase 3](model-price-catalog.md#phase-3-resolution-failover--write-logic) — which exists to arbitrate
  two sources contesting one cell — therefore **never fires** (D7's "0 real collisions" state).
- The runtime cost lookup keys on `route.ProviderModelId`, which equals a source's raw key only by
  coincidence, so most models resolve to `null`.

The `model_aliases` table already exists and `UpsertPrices` already calls `UpsertAlias` on every row — but
nothing **resolves** through it. `GetFreshPrice` matches `models.model_identifier` directly. The alias
table is being populated as pure lineage and read by nothing.

## The decision: hybrid resolution (auto-match now, explicit overrides later)

The catalog **cannot infer** that the operator's routing name `gpt-5.4` means OpenAI's `gpt-4o`. Something
has to supply that mapping. The chosen approach is a **hybrid**:

1. **Auto-match on `(provider, model-id)`** at ingest — the default path, zero operator configuration.
2. **Explicit operator-declared overrides** — a thin layer for what auto-match can't reach, and the
   subject of the [future UI work](#future-ui-managed-overrides) below.

**Matching MUST stay exact.** No fuzzy or best-guess prefix matching, ever — a confidently wrong price is
the exact failure the whole price subsystem exists to prevent. An aggregator entry that doesn't match
exactly (after the defined normalizations) is left unmapped, not approximately mapped.

### Resolution happens at ingest, not at read

D3 resolves aggregator naming onto the internal id **at ingest** (on the 4–12h poll), via `model_aliases`,
so the per-request read stays a single direct match on `models.model_identifier`. This matters because the
routing consumer reads inline with a live request and must not pay resolution cost per request (see the
catalog doc's [three consumers](model-price-catalog.md#the-three-consumers)).

## Implementation slices

> **No schema migration.** `models`, `model_aliases`, and `model_prices` already carry everything below.
> The change is ingest-resolution logic plus one lookup-key change.

### Slice 1 — `ModelIdentityResolver` (new) ✅ implemented

A resolver seeded from `ModelRouting:ModelList` + `ModelRouting:Providers`. Given an aggregator
`(ModelIdentifier, Provider)`, it returns the internal `ModelName` or `null`.

- **Match rule (exact):** the aggregator's provider maps to a configured `Provider`, **and** the
  normalized aggregator model-id equals that entry's `ProviderModelId`.
- **Model-id normalization:** strip any leading `provider/` prefix (so OpenRouter's `openai/gpt-4o`
  normalizes to `gpt-4o`); compare case-insensitively. Nothing more aggressive than that.
- **Provider-name normalization:** case-insensitive identity comparison only (the implemented resolver
  trims and lowercases both sides). Provider-name *divergence* — where the config's `Provider` key differs
  from the aggregator's own provider string (`azure-openai` vs `openai`) — is **not** handled by
  auto-match; it is deferred to the explicit-override slice (Slice 4), never guessed. A provider-name alias
  map is part of that future slice, not this one.

### Slice 2 — Wire resolution into `UpsertPrices` ✅ implemented

Resolve before `GetOrCreateModelId`:

- **On a hit:** store the price under the internal `ModelName` as `models.model_identifier`, and record the
  source's raw name as a `model_aliases` row for lineage.
- **On a miss:** fall back to today's raw-key behavior (store under the source's own key). Nothing
  regresses, telemetry-by-raw-key keeps working, and an unmapped model is simply unpriced-by-routing-key —
  the same honest "unknown" the rest of the system already uses.

This is the change that makes two sources collide into one `models` row, which is the **first real
exercise of the `priority_score` gate** — the higher-ranked source's number wins the contested cell.

### Slice 3 — Repoint the runtime cost lookup ✅ implemented

Change `ProxyMiddleware`'s cost lookup from `ModelKey(route.ProviderModelId, route.Provider)` to
`ModelKey(route.ModelName, route.Provider)`, matching Phase 4's `ModelKey.ModelName` contract (the
client-facing routing name). **This is the change that actually makes cost resolve for configured models.**

> Slices 2 and 3 must land in the **same PR**: repointing the lookup to `ModelName` before ingest stores
> prices under `ModelName` would briefly resolve against neither key.

### Slice 4 — Explicit alias overrides (config) — open

For the cases auto-match can't reach — a provider-name divergence the normalization map doesn't cover
(`azure-openai` vs `openai`), or a `ProviderModelId` that isn't the provider's public model id (a pinned
snapshot like `gpt-4o-2026-01` against an aggregator's `gpt-4o`). An optional operator-declared mapping
of `(aggregator source, aggregator model key) → ModelName` (or a per-`ModelRouteEntry` catalog-key field),
consulted by the resolver ahead of auto-match. Still exact; still never a guess.

### Slice 5 — Tests ✅ implemented (auto-match paths)

- Two sources naming one real model differently → a **single** `models` row, with the `priority_score`
  gate selecting the higher-ranked source's price (the gate's first real coverage).
- `GetFreshPrice(new ModelKey(ModelName, Provider))` resolves after ingest.
- A `ProxyMiddleware` cost test keyed on `route.ModelName` (the Slice-3 repoint) produces a real
  `EstimatedCostUsd`.
- An unmappable aggregator entry falls back to its raw key without error and stays unpriced-by-routing-key.

The above cover the auto-match paths and are implemented. One further test lands **with Slice 4**, not yet:

- *(pending Slice 4)* An explicit override resolves a case auto-match misses.

## Future: UI-managed overrides

**TODO — make explicit overrides (Slice 4) manageable through the Governance UI, not just `appsettings.json`.**

The auto-match path needs no configuration, but the override layer, if it ships as a raw config block, has
the same ergonomic problem D6's toggle originally had: the operator can only change it by editing a file
and restarting. The reasons to add an override are discovered at runtime — an operator notices a model's
cost is showing as unknown and wants to correct the mapping — so it should be fixable where that's noticed.

Scope of the future UI work (a **Governance → Price Sources** extension, or an adjacent "Model Aliases"
pane):

- Surface, per configured `ModelName`, whether a catalog price currently resolves and via which source /
  aggregator key (read-only diagnosis first — this alone tells the operator *why* a cost is unknown).
- Let the operator declare an override mapping `(source, aggregator model key) → ModelName` and have it
  take effect on the next poll cycle **without a restart**, mirroring how `PriceSourceToggleStore` moved
  D6's toggle from config into the database.
- Persist overrides in the catalog database (alongside `aggregator_sources`), not `appsettings.json`, for
  the same runtime-editability reason.
- Keep the licensing boundary from [D5](model-price-catalog.md#d5-price-data-must-never-be-exposed-via-a-public-api--licensing):
  this is operator control over the user's own mapping, carrying no price values outward — the same footing
  that lets the Price Sources panel exist.

This TODO is deliberately **not** on the critical path for closing D3: auto-match (Slices 1–3) resolves the
common case and is what unblocks cost for configured models. The override UI is an ergonomic follow-up for
the long tail auto-match can't reach.

## Risks

- **Provider-name divergence** (config `azure-openai` vs aggregator `openai`) → auto-match misses; handled
  by the normalization map or an explicit override, never by loosening the match.
- **`ProviderModelId` divergence** (operator pins `gpt-4o-2026-01`, aggregator keys `gpt-4o`) → miss;
  explicit override, never fuzzy matching.
- **Lookup-repoint regression** — Slices 2 and 3 must ship together (see the note under Slice 3).
- **`models.model_identifier` UNIQUE** — merging sources onto one identifier is the goal; two *different*
  real models mapping to one `ModelName` would collide, but `ModelRoutingOptions.EnsureValid()` already
  rejects duplicate `ModelName`s, so this can't arise from valid config.

## Effort

**Medium — one focused PR** for Slices 1–3 + 5 (resolver, ingest wiring, lookup repoint, tests), then a
**small follow-up PR** for Slice 4 (config overrides). The UI-managed override work above is a separate,
larger GUI effort tracked as its own TODO. No new dependency, no schema change.

## Related

- [`model-price-catalog.md`](model-price-catalog.md) — the canonical catalog plan; D3 and D7 are the
  decisions this implements.
- [`../../PLAN.md`](../../PLAN.md) — the price-catalog reference entry, which tracks D3 as the remaining
  open item for the "Basic Token/Cost Tracking" pillar's coverage.
