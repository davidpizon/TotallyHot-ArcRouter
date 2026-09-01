# 0004. Surface out-of-credits provider failures on the Providers tab

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-01
**Deciders:** David Pizon

## Context and Problem Statement

The router forwards chat requests to upstream providers and occasionally hits one whose account is
out of credits. A concrete instance: Anthropic returned **HTTP 400** with body message `"Your credit
balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase
credits."` Two things should happen when this occurs: the Providers tab should show a warning icon
naming the reason, and — when the router itself chose the model — the in-flight request should fail
over to an alternative provider rather than simply failing.

Neither is a green-field feature; both already have machinery in place that a naive implementation
would either miss or accidentally break:

- **The Providers-tab warning icon and tooltip already exist**, but only for admin-triggered
  actions. `ProviderInteractionStatusStore`/`ProviderAdminView.LastInteraction`/
  `ProvidersAdmin.razor:73-78` render exactly the icon+tooltip shape this ADR needs, but the store's
  doc comment states it is "deliberately narrower than the CircuitBreaker's live-traffic health" and
  reflects only refresh/discover/scan actions (`RecordFailure`'s three call sites, all in
  `ManagementFacade.cs`). The hot proxy request path (`ProxyMiddleware.cs`) never touches this store
  today, and the store keeps a single shared slot per provider — wiring the hot path into that same
  slot naively would let an ordinary successful chat completion silently erase an unrelated
  admin-recorded failure (e.g. "Refresh from endpoint failed: expired key"), and vice versa.
- **The router already has a full within-request failover loop**, but it explicitly excludes this
  case. `RequestInterceptor.ResolveModelRouteAsync` (`RequestInterceptor.cs:285-531`) builds an
  ordered candidate list across every eligible model and provider; `ProxyMiddleware.InvokeCoreAsync`
  (`ProxyMiddleware.cs:449-571`) tries them in order within the same request, but its documented
  retry rule (`ProxyMiddleware.cs:449-455`) is explicit: a candidate is retried against the next one
  "only on a genuine upstream *outage* ... **never on a client-fault status (400/401/403/422), which
  a backup would reject identically**." That assumption is wrong for a billing failure carried on a
  400: unlike a malformed request, a different provider's account is not guaranteed to be out of
  credits too. 429 already has a documented carve-out from this rule (`nextProviderDiffers`,
  `ProxyMiddleware.cs:563-571`) — the shape a new carve-out should follow.
- **No classification of "out of credits" exists anywhere.** Detection cannot rely on HTTP status
  code alone: Anthropic's real case is a 400, not a 402, while OpenAI signals via a typed
  `insufficient_quota` error code (typically on a 429). ArcRouter's own 402 is unrelated — that's the
  router rejecting a request itself over a configured `DollarCap`/`TokenCap`, not reading an upstream
  billing failure.
- `ICircuitBreaker.RecordProviderFailure(provider)` (`CircuitBreaker.cs:97-154`) already exists for
  exactly this shape of problem: one call trips every model on that provider immediately, modeled on
  a 401, and `ProxyMiddleware.cs:531-544` already bypasses a provider-wide-open circuit for every
  subsequent request with no network call — the mechanism to reuse for blocking future requests.

This ADR deliberately keeps one part of the decision narrow: whether an *explicitly*-selected
request should see the provider's real error instead of a silent substitution is decided here only
for the out-of-credits classification. Whether that same protection should extend to every
provider-wide circuit trip (including the router's existing 401 handling) is a separate, broader
routing-policy question, decided in [ADR-0005](0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-trip.md).

So this ADR resolves three coupled parts: how to detect the failure, whether the in-flight request
that discovers it also fails over (and for whom), and how to surface it on the Providers tab
including how that surfacing later clears.

## Decision Drivers

- **Status-code independence** — detection must not depend on HTTP status code alone: Anthropic's
  case is a 400, not a 402, and OpenAI's signal is a typed error code rather than a distinct status.
- **In-flight benefit** — the request that surfaces the failure should benefit from failover if a
  genuinely distinct alternative (a different provider) exists, not only requests that arrive after
  it — this is the explicit ask: find an alternative provider, not just remember that this one
  failed.
- **Narrow retry carve-out** — the fix must not silently invalidate the documented "never retry a
  client-fault status" rule for the other 4xx cases it doesn't apply to (e.g. a malformed request,
  which genuinely would fail identically on a backup); the carve-out must be scoped to this one
  classification.
- **Track separation** — the Providers-tab indicator must not conflate a live billing failure with
  the existing admin-action failure indicator, and neither may silently clear the other: they answer
  two different questions about the same provider (does live traffic work vs. did the last admin
  action work).
- **Provider-wide self-clearing** — the warning must clear itself the moment any subsequent call to
  that provider succeeds (not necessarily the same model — the underlying cause, billing, is
  account-wide), with no operator action required.
- **Explicit-selection honesty, scoped to this classification** — a request that explicitly named a
  provider now classified out-of-credits should see that real error rather than being silently
  served by a different provider it never asked for. This ADR limits the driver to the out-of-credits
  classification; generalizing it to every provider-wide trip cause is ADR-0005's decision, not this
  one.

## Considered Options

- Option A — Detect and record for future requests only; leave the in-flight request's failure
  response unchanged
- Option B — Detect, record, fail over an auto-selected in-flight request, and relay the truthful
  error to an explicit selection — via a targeted carve-out mirroring the existing 429 cross-provider
  retry
- Option C — Generalize the retry rule to retry any 4xx when a different-provider candidate exists

## Decision Outcome

Chosen option: "Option B", because of **In-flight benefit**, **Narrow retry carve-out**, and
**Explicit-selection honesty**. It is the only option that satisfies the explicit ask — failover for
the request that surfaced the problem, not only future ones — while keeping the change scoped to the
one classification this ADR defines, unlike Option C, which would touch how the router treats every
other 4xx it sees today.

Concretely, this decision resolves the three coupled parts as follows:

1. **Detection.** Classification lives alongside the existing non-2xx handling in
   `ProxyMiddleware.cs`, independent of (but feeding) the circuit-breaker classification already
   there. It inspects the parsed error body — a provider's typed error code where one exists
   (OpenAI's `insufficient_quota`), falling back to a message-substring match ("credit", "balance",
   "quota", "billing") for providers with no typed field, as Anthropic's case requires. The exact
   per-provider parsing rules are implementation detail, not fixed by this ADR (see More
   Information).
2. **Failover.** On a classified out-of-credits response: always call
   `_circuitBreaker.RecordProviderFailure(provider)` (the existing immediate, provider-wide trip used
   for 401) so every future request bypasses this provider until its cooldown, regardless of how the
   failed request selected its model. For the in-flight request itself:
   - **Auto-selected** (`isAutoSelectRequest`, or a `RoutingSubstitutionReason` other than `None` at
     the point of failure): extend the `ProxyMiddleware.cs:563-571`-style cross-provider check so an
     out-of-credits classification is retried within the same request against the next
     different-provider candidate, the same way 429 is already special-cased.
   - **Explicit selection** (`RoutingSubstitutionReason.None` up to the point of failure — the
     client's own choice): never substitute a different provider for this classification. If this
     request's own attempt discovered the trip, relay that attempt's real upstream error back to the
     client unchanged. If the provider's circuit was already open before this request started (a
     later explicit request arrives while an earlier out-of-credits trip's cooldown is still active),
     skip the network call `ShouldBypassProvider`/`IsProviderOpen` would otherwise silently swallow,
     and instead synthesize the client-facing error from the `LiveTrafficStatus` track's recorded
     `Message` (part 3) — the same text already shown in the Providers-tab tooltip. Because this
     classification is always detected on the hot path, `LiveTrafficStatus` always has a message to
     draw from here.

   Every other client-fault status not specifically classified this way keeps its current behavior
   unchanged, including today's substitution for an explicit selection on other provider-wide trip
   causes (e.g. a 401) — that inconsistency is real, but resolving it is ADR-0005's job, not this
   one.
3. **Providers-tab surfacing, including recovery.** Split `IProviderInteractionStatusStore`'s single
   shared slot into two independently-maintained tracks per provider:
   - `AdminActionStatus` — exactly today's existing behavior, untouched: written only by
     `ManagementFacade.cs`'s three admin-triggered call sites (refresh/discover/scan).
   - `LiveTrafficStatus` — new: written only from the hot request path in `ProxyMiddleware.cs`. On a
     classified out-of-credits response it calls `RecordFailure` with a `Kind` (`OutOfCredits`,
     extensible to other live-traffic reasons later) so the tooltip can word it precisely ("Out of
     credits: {Message}"). Symmetrically, it calls `RecordSuccess(provider, operation)` on every
     successful (2xx) forwarded response to *any* model under that provider — not only the model that
     failed, since an out-of-credits trip is account-wide, not model-specific — so the track clears
     itself the moment the provider next works, with no operator action needed.

   Because the two tracks are independent, a live-traffic success can never erase an admin-recorded
   failure or vice versa. `ProviderAdminView` exposes both (`AdminAction` and `LiveTraffic`, each a
   nullable `ProviderInteractionStatusAdminView`); `ProvidersAdmin.razor`'s existing
   `@if (provider.LastInteraction is { Ok: false } ...)` block becomes two independent checks, one per
   track, so a provider can show up to two distinct warnings at once if both are true.

### Consequences

- Good, because the request that discovers the outage gets the benefit of failover itself (when
  auto-selected), not just requests that arrive afterward.
- Good, because it reuses existing machinery wholesale — `RecordProviderFailure`, the
  candidate-ranking/failover loop, and the `LastInteraction`/warning-icon/tooltip scaffold — rather
  than building a parallel detection, routing, or UI system.
- Good, because a client that explicitly picked a now-out-of-credits provider is told the truth
  instead of silently receiving a response from a different provider it never selected.
- Good, because splitting the store into two tracks genuinely resolves the conflation risk: an
  admin-recorded failure and a live-traffic failure can no longer erase one another, and both can be
  shown at once when both are true.
- Bad, because it adds a hot-path dependency on `IProviderInteractionStatusStore` that didn't exist
  before, a second tracked record per provider, and a new typed `Kind` field to keep in sync across
  proxy and GUI.
- Bad, because per-provider detection heuristics (message-substring matching, typed error codes) will
  need upkeep as providers change their error response shapes — there is no stable, universal signal
  to rely on.
- Bad, because reusing a recorded `Message` as the client-facing error for a later explicit request
  against an already-open circuit means that error text is only as fresh as the last live attempt
  that wrote it — if the account was topped up moments ago but no successful call has landed yet to
  clear the track, the client sees a slightly stale reason rather than a live retry.
- Neutral, because the exact per-provider error-body parsing rules are left as follow-up
  implementation detail rather than fixed by this ADR.
- Neutral, because this ADR leaves an explicit selection unprotected against every *other*
  provider-wide trip cause (e.g. 401) — a known, deliberate scope boundary resolved separately in
  ADR-0005.

## Pros and Cons of the Options

### Option A — Detect and record for future requests only

Classify the response, call `RecordProviderFailure` and the new typed `RecordFailure(...,
Kind.OutOfCredits)`, but leave the in-flight request's failure response unchanged — no same-request
retry.

- Good, because it is the smallest change and leaves the documented "never retry a client-fault
  status" invariant completely untouched.
- Good, because it still fixes the Providers-tab visibility gap on its own.
- Bad, because it does not satisfy the explicit ask: the request that surfaces the problem is the
  one still handed back to the client as a failure, even when a working alternative provider exists
  one hop away.

### Option B — Detect, record, fail over auto-selected requests, and tell explicit ones the truth (chosen)

All of Option A, plus a targeted carve-out in the retry loop mirroring the existing 429
cross-provider retry for auto-selected requests, and a narrower carve-out (scoped to this
classification only) so an explicit agent/model selection gets the provider's real error relayed
instead of a silent substitution.

- Good, because it directly satisfies "find an alternative provider if possible" for the request
  that surfaced the problem, when the router made the choice.
- Good, because the carve-out is scoped narrowly (mirrors the existing 429 precedent, applies to one
  classification) rather than reopening the general client-fault retry question.
- Bad, because it is more implementation surface than Option A: the retry loop's candidate-skip logic
  needs a new classification input and a routing-origin check (auto-select vs. explicit), plus the
  store split into two tracks — not just a single store write.

### Option C — Generalize the retry rule to retry any 4xx with a different-provider candidate

Instead of a narrow, classified carve-out, broaden the existing rule so any 400/401/403/422 is
retried across providers whenever a different-provider candidate exists.

- Good, because it is a single, simple rule change rather than a per-classification carve-out, and
  would also incidentally cover failure shapes this ADR didn't anticipate.
- Bad, because it changes behavior for every other 4xx the router sees today, silently defeating the
  documented reasoning for why those statuses aren't retried — a malformed request (e.g. a bad
  `tools` schema) genuinely would fail identically on a backup, so retrying it just adds latency and
  duplicate upstream calls for no chance of success.
- Bad, because it is a much larger blast radius than the problem this ADR is scoped to solve, and
  would need its own dedicated evaluation of every existing 4xx path rather than riding along with
  this decision.

## More Information

This ADR documents the intended design only; no implementation has landed yet. Open follow-up for
the implementing PR: confirm the exact per-provider error-body parsing rules (OpenAI's
`insufficient_quota` error code vs. Anthropic's message-substring match vs. a generic keyword
fallback for providers with neither) against live provider responses, since the two known providers
already disagree on both status code and error shape.

[ADR-0005](0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-trip.md)
extends this ADR's explicit-selection protection (part 2 above) from "out of credits only" to every
provider-wide circuit trip cause.
