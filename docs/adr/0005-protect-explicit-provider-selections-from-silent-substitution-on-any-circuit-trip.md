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

This ADR generalizes ADR-0004's explicit-selection protection to every provider-wide circuit trip,
not just out-of-credits.

## Decision Drivers

- **Selection-origin consistency** — whether an explicit selection is protected from silent
  substitution should depend on how the model was selected (auto vs. explicit), not on why the
  provider happens to be untrusted right now.
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

Chosen option: "Option B", because of **Selection-origin consistency** — the alternative (Option A)
leaves a codebase inconsistency that would look arbitrary to the next person who hits it, and Option
C protects against a failure mode (a single model's transient outage) that is not what an explicit
provider/model selection is asserting a claim about.

Mechanism:

- `RequestInterceptor.cs:480-497`'s substitution block currently substitutes on
  `_circuitBreaker.IsOpen(...)`/`IsProviderOpen(...)` unconditionally, whether the route was
  auto-selected or explicit. This ADR changes that block so substitution *specifically for a
  provider-wide trip* (`IsProviderOpen`) only fires when the request is auto-selected
  (`isAutoSelectRequest`, or a `RoutingSubstitutionReason` other than `None`). An explicit selection
  instead takes the "relay the error" path below. Target-level-only trips (`IsOpen` on a single
  model's target, not a provider-wide trip) are unaffected and keep today's behavior for both
  auto-selected and explicit requests — matching the ADR-0004 precedent this generalizes, which was
  itself scoped to `RecordProviderFailure`'s provider-wide trip, not per-model outages.
- **Trip discovered live, by this very request's attempt:** relay that attempt's real upstream error
  back to the client unchanged, exactly as ADR-0004 already does for its narrower out-of-credits
  case.
- **Trip already open before this request arrived:** skip the network call
  `ShouldBypassProvider`/`IsProviderOpen` would otherwise silently swallow, and synthesize the
  client-facing error from whichever track actually has a record, in this order:
  1. `LiveTrafficStatus.Message` (ADR-0004's track) if present — the most likely case, and the only
     one ADR-0004 itself needed to handle, since out-of-credits is always hot-path-detected.
  2. `AdminActionStatus.Message` if `LiveTrafficStatus` has none but an admin action already recorded
     why this provider is untrusted (e.g. a 401 first surfaced by "Refresh from endpoint").
  3. A generic, circuit-breaker-sourced "provider is temporarily unavailable" message if neither
     track has a record — a provider-wide trip with no recorded reason in either track (e.g. from a
     lower-level condition the circuit breaker reacts to that doesn't build a text explanation
     today).

### Consequences

- Good, because explicit-selection protection is now consistent across trip causes: a client's
  explicit choice is never silently overridden by *why* the provider is untrusted, resolving the
  inconsistency ADR-0004 knowingly left open.
- Good, because it reuses ADR-0004's `LiveTrafficStatus` track and the circuit breaker's existing
  provider/target distinction rather than introducing new state.
- Bad, because it touches `RequestInterceptor.cs`'s substitution block, which both the auto-select
  and explicit paths share today — this is a larger blast radius than ADR-0004's narrower,
  classification-scoped carve-out, and needs careful testing to avoid regressing the auto-select
  substitution behavior that must stay unchanged.
- Bad, because the three-way message fallback (live-traffic → admin-action → generic) is a new piece
  of state-reading logic with its own edge cases (e.g. which track "wins" if both have a record for
  the same provider but disagree) that ADR-0004's narrower scope never had to handle.
- Neutral, because target-level (single-model) trips remain out of scope for this protection — an
  explicit request can still be substituted to a different model on transient per-model outages;
  only a provider-wide trip triggers the "tell the truth" behavior.

## Pros and Cons of the Options

### Option A — Leave the inconsistency

- Good, because it requires no further changes beyond ADR-0004 — zero additional implementation
  surface.
- Bad, because it leaves a codebase inconsistency with no principled justification: the same
  explicitly-selected request is treated honestly for one trip cause and dishonestly for every other,
  which will look arbitrary (or like a bug) to a future reader or an operator comparing the two
  behaviors.

### Option B — Generalize to every provider-wide trip (chosen)

- Good, because it removes the inconsistency directly, and reuses ADR-0004's track and the circuit
  breaker's existing provider/target split rather than adding new machinery.
- Good, because the fallback chain (live-traffic → admin-action → generic) means every case produces
  a real, actionable message rather than a bare "unavailable."
- Bad, because it is a change to shared substitution logic (`RequestInterceptor.cs:480-497`) used by
  every request, auto-selected or not, raising the risk of an unintended behavior change for the
  auto-select path if the routing-origin check is implemented incorrectly.

### Option C — Also protect against target-level (single-model) trips

- Good, because it would be maximally consistent: an explicit selection is never substituted for any
  reason, including a transient single-model outage.
- Bad, because a target-level trip is a narrower, often genuinely transient condition (e.g. a few
  consecutive 5xxs from one model endpoint) that the client's explicit selection did not assert
  anything about the *account* being fine — conflating it with the provider-wide, account-level cases
  this ADR and ADR-0004 are about would change behavior for a class of failure neither ADR was
  written to address.
- Bad, because it has no concrete motivating case in this conversation, unlike the provider-wide
  401/out-of-credits precedent — expanding scope without a real driver risks solving a problem nobody
  has yet.

## More Information

Extends [ADR-0004](0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md), which
introduces the `LiveTrafficStatus`/`AdminActionStatus` track split and the narrower,
out-of-credits-only version of this same explicit-selection protection. Implement both ADRs
together, since ADR-0005's mechanism assumes ADR-0004's two-track store already exists.
