# 0009. Estimate the routing counterfactual from the request's own prompt, not a global token average

**Status:** proposed
**Date:** 2026-09-10
**Deciders:** David Pizon (operator), Claude Opus 5

## Context and Problem Statement

The Routing ROI pipeline prices a counterfactual — "what would this turn have cost if the `dim_best`
baseline model had served it?" — in `TaxonomyComparisonService.EstimateCounterfactual`. It obtains the
baseline's token counts from `ITranscriptStore.LoadObservedTokenAveragesAsync`, which is an *all-time,
unweighted, global* mean per model:

```sql
SELECT routed_model, AVG(input_tokens), AVG(output_tokens), COUNT(*)
FROM request_transcripts
WHERE input_tokens IS NOT NULL AND output_tokens IS NOT NULL
GROUP BY routed_model;
```

Because that average is unconditioned on the request, a 500-token turn and a 150,000-token turn receive
the **identical** baseline estimate — so the ROI chart's per-turn bars carry almost no per-turn
information. Three further defects follow from the same source: cache-creation and cache-read tokens are
dropped even though `ModelPrice.EstimateCost(UsageInfo)` prices them at their own tiers; a baseline model
that has never been routed to has no average at all and yields `null`, which is precisely the cold-start
case the counterfactual exists to answer; and the mean is unconditioned on dimension and never ages out.

Arc Router has no LLM tokenizer anywhere in the codebase — every billable count originates from
provider-reported `usage` on the response — so counting the baseline's tokens has not previously been
possible. Reviewing [purplestrike/TokenMeter](https://github.com/purplestrike/TokenMeter) surfaced this
gap, though that project itself supplies no monitoring capability (see "Considered Options", option 1).

## Decision Drivers

- The ROI metric is only actionable if it varies per request; a constant baseline makes the chart decorative.
- Nothing may be added to the proxy hot path — `ProxyMiddleware` latency is the product's core constraint.
- The router must keep working fully offline; a token count must never *require* network reachability.
- `tiktoken` is the wrong tokenizer for Claude. Anthropic documents that it undercounts Claude tokens by
  ~15–20% on typical text and by considerably more on code, and Arc Router routes `anthropic` and
  `openai` in comparable volume.
- The codebase already refuses to be confidently wrong: `CostConfidence` exists precisely so an
  approximate figure can be *labeled* approximate rather than silently passed off as exact.
- A new outbound egress destination is a security-boundary change and must be opt-in, not a default.

## Considered Options

- **Option 1** — Transplant TokenMeter's token monitoring into Arc Router, replacing the existing stack.
- **Option 2** — Local tokenization for every model using `Microsoft.ML.Tokenizers` (tiktoken encodings).
- **Option 3** — Call each provider's token-counting API for every counterfactual estimate.
- **Option 4** — Local tokenization always, with a background sampler that learns a per-model correction
  factor from the provider's own counting API.

## Decision Outcome

Chosen option: **"Option 4"**, because it is the only option that satisfies the offline-first driver and
the tokenizer-accuracy driver simultaneously. Option 2 is offline but bakes a systematic,
provider-correlated error into the metric; option 3 is accurate but makes every estimate depend on
network reachability and a live credential. Option 4 keeps the estimate path local and deterministic,
and treats provider-exact counts as a *calibration signal* rather than a runtime dependency — so
accuracy improves when the network is available and never regresses below "labeled approximation" when
it is not.

Two supporting decisions fall out of the same reasoning:

1. **The estimator runs off the hot path.** `request_transcripts` already persists `prompt_text`,
   `dimension`, and `input_tokens`, and `TaxonomyComparisonService` already drains in the background
   under the `InFlightRequestGauge` hard pause. Counting therefore costs the proxy path nothing.
2. **Counterfactual *output* tokens stay estimated, not counted, and are conditioned on
   `(dimension × prompt-size bucket)`.** A model that never ran produced no output, so its output length
   is unknowable in principle. Conditioning on prompt-size bucket recovers most of the per-request
   sensitivity that a naive global mean loses, without requiring same-prompt co-occurrence data that
   Arc Router's one-model-per-request routing never produces.

### Consequences

- Good, because the ROI bars become genuinely per-request on the input dimension, which is the larger
  and more variable half of most turns' cost.
- Good, because cache-read and cache-creation tokens finally reach the baseline figure through the
  cache-aware `EstimateCost(UsageInfo, out bool)` overload, removing a systematic under-estimate.
- Good, because the CodeRouterBench prior gives never-routed baseline models an estimate for the first
  time, closing the cold-start hole.
- Bad, because a second outbound egress destination (`api.anthropic.com/v1/messages/count_tokens`) now
  exists in the codebase, with a *different credential scope* from `AnthropicUsageReportClient`: it uses
  an ordinary inference API key named by its own `TokenizationOptions.ApiKeyEnvVar` setting rather
  than a key borrowed from routing, and never the Admin API key. It is disabled by default and gated
  behind `TokenizationOptions.CalibrationEnabled`.
- Bad, because the counterfactual's output half remains an estimate however well conditioned, so the ROI
  figure can never become exact. This is inherent to counterfactuals, not to this design.
- Neutral, because `ModelTokenAverage` is replaced by a conditioned type, which forces a signature change
  on `ITranscriptStore` and updates to its stub implementations across at least five test files.
- Neutral, because three `Microsoft.ML.Tokenizers*` packages join the dependency set — the same
  `Microsoft.ML.*` family as the existing `Microsoft.ML.OnnxRuntime` reference, with embedded encoding
  data rather than downloaded artifacts.

## Pros and Cons of the Options

### Option 1 — Transplant TokenMeter

The request that prompted this ADR. Investigation showed there is nothing to transplant.

- Good, because it would have reused proven code rather than writing new code.
- Bad, because TokenMeter is a client-side React SPA that compares serialization formats by token count.
  It carries no pricing data, no per-request/model/session tracking, no cost estimation, no persistence
  beyond a `localStorage` list of the last 10 inputs, and no backend.
- Bad, because adopting it would mean discarding `UsageLedger`, `UsageRollupStore`, `PriceCatalog`,
  `CostConfidence`, and `CostReconciliationService` — a severe capability regression.
- Bad, because the repository publishes no LICENSE file (the GitHub API reports `license: null` and
  `package.json` is marked `"private": true`), so its code is not safely reusable.

### Option 2 — Local tiktoken everywhere

- Good, because it is fully offline, deterministic, and adds no egress.
- Good, because it is the smallest implementation.
- Bad, because it is *systematically* wrong for Claude by ~15–20%, worse on code — and Claude is a
  first-class routed provider here. The error is not noise that averages out; it is a consistent bias
  correlated with provider, which is exactly the axis the ROI metric compares along.

### Option 3 — Provider counting API per estimate

- Good, because it is the most accurate option available, and Anthropic's `count_tokens` is free.
- Bad, because every ROI estimate would then require network reachability and a valid credential,
  breaking the offline-first driver.
- Bad, because it couples a background analytics computation to provider rate limits and availability.

### Option 4 — Local-first with learned calibration

- Good, because the estimate path stays local, deterministic, and offline-safe.
- Good, because sampling against a free, model-specific endpoint corrects the provider-correlated bias
  that sinks option 2, at a bounded and opt-in cost.
- Good, because the correction factor is observable and storable, so the estimate's basis can be
  reported honestly through `CostConfidence` rather than asserted.
- Bad, because it is the largest of the three viable implementations, and introduces a calibration store
  and a background service that must respect the in-flight hard pause.
- Bad, because a stale or thinly-sampled factor is a silent accuracy risk; mitigated by an
  exponentially-weighted mean and a minimum sample count before a factor is trusted.

## More Information

- Execution plan: [`docs/router/counterfactual-token-estimation-plan.md`](../router/counterfactual-token-estimation-plan.md)
- The estimator this replaces: `TaxonomyComparisonService.EstimateCounterfactual`, introduced by
  [`self-organizing-classification-plan.md`](../router/self-organizing-classification-plan.md) Phase T4
  and extended by [`routing-roi-regret-plan.md`](../router/routing-roi-regret-plan.md).
- The confidence ladder reused for labeling: `Telemetry/CostConfidence.cs`, rationale in
  [`token-tracking-improvements.md`](../router/token-tracking-improvements.md) §5.6/§5.7.
- The egress client this one is modeled on: `Telemetry/AnthropicUsageReportClient.cs`.
