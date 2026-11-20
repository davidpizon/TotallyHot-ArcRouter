# Effort levels

> **Status: proposal — not implemented.** Names such as Standard, High, and Max are not routing
> modes in this repository. Nothing here is an approved plan or permission to code.
> [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
> requires an observed cost before the work is scheduled.

The names come from GitHub's page
[About GitHub Copilot Fusion](https://docs.github.com/en/copilot/concepts/models/copilot-fusion),
read on 2026-11-20.

## What Fusion's page says

Fusion has effort levels so a person can choose a quality, cost, and latency tradeoff without
picking a model. The page's table is:

| Level | What the page says |
|---|---|
| Fusion Standard | Balances cost, quality, and latency. Everyday work. Fusion selects one model. |
| Fusion High | Balanced orchestration with moderate token savings. Extra model calls may be worth the time and credits. |
| Fusion Max | Frontier capability while still considering token efficiency. Deepest reasoning. |
| Fusion Fast | Not selectable. Only used in Visual Studio Code inline chat, where a fast model is required. |

The page also says a task on High or Max can cost more than Standard because it may call more than
one model, and that you are billed for each model at that model's rate. If Max is missing from the
picker, one or more models it needs may be disabled. Local models, where the page describes them,
are eligible on Standard only.

## What Arc Router does today

There is no client-facing or dashboard control named Standard, High, or Max, and no code reads an
effort level off a request.

The quality-versus-cost reward that does exist is

```text
r = ε₁ · s + ε₂ · κ
```

with `RoutingOptions.Epsilon1` defaulting to `1.0` and `RoutingOptions.Epsilon2` defaulting to
`-0.1`. `s` is an observed quality average. `κ` is a price. A more expensive model scores lower
because `ε₂` is negative.

`UtilityRoutingPolicy` is the policy that selects with that reward. `CompositeRoutingPolicy` sends
it only traffic the classifier marked utility, and a light subagent whose
`NearBestValueFloor` is set (`SelectNearBestValue` uses the same reward, with no cold-start
fallback). The class remarks say every candidate is priced with `PriceContext.Standard`: nothing
upstream computes batch eligibility or a repeated prompt cache, so cached and batch rates are not
used for the ranking.

The default general path does not use the reward.
`CompositeRoutingPolicy`'s remarks say the Orchestrator has no cost term of its own.
`Routing:EnableOrchestratorPolicy` defaults to true; turning it off selects `AgentRouterPolicy`,
which is also not the utility formula. [`docs/how-it-learns.md`](../how-it-learns.md) Step 7 says
the same thing, and adds that the report card still *accounts* with `ε₁` and `ε₂` after the fact,
including for non-utility traffic. That report card is not a picker.

`Routing:UtilityMinQualityScore` (default `0.3`) is a floor on the utility path only. An
unobserved candidate is not dropped by it. [`docs/router/utility-model-routing.md`](utility-model-routing.md)
records a further limit: per-tier weights for `copilot-utility` versus `copilot-utility-small`
were specified and were not built. Both aliases share one `ε₁`, one `ε₂`, and one floor.

Those weights are configuration on `RoutingOptions`. No GUI component binds `Epsilon1` or
`Epsilon2`. An operator who wants a different tradeoff edits configuration. The user does not pick
a named level per prompt.

`output_config.effort` is a different thing. It is a field some clients send and some models
reject. `MessagesFeatureStripper` can remove it, and the removal is reported in
`X-ArcRouter-Stripped-Features`. That is capability stripping, not an Arc Router effort level.

## How named levels could sit on top

A named level would be a label for a pair the code already understands: `(ε₁, ε₂)` plus, on the
utility path, `UtilityMinQualityScore`. Illustrative only — these numbers are not in the product:

| Label someone might add | What it would have to mean in today's reward |
|---|---|
| Standard | The defaults already shipped: `ε₁ = 1.0`, `ε₂ = -0.1`, utility floor `0.3`. |
| High | A smaller cost penalty, or a higher floor, so a pricier candidate survives more often. |
| Max | Cost weight at or near zero, so rank by observed quality. Still one model, unless cascade exists. |

Two scopes are different projects:

1. **Labels on the paths that already use the reward.** Utility traffic and the light-subagent
   near-best pick. The general coding path would stay cost-blind. A user who sets Max on a normal
   `auto` coding request would see no change until the second scope is built.
2. **A cost term on `OrchestratorRoutingPolicy`.** That changes which model serves ordinary
   requests. [`docs/how-it-learns.md`](../how-it-learns.md) treats that formula as accounting on
   the general path, not as the live vote. Turning the account into the vote is a routing change
   and needs its own measurement.

Fusion High and Fusion Max also allow extra model calls. A level in Arc Router that only retunes
`ε₁` and `ε₂` still sends one model. Extra calls are the cascade proposal
([`todo-cascade-mode.md`](todo-cascade-mode.md)), not a consequence of renaming the weights.

Nothing in the request body is read as a level today. A design would have to choose where the
choice lives (a request field, a header, or a dashboard default) and what happens when the client
also named a real model. An explicit servable model is served as named; the policy is not
consulted.

## Measured cost before anyone builds it

The weights are already operator-configurable. A named picker is worth building only if someone
records a cost of the current shape: operators mis-set `Epsilon1` / `Epsilon2`, or users cannot
reach a tradeoff the utility path already supports, with a count of requests or a support trail
attached. [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
does not treat "Fusion has three names" as that cost. This file does not record one.

CodeGraph had no index for this tree when the note was written, and Serena was not asked. This is
not a smell finding and does not belong in
[`code-smell-refactoring-plan.md`](code-smell-refactoring-plan.md).
