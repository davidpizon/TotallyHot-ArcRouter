# Phase M2 Implementation Plan — Requested vs. Routed, End to End

**Status:** ready to implement. Supersedes nothing; this is the step-by-step build order for
[`orchestrator-live-path-plan.md`](orchestrator-live-path-plan.md) §M2, which remains the owning spec
for *what* and *why*. This file is *how*, in dependency order, with exact files and line anchors as of
commit `513cbb5` (M1 shipped).

**Verified starting state (2026-08-18, via CodeGraph + direct read):**
- [`ProxyMiddleware.cs:259`](../../src/TotallyHotArcRouter/Proxy/ProxyMiddleware.cs:259) sets
  `requestedModelName = candidates[0].Route.ModelName` — the post-routing primary, not the client's
  literal string.
- [`RoutingTelemetryEvent.cs:72-95`](../../src/TotallyHotArcRouter/Telemetry/RoutingTelemetryEvent.cs:72)
  has no `RoutedModel` or substitution-reason field.
- [`ModelRouteResolutionResult.cs`](../../src/TotallyHotArcRouter/Proxy/ModelRouteResolutionResult.cs)
  carries no client-literal requested-model field.
- `UsageLedgerEntry.RequestedModel` / `SpendTracker.RecordAsync` both currently receive
  `candidates[0].Route.ModelName` (`ProxyMiddleware.cs:1437`, `:1470`) — the lined-up model, not the one
  that served.

**Scope for this pass (per user decision):** proto → domain event → wire DTO → GUI aggregation model →
`ConversationTurn` field plumbing, so the data exists end to end. **Not in this pass:** the M3.1
rendering logic (`LiveConversationMapper.BuildRoutingSteps`'s substitution step, `TurnCard.razor`
styling) and M3.2's Routing Mode governance card — those stay Phase M3's job, per the roadmap's own
phase split.

---

## 1. New shared type: `RoutingSubstitutionReason`

New file `src/TotallyHotArcRouter/Telemetry/RoutingSubstitutionReason.cs`:

```csharp
namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>Why the model that served a request differs from the client's literal <c>model</c> string.</summary>
public enum RoutingSubstitutionReason
{
    /// <summary>The routed model is exactly what the client named. No substitution occurred.</summary>
    None,
    /// <summary>The client asked for <c>"auto"</c>, delegating the choice to the router.</summary>
    AutoSelect,
    /// <summary>The client's named model is not in <c>ModelList</c>.</summary>
    UnresolvedName,
    /// <summary>The client's named model resolved but is administratively stopped (or dropped by its provider's last scan).</summary>
    ModelStopped,
    /// <summary>The client's named model resolved and is enabled, but its circuit (or its whole provider's) is open.</summary>
    CircuitOpen,
    /// <summary>The primary candidate was attempted and failed at the transport layer; a later candidate served instead.</summary>
    Failover,
}
```

Lives in `Telemetry`, not `Router` or `Proxy`: it is reported data, mirroring `CostConfidence`'s home,
and both `RequestInterceptor` (computes it) and `RoutingTelemetryEvent`/the proto (carry it) already
depend on `Telemetry` types elsewhere (`CostConfidence`).

---

## 2. `ModelRouteResolutionResult` — carry the client-literal name and the resolution-time reason

`src/TotallyHotArcRouter/Proxy/ModelRouteResolutionResult.cs`:

- Add two fields to the private constructor and `Success` factory: `string RequestedModelName` and
  `RoutingSubstitutionReason SubstitutionReason`.
- `Success(candidates, requestedModelName, substitutionReason, taskEmbedding = null, routerTokens = 0)`.
  `requestedModelName` is required (no default) — same reasoning the type already applies to
  `CarriesTools`: a silently-wrong default here is worse than a compile error at every call site.
- `Failure` needs no change (never reaches `PublishTelemetryAsync`).

This reason is the *resolution-time* reason only (§3 below computes it). `ProxyMiddleware` may still
override it to `Failover` at serve time (§4) — `ModelRouteResolutionResult` only reports what
`RequestInterceptor` knew when it built the candidate list.

---

## 3. `RequestInterceptor.ResolveModelRouteAsync` — compute the literal name and the reason

`src/TotallyHotArcRouter/Proxy/RequestInterceptor.cs`, inside `ResolveModelRouteAsync`
(`:279-414`):

1. Capture the client's literal string **before** the `_forcedModelName` override
   (`:291-294`): `var clientRequestedModelName = modelName;` right after the null/empty check at `:283-286`,
   before the forced-serving branch. Forced single-model serving still overrides what's *served*
   unconditionally (ground rule, unchanged) — this only affects what's *reported* as requested.
2. Track a `RoutingSubstitutionReason` local, defaulted to `None`, set at each branch:
   - `isAutoSelectRequest` true (`:306`) → `AutoSelect`.
   - Unresolved/disabled path (`:323`): the existing condition
     `!_modelRouteResolver.TryResolve(modelName, out route) || !_modelRouteResolver.IsModelEnabled(modelName)`
     conflates two cases that need different reasons. Split it:
     ```csharp
     var resolved = _modelRouteResolver.TryResolve(modelName, out route);
     if (!resolved || !_modelRouteResolver.IsModelEnabled(modelName))
     {
         reason = resolved ? RoutingSubstitutionReason.ModelStopped : RoutingSubstitutionReason.UnresolvedName;
         // ... existing agenticRoute fallback logic unchanged ...
     }
     ```
     Only set when `agenticRoute is not null` actually substitutes (mirror the existing `if
     (agenticRoute is not null)` branch at `:338`) — the `else` branch already returns `Failure` and
     never reaches a `Success` construction.
   - Circuit-open/administrative-disable substitution block (`:378-391`): the existing condition ORs
     four checks. Split the *reason* (not the substitution logic, which stays as one block since all
     four conditions share the same substitute-lookup):
     ```csharp
     if (substitute is not null)
     {
         reason = _circuitBreaker.IsOpen(...) || _circuitBreaker.IsProviderOpen(route.Provider)
             ? RoutingSubstitutionReason.CircuitOpen
             : RoutingSubstitutionReason.ModelStopped;
         route = substitute;
     }
     ```
     Only reachable when `_forcedModelName is null` (already gated by the surrounding `else` at `:364`).
3. Forced single-model serving (`_forcedModelName is not null`, `:354-363`) always reports `None` —
   it is a deployment-time decision, not a per-request substitution; the client's literal name is still
   captured in step 1 and will differ from the served model in telemetry, which is honest (the client
   asked for something, forced serving gave them the pinned model — that's visible via
   `RequestedModel != RoutedModel`, no separate reason needed for a config-time behavior that has
   nothing to fail over from).
4. Pass both through to the final `Success` call (`:414`):
   `ModelRouteResolutionResult.Success(candidates, clientRequestedModelName, reason, taskEmbedding, routerTokens)`.

**Test surface:** `RequestInterceptorTests` gets four new cases (or extensions of existing ones) —
auto-select → `AutoSelect`; unknown name → `UnresolvedName`; resolved-but-stopped name → `ModelStopped`;
resolved-and-enabled name whose circuit is open → `CircuitOpen`; a named, healthy, enabled model →
`None`. Reuse existing test fixtures in that file rather than new ones — the routes/models they already
construct cover these branches.

---

## 4. `ProxyMiddleware` — resolve `Failover`, fix requested/routed/spend attribution

`src/TotallyHotArcRouter/Proxy/ProxyMiddleware.cs`:

### 4.1 `InvokeAsync` (`:247-430`)

- Replace `requestedModelName = candidates[0].Route.ModelName` (`:259`) with the client's literal name
  from the resolution: `var requestedModelName = resolution.RequestedModelName;` (new property on
  `ModelRouteResolutionResult`, wired in §2). Keep the existing local name so every downstream call site
  (`InvokeBedrockAsync`, `PublishTelemetryAsync`, `WriteBudgetExhaustedResponseAsync`, the log lines at
  `:271`, `:853`, `:860`, `:870`) compiles unchanged — it now just holds a different, correct value.
- In the `for` loop (`:283-430`), once a candidate is chosen to attempt (`isFallback = i > 0` at
  `:374`), that boolean already *is* "does this differ from the primary the interceptor lined up." Pass
  it through to `PublishTelemetryAsync`/`InvokeBedrockAsync` as today (`isFallback` parameter, unchanged
  signature) — §4.2 derives the final `RoutingSubstitutionReason` from it, no new parameter needed here.

### 4.2 `PublishTelemetryAsync` (`:1244-1559`)

- Add a `RoutingSubstitutionReason resolutionReason` parameter (from `resolution.SubstitutionReason`,
  passed by the caller at `:828`/`:1126`) alongside the existing `requestedModelName`/`isFallback`
  parameters.
- Compute the **final** reason right before constructing `RoutingTelemetryEvent` (`:1520`):
  ```csharp
  var substitutionReason = isFallback ? RoutingSubstitutionReason.Failover : resolutionReason;
  ```
  `isFallback` wins over the resolution-time reason because it means the primary that reason describes
  was never actually served — a transport failure at serve time is a more accurate account of *why this
  response* came from a different model than whatever the interceptor anticipated at resolution time.
- `var requestedModel = requestedModelName;` (`:1312`) is now already the client's literal string (via
  §4.1) — no change needed here beyond what §4.1 already fixes upstream. Update the doc comment at
  `:1308-1311` (references "candidate 0" — no longer accurate) and at `ProxyMiddleware.cs:257`
  (same content, must be rewritten per the roadmap's own exit criterion #5, not just extended).
- **RoutedModel**: add `route.ModelName` — `route` is already the parameter naming the model that
  *served* (post-failover winner, per the method's own doc comment at `:1236-1242`). This is the new
  field on `RoutingTelemetryEvent` (§5).
- **Spend/ledger attribution (M2.3, decided: the model that served):**
  - `:1437` `_spendTracker.RecordAsync(requestedModel, ...)` → `_spendTracker.RecordAsync(route.ModelName, ...)`.
  - `:1470` `UsageLedgerEntry(RequestedModel: requestedModel, ...)` → `RequestedModel: route.ModelName`.
    (`ResolvedModel: route.ProviderModelId` at `:1471` is already correct and unchanged — it was never
    the field with the bug.)
  - `:1524` `RoutingTelemetryEvent(RequestedModel: requestedModel, ...)` **stays** `requestedModel` (the
    client's literal string) — this field's *meaning* changes today (§M2.2's table), but the parameter
    that already flows into it now carries the corrected value from §4.1, so no line-level edit is
    needed here beyond adding `RoutedModel`/`SubstitutionReason`.
  - Update `agent-cost-tracking.md`'s documented column meaning per M2.3's "value fix, not schema
    change" — see §7 below (M4-equivalent doc pass folded into this plan rather than deferred).

### 4.3 `InvokeBedrockAsync` (`:979-1126`)

Same `resolutionReason` parameter threaded through from its caller (`:430`) to its own
`PublishTelemetryAsync` call (`:1126`) — mechanical, no new logic; Bedrock is just a second call site of
the same telemetry publish.

### 4.4 Response headers (new)

Immediately before writing the response status/headers on **every** path that reaches a served response
(both the HTTP forwarding path and `InvokeBedrockAsync`), set:

```
X-ArcRouter-Requested-Model: {requestedModel}
X-ArcRouter-Routed-Model: {route.ModelName}
X-ArcRouter-Substitution-Reason: {substitutionReason}
```

Set on `context.Response.Headers` before the first byte is written (same constraint every other
response-header mutation on this path already respects — headers are immutable once streaming starts).
Since both streaming and buffered responses go through the same header-setting point before body writes
begin, no separate streaming-specific path is needed (confirms §M2.2's "headers work identically for
streaming and buffered responses" claim against this codebase's actual response-writing structure —
verify the exact call site during implementation by locating where `context.Response.StatusCode` is
first assigned on each path, and set these three headers at the same point).

**Test surface:** a new `ProxyMiddlewareRequestedRoutedHeaderTests.cs` (or an extension of an existing
`ProxyMiddleware*Tests.cs` file) covering: non-substituted request → headers equal, reason `None`;
`auto` request → reason `AutoSelect`, `RoutedModel` is the chosen model; a request naming a stopped
model → reason `ModelStopped`; a failover (first candidate transport-fails, second serves) → reason
`Failover`, `RoutedModel` is the second candidate's name; headers present on both streaming and buffered
responses.

---

## 5. `RoutingTelemetryEvent` — new fields

`src/TotallyHotArcRouter/Telemetry/RoutingTelemetryEvent.cs`:

- Add `string RoutedModel` (required, no default — same reasoning as `RequestedModel`/`ResolvedModel`
  already get: a silently-omitted routed model is worse than a compile error) and
  `RoutingSubstitutionReason SubstitutionReason = RoutingSubstitutionReason.None` (defaulted, since
  every non-substituted event legitimately is `None` and most existing test constructions won't care).
- Rewrite the `RequestedModel` `<param>` doc (`:20`) — it no longer merely says "the client-facing model
  name from the request body" ambiguously; it must now say "the client's literal `model` string" and
  cross-reference `RoutedModel`, per the roadmap's exit criterion #5 flagging this exact comment as
  stale-on-change.
- Add `<param name="RoutedModel">` and `<param name="SubstitutionReason">` docs.

---

## 6. Wire protocol: `telemetry.proto` → generated code → `TelemetryBroadcaster` → GUI DTO → aggregation → `ConversationTurn`

### 6.1 `src/Protos/telemetry.proto`

In `message RoutingTelemetryEvent` (`:372-409`), append two fields at the next free numbers (23, 24 —
21/22 are `router_tokens`/`router_cost_usd`, the last ones added):

```protobuf
string routed_model = 23;
// TotallyHot.ArcRouter.Telemetry.RoutingSubstitutionReason serialized via Enum.ToString()/Enum.Parse() on
// each end, same convention as cost_confidence above. Not optional: every writer always sets it
// (None is a real, always-present value, not an absent one).
string substitution_reason = 24;
```

Plain `string` (not `optional string`), matching `requested_model`/`resolved_model`'s own convention —
these are never-absent fields on every writer, unlike `cost_confidence` which needed presence-tracking
against an older writer. `routed_model` has the same "every current writer always sets it" property.

Both C# projects (`TotallyHotArcRouter`, `TotallyHotArcRouter.Gui.Telemetry`) compile this file
independently via `Grpc.Tools`; no manual codegen step — `dotnet build` regenerates
`Contract.RoutingTelemetryEvent` in both.

### 6.2 `TelemetryBroadcaster.ToWire` (`src/TotallyHotArcRouter/Telemetry/TelemetryBroadcaster.cs:110-177`)

Add to the unconditional-fields block (alongside `RequestedModel`/`ResolvedModel`, `:117-118`):

```csharp
RoutedModel = e.RoutedModel,
SubstitutionReason = e.SubstitutionReason.ToString(),
```

Both always-set (matching `RouterTokens`/`RouterCostUsd`'s treatment just below in the same object
initializer) — no `Has*` presence check needed on the writer side.

### 6.3 `RoutingTelemetryEventDto` (`src/TotallyHotArcRouter.Gui.Telemetry/RoutingTelemetryEventDto.cs`)

Add `string RoutedModel` and `string? SubstitutionReason` (nullable-with-null-default like
`CostConfidence`'s own field, `:34`, since a mixed-version proxy predating this phase would send neither
— degrade to null, not a fabricated `"None"`).

### 6.4 `LiveDataStore.MapToDto` (`src/TotallyHotArcRouter.Gui/Services/LiveDataStore.cs:236-266`)

```csharp
RoutedModel: e.RoutedModel,
SubstitutionReason: e.HasSubstitutionReason ? e.SubstitutionReason : null,
```

`routed_model` is a plain (non-optional) proto3 string, so an absent field from an older writer decodes
as `""`, not detectable via `Has*` — mirror `RequestedModel`/`ResolvedModel`'s own unconditional mapping
immediately above it in the same initializer (`:240-241`), which already accepts this same limitation
for those two fields.

### 6.5 `ConversationAggregator` (`src/TotallyHotArcRouter.Gui.Telemetry/ConversationAggregator.cs`)

- Add `string RoutedModel` and `string? SubstitutionReason = null` to `LiveConversationTurn` (`:4-19`).
- In `BuildConversation` (`:87-123`), map them straight through from the `RoutingTelemetryEventDto`:
  `RoutedModel: e.RoutedModel, SubstitutionReason: e.SubstitutionReason`.
- **Deliberately do not** change `Agent`/`Model` (`:95-96`, currently `e.ResolvedModel`) — that display
  choice is unrelated to this plumbing pass and is M3's call to make if it wants `RoutedModel` shown
  instead of the provider id.

### 6.6 `ConversationTurn` (`src/TotallyHotArcRouter.Gui/Models/DashboardData.cs:51-77`)

Add `string? RequestedModel = null`, `string? RoutedModel = null`, `string? SubstitutionReason = null` —
optional/defaulted like `CostConfidence` (`:77`) immediately above, for the same reason: existing mock
turns and any other call site must keep compiling untouched.

### 6.7 `LiveConversationMapper.ToModel` (`src/TotallyHotArcRouter.Gui/Services/LiveConversationMapper.cs:61-83`)

Add three lines to the `ConversationTurn` constructor call: `RequestedModel: turn.???` — **note:**
`LiveConversationTurn` as extended in §6.5 does not itself carry the client's literal `RequestedModel`
(only `RoutedModel`/`SubstitutionReason` were added there, matching what a turn-level display actually
needs). Add `RequestedModel` to `LiveConversationTurn` too (mapped straight from
`RoutingTelemetryEventDto.RequestedModel`, which already exists) so this mapping has a source:

```csharp
RequestedModel: turn.RequestedModel,
RoutedModel: turn.RoutedModel,
SubstitutionReason: turn.SubstitutionReason,
```

This is the last hop named in §M2.2's exit criterion ("Propagate through telemetry.proto →
RoutingTelemetryEventDto → LiveDataStore → LiveConversationTurn → ConversationTurn"). Nothing reads
these three new `ConversationTurn` fields yet — `BuildRoutingSteps` (`:86-96`) is untouched, per the
strict M2/M3 split. They exist so M3.1 has data to render without another plumbing pass.

**Test surface:** extend `ConversationAggregatorTests` (new fields flow through `BuildConversation`),
`LiveConversationMapperTests` (new fields flow through `ToModel`), and `LiveDataStoreTests` if one
exists for `MapToDto` — check before assuming; if none does, this mapping is currently only covered
indirectly and a small direct test is worth adding given it is now handling three more fields.

---

## 7. Documentation (folded into this pass, not deferred to a separate M4)

- **`docs/router/agent-cost-tracking.md:148`** — update the `requested_model` ledger column's documented
  meaning: was "the model lined up first," now "the model that served" (M2.3). Note the change date;
  historical rows before it are not backfilled (per the roadmap's own decision).
- **`docs/router/telemetry.md`** — document `RoutedModel`, `SubstitutionReason`, and the three new
  response headers.
- **`README.md` / `docs/HANDBOOK.md`** — one line noting the three response headers exist, per
  orchestrator-live-path-plan.md §M4's own checklist (pulled forward since M2 is what actually adds
  them).
- **`src/PLAN.md`** — flip Phase M's status line once M2 lands: still "M1 shipped, M2 shipped, M3-M4
  next" (M3/M4 remain open; do not mark the whole phase M done).

---

## 8. Build/verification order

1. Add `RoutingSubstitutionReason` (§1) — compiles standalone.
2. `ModelRouteResolutionResult` (§2) — will not compile until `RequestInterceptor`'s `Success` call site
   is updated; do both in the same commit-sized step.
3. `RequestInterceptor` (§3).
4. `RoutingTelemetryEvent` (§5) — independent of §3/§4, can be done in parallel, but `ProxyMiddleware`
   (§4) needs it to compile, so land before §4.
5. `telemetry.proto` (§6.1) + `TelemetryBroadcaster` (§6.2) — needs §5.
6. `ProxyMiddleware` (§4) — needs §2, §5.
7. GUI-side DTO/aggregation/model chain (§6.3–§6.7) — needs §6.1's regenerated `Contract` type.
8. `dotnet build` clean (zero warnings — `TreatWarningsAsErrors`), then run the full suite via the
   xunit v3 exe runner (this repo's `dotnet test` does not work on .NET 10 — run the built test exe with
   `-class`/`-method` filters instead), then the coverage check.
9. Docs pass (§7).

**Exit criteria** (mirrors orchestrator-live-path-plan.md §M2's own, restated for this pass): a
substituted request reports the client's literal name as `RequestedModel`, the served name as
`RoutedModel`, the provider id as `ResolvedModel`, and the correct `RoutingSubstitutionReason`; a
non-substituted request reports `RequestedModel == RoutedModel` and reason `None`; the three response
headers are present on both streaming and buffered responses; a failover request attributes spend and
its ledger row to the model that served, not the one first attempted; an `auto` request attributes spend
to the routed model, never to the literal string `auto`; existing telemetry tests pass with the new
fields defaulted; zero build warnings; ≥80% per-assembly coverage maintained; no test over 5 seconds.
