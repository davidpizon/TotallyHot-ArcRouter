using System.Diagnostics.Metrics;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// The <see cref="System.Diagnostics.Metrics.Meter"/> and instruments this codebase publishes usage
/// telemetry through, for any OTLP/Prometheus exporter the host process configures
/// (<c>docs/router/token-tracking-improvements.md</c> §5.12). Creating a <see cref="Meter"/> and its
/// instruments is always cheap - no listener means near-zero overhead - so this class is unconditional;
/// only whether an actual OTLP exporter is wired up (the "optional" part of §5.12) is a hosting-level
/// decision made elsewhere.
/// </summary>
/// <remarks>
/// None of the published instrument names below end in <c>_total</c>, even though every instrument here is
/// a <see cref="Counter{T}"/> - the C# field names carry that suffix (<see cref="ExtractionFailedTotal"/>,
/// <see cref="TokensTotal"/>, etc.) so callers can tell a counter from a gauge at a glance, but the
/// exported name should not: OTel's Prometheus bridge already appends <c>_total</c> to every cumulative-sum
/// instrument, so baking it into the instrument name here would double it up downstream.
/// </remarks>
public static class UsageMetrics
{
    /// <summary>The meter name every usage instrument below is published under.</summary>
    public const string MeterName = "TotallyHot.ArcRouter.Usage";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Counts requests whose response usage could not be extracted at all - neither the primary
    /// head-capped parse nor <see cref="IncrementalUsageScanner"/>'s tail-window fallback succeeded.
    /// Tagged with <c>provider</c> so a sustained rise for one provider (a translator regression, an
    /// upstream response-shape change) is distinguishable from routine, expected misses (malformed
    /// upstream bodies, providers with no usage block at all).
    /// </summary>
    public static readonly Counter<long> ExtractionFailedTotal = Meter.CreateCounter<long>(
        name: "arcrouter.usage.extraction_failed",
        description: "Requests whose response usage could not be extracted by any parser.");

    /// <summary>
    /// Counts tokens by dimension - tagged <c>provider</c>, <c>model</c>, and <c>kind</c> (one of
    /// <c>prompt</c>, <c>completion</c>, <c>cache_creation</c>, <c>cache_read</c>), following the ledger's
    /// own four-dimension token model (§5.2/§5.4).
    /// </summary>
    public static readonly Counter<long> TokensTotal = Meter.CreateCounter<long>(
        name: "arcrouter.usage.tokens",
        unit: "{token}",
        description: "Tokens consumed, by provider/model/kind.");

    /// <summary>
    /// Sums estimated USD cost - tagged <c>provider</c> and <c>model</c>. Unpriced requests contribute nothing (see
    /// <see cref="UnpricedRequestsTotal"/>).
    /// </summary>
    public static readonly Counter<double> CostUsdTotal = Meter.CreateCounter<double>(
        name: "arcrouter.usage.cost_usd",
        unit: "{USD}",
        description: "Estimated USD cost, by provider/model.");

    /// <summary>Counts requests with extracted usage but no known price - tagged <c>provider</c>.</summary>
    public static readonly Counter<long> UnpricedRequestsTotal = Meter.CreateCounter<long>(
        name: "arcrouter.usage.unpriced_requests",
        description: "Requests whose cost could not be priced.");
}