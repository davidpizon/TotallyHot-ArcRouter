# 0005. Protect explicit provider selections from silent substitution on any circuit trip

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-01
**Deciders:** David Pizon

## Context and Problem Statement

[ADR-0004](0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md) decided that when
a request explicitly names a specific provider/model and that provider is classified out-of-credits,
the router relays the real error instead of silently substituting a different provider — reserving
silent substitution for auto-selected requests only.

But `RequestInterceptor.ResolveModelRouteAsync`'s existing substitution block
(`RequestInterceptor.cs:480-497`) applies that same "swap in the next-best model" behavior for
**every** provider-wide circuit trip today, regardless of cause — including the router's existing
401 handling ("an invalid credential affects every model on the provider, not just the one that
surfaced it") — and regardless of whether the request was auto-selected or explicit. So once
ADR-0004 ships, the router has an inconsistency: an explicitly-selected request is told the truth
when its provider is out of credits, but is still silently redirected to a different provider when
the same provider is untrusted for any other reason (e.g. an expired API key). There is no
principled reason a client's explicit choice deserves respect only when the trip cause happens to be
billing.

This ADR generalizes ADR-0004's explicit-selection protection to every circuit-breaker trip an
explicit selection can hit — provider-wide (401/403/405/out-of-credits) and target-level (a single
model's own trip) alike — not just out-of-credits, and not just provider-wide causes.

## Decision Drivers

- **Selection-origin consistency** — whether an explicit selection is protected from silent
  substitution should depend on how the model was selected (auto vs. explicit), not on why or at what
  granularity (one model vs. the whole provider) the circuit breaker currently distrusts it.
- **No wasted network calls** — a later explicit request arriving while a provider's circuit is
  already open (tripped by an earlier request, of any cause) should not need a doomed live call just
  to produce an honest error.
- **Reuse over duplication** — this should extend the circuit-breaker/candidate-loop machinery and
  the `LiveTrafficStatus` track ADR-0004 introduces, not add a second, parallel detection or routing
  system.
- **Graceful fallback when no hot-path record exists** — a provider-wide trip can originate from an
  admin-triggered action (e.g. a 401 first observed during a "Refresh from endpoint" call, which
  only writes to `AdminActionStatus`, never `LiveTrafficStatus`). A later explicit request must still
  get a sensible error even when the hot-path track has no record to draw from.

## Considered Options

- Option A — Leave the inconsistency: only the out-of-credits classification gets truthful-error
  treatment for explicit selections; every other provider-wide trip cause keeps silently substituting
- Option B — Generalize ADR-0004's carve-out to every provider-wide circuit trip
- Option C — Generalize further still: also protect explicit selections from target-level
  (single-model, non-provider-wide) trips

## Decision Outcome

Chosen option: "Option C", because of **Selection-origin consistency**, pushed to its full conclusion.
An earlier draft of this ADR chose Option B and rejected C on the grounds that a target-level trip (a
single model's transient outage) "is not what an explicit provider/model selection is asserting a
claim about." On reconsideration, that distinction is not one a client can act on: the client asked
for one exact model, and whether the router quietly answers with a different one because the whole
provider is untrusted or because just that one model is misbehaving, the surprise is the same. An
explicit choice should never be silently overridden by the router for a circuit-breaker trip of any
kind — the client asked for that exact model, and discovering afterward that a different one actually
answered is a worse outcome than seeing a clear, honest error.

Mechanism:

- `RequestInterceptor.cs:480-497`'s substitution block currently substitutes on
  `_circuitBreaker.IsOpen(...)`/`IsProviderOpen(...)` unconditionally, whether the route was
  auto-selected or explicit. This ADR changes that block so substitution *for any circuit-breaker
  trip* — target-level (`IsOpen`) or provider-wide (`IsProviderOpen`) — only fires when the request is
  auto-selected (`isAutoSelectRequest`, or a `RoutingSubstitutionReason` other than `None`). An
  explicit selection instead takes the "relay the error" path below for either kind of trip. An
  operator-driven Stop/disable (Governance's Stop toggle, or a model dropped by the last endpoint
  scan) is unaffected by this and keeps substituting for explicit and auto-selected requests alike —
  that is a deliberate administrative action, not an outage, and is outside what this ADR (or
  ADR-0004) addresses.
- **Trip discovered live, by this very request's attempt:** relay that attempt's real upstream error
  back to the client unchanged, exactly as ADR-0004 already does for its narrower out-of-credits
  case. (This applies to the provider-wide statuses this ADR generalizes to — 401/403/405/Gemini's
  disguised-401/out-of-credits; a target-level trip discovered live, within this same request's own
  outage cascade, e.g. a fresh 5xx, is ordinary same-request failover and is a separate mechanism from
  bypassing an *already-open* circuit, unchanged by this ADR.)
- **Trip already open before this request arrived** (both target-level and provider-wide): skip the
  network call `ShouldBypass`/`ShouldBypassProvider`/`IsOpen`/`IsProviderOpen` would otherwise silently
  swallow, and synthesize the client-facing error:
  - **Provider-wide** (`IsProviderOpen`): from whichever track actually has a record, in this order:
    1. `LiveTrafficStatus.Message` (ADR-0004's track) if present — the most likely case, and the only
       one ADR-0004 itself needed to handle, since out-of-credits is always hot-path-detected.
    2. `AdminActionStatus.Message` if `LiveTrafficStatus` has none but an admin action already
       recorded why this provider is untrusted (e.g. a 401 first surfaced by "Refresh from
       endpoint").
    3. A generic, circuit-breaker-sourced "provider is temporarily unavailable" message if neither
       track has a record.
  - **Target-level** (`IsOpen` on a single model's target, provider not open): a generic
    "model is temporarily unavailable" message — both interaction-status tracks are recorded
    per-provider, not per-model, so there is no per-target record to draw from; borrowing the
    provider-wide tracks' text here would risk misattributing an unrelated provider-wide cause to one
    specific model.

### Consequences

- Good, because explicit-selection protection is now fully consistent: a client's explicit choice is
  never silently overridden by the router for a circuit-breaker trip, regardless of whether the trip
  is provider-wide or target-level, or why it happened.
- Good, because it reuses ADR-0004's `LiveTrafficStatus` track and the circuit breaker's existing
  provider/target distinction rather than introducing new state.
- Bad, because it touches `RequestInterceptor.cs`'s substitution block, which both the auto-select
  and explicit paths share today — this is a larger blast radius than ADR-0004's narrower,
  classification-scoped carve-out, and needs careful testing to avoid regressing the auto-select
  substitution behavior that must stay unchanged.
- Bad, because the three-way message fallback (live-traffic → admin-action → generic) is a new piece
  of state-reading logic with its own edge cases (e.g. which track "wins" if both have a record for
  the same provider but disagree) that ADR-0004's narrower scope never had to handle.
- Bad, because an explicit selection can now fail outright on a genuinely transient single-model blip
  (e.g. a brief run of 5xxs from one endpoint) that a silent backup would previously have papered
  over — the client sees an honest error instead of an invisible, working substitute. This is accepted
  deliberately: never surprising an explicit choice is judged more valuable than the convenience of a
  silent recovery the client never asked for.

## Pros and Cons of the Options

### Option A — Leave the inconsistency

- Good, because it requires no further changes beyond ADR-0004 — zero additional implementation
  surface.
- Bad, because it leaves a codebase inconsistency with no principled justification: the same
  explicitly-selected request is treated honestly for one trip cause and dishonestly for every other,
  which will look arbitrary (or like a bug) to a future reader or an operator comparing the two
  behaviors.

### Option B — Generalize to every provider-wide trip

- Good, because it removes the inconsistency directly, and reuses ADR-0004's track and the circuit
  breaker's existing provider/target split rather than adding new machinery.
- Good, because the fallback chain (live-traffic → admin-action → generic) means every case produces
  a real, actionable message rather than a bare "unavailable."
- Bad, because it is a change to shared substitution logic (`RequestInterceptor.cs:480-497`) used by
  every request, auto-selected or not, raising the risk of an unintended behavior change for the
  auto-select path if the routing-origin check is implemented incorrectly.
- Superseded during drafting by Option C below, before this ADR was ever accepted: Option B still
  leaves an explicit selection silently substituted on a transient single-model trip, which on
  reflection is the same kind of surprise this ADR exists to eliminate for provider-wide trips.

### Option C — Also protect against target-level (single-model) trips (chosen)

- Good, because it is maximally consistent: an explicit selection is never substituted for any
  circuit-breaker-detected reason, including a transient single-model outage.
- Good, because it reuses the exact same mechanism as Option B (relay-if-discovered-live,
  synthesize-if-already-open) with no new detection or state — the only change is which circuit-open
  checks route through it.
- Bad, because a target-level trip is a narrower, often genuinely transient condition (e.g. a few
  consecutive 5xxs from one model endpoint) that the client's explicit selection did not assert
  anything about the *account* being fine, unlike the provider-wide, account-level cases ADR-0004 was
  originally written around — an explicit request can now fail outright on a blip a silent backup
  would have absorbed. Accepted deliberately: an explicit choice is judged to deserve the truth even
  for a transient failure, not only an account-level one.

## More Information

Extends [ADR-0004](0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md), which
introduces the `LiveTrafficStatus`/`AdminActionStatus` track split and the narrower,
out-of-credits-only version of this same explicit-selection protection. Implement both ADRs
together, since ADR-0005's mechanism assumes ADR-0004's two-track store already exists.
