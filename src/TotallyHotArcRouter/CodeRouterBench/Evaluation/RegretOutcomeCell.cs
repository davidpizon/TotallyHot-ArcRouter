namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// One <c>(task, model)</c> cell of the outcome matrix <c>O[i,j] = (s_ij, κ_ij)</c>
/// (docs/router/regret-evaluation-harness-plan.md "Metrics"): the verifier score and cost a specific
/// model actually achieved on a specific benchmark task, already resolved from
/// <c>benchmark_id_results</c>/<c>benchmark_ood_results</c> (cost falling back to
/// <c>benchmark_models</c> pricing upstream of this type, per the plan's <c>$Total</c> definition).
/// </summary>
/// <param name="Score">The verifier score <c>s_ij</c>, in <c>[0,1]</c>.</param>
/// <param name="CostUsd">The cost <c>κ_ij</c> in USD, already resolved (never a raw-null passthrough).</param>
/// <param name="TotalTokens">
/// The total tokens (input + output) this cell consumed, for the <c>TotTok</c> metric. <see langword="null"/>
/// when the source row published no token counts at all.
/// </param>
public sealed record RegretOutcomeCell(double Score, double CostUsd, long? TotalTokens);