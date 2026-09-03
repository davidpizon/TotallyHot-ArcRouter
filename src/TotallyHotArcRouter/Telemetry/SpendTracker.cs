using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Tracks a personal-scale running spend total across the process lifetime - not a team/org billing
/// system, just enough for a single developer to see what they've spent. Every recorded request's
/// running total is logged (visible as terminal output through the configured Serilog Console sink),
/// gated by <see cref="SpendTrackingOptions.Enabled"/>. §5.13 retired the JSON Lines file this class
/// used to append to alongside the console line - the durable usage ledger supersedes it - so this is
/// now the console line and the in-memory running total only.
/// </summary>
public interface ISpendTracker
{
    /// <summary>
    /// Records one routed request's usage/cost - <paramref name="estimatedCostUsd"/> and the token
    /// counts may all be <see langword="null"/> when usage couldn't be determined (e.g. an
    /// unsupported provider, or a model with no price data); the request still
    /// counts toward <see cref="SpendSummary.RequestCount"/>, just contributing zero cost/tokens.
    /// </summary>
    /// <returns>The updated running summary.</returns>
    Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens, decimal? estimatedCostUsd,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current running total without recording a new request.</summary>
    SpendSummary GetSummary();
}

/// <summary>Running spend/usage totals accumulated since process start.</summary>
/// <param name="RequestCount">The total number of requests recorded.</param>
/// <param name="TotalPromptTokens">The cumulative prompt/input tokens across every recorded request.</param>
/// <param name="TotalCompletionTokens">The cumulative completion/output tokens across every recorded request.</param>
/// <param name="TotalCostUsd">The cumulative estimated USD cost across every recorded request with a known cost.</param>
/// <param name="UnpricedRequests">
/// Requests counted in <paramref name="RequestCount"/> whose cost was unknown and therefore contributed
/// nothing to <paramref name="TotalCostUsd"/>. A non-zero value here means the running total is a floor,
/// not an estimate - the difference between "you have spent $4.10" and "you have spent at least $4.10,
/// and some requests could not be priced" (see <c>docs/router/token-tracking-improvements.md</c> §5.6).
/// </param>
public readonly record struct SpendSummary(
    int RequestCount,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    decimal TotalCostUsd,
    int UnpricedRequests = 0);

/// <inheritdoc cref="ISpendTracker"/>
public sealed class SpendTracker : ISpendTracker
{
    private readonly ILogger<SpendTracker> _logger;
    private readonly SpendTrackingOptions _options;

    // Guards the four running totals below (fast, synchronous - never held across an await).
    private readonly object _totalsLock = new();

    private int _requestCount;
    private long _totalCompletionTokens;
    private decimal _totalCostUsd;
    private long _totalPromptTokens;
    private int _unpricedRequests;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendTracker"/> class.
    /// </summary>
    public SpendTracker(ILogger<SpendTracker> logger, IOptions<SpendTrackingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens,
        decimal? estimatedCostUsd, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Task.FromResult(GetSummary());

        SpendSummary summary;
        lock (_totalsLock)
        {
            _requestCount++;
            _totalPromptTokens += promptTokens ?? 0;
            _totalCompletionTokens += completionTokens ?? 0;

            // A null cost contributes nothing to the total and is counted separately (UnpricedRequests)
            // rather than folded in as 0 - the aggregate-level twin of ModelPrice never estimating from
            // unverified rates: an aggregate that silently sums only the priced subset is just as
            // misleading as a single request's null collapsing into a confident zero.
            if (estimatedCostUsd is decimal knownCost)
                _totalCostUsd += knownCost;
            else
                _unpricedRequests++;

            summary = new SpendSummary(RequestCount: _requestCount, TotalPromptTokens: _totalPromptTokens,
                TotalCompletionTokens: _totalCompletionTokens, TotalCostUsd: _totalCostUsd,
                UnpricedRequests: _unpricedRequests);
        }

        // Logged as two separate calls (rather than pre-formatting the decimals to strings, e.g. via
        // ToString("F6")) so CostUsd/RunningTotalUsd stay structured decimal properties for any
        // sink that captures them, not culture-dependent, query-unfriendly strings - the ":F6" format
        // specifier only affects the rendered text, not the captured property value.
        if (estimatedCostUsd is decimal cost)
            _logger.LogInformation(
                message:
                "[SPEND] model={Model} cost=${CostUsd:F6} runningTotal=${RunningTotalUsd:F6} requests={RequestCount}",
                model,
                cost,
                summary.TotalCostUsd,
                summary.RequestCount);
        else
            _logger.LogInformation(
                message:
                "[SPEND] model={Model} cost=unknown runningTotal=${RunningTotalUsd:F6} requests={RequestCount}",
                model,
                summary.TotalCostUsd,
                summary.RequestCount);

        return Task.FromResult(summary);
    }

    /// <inheritdoc/>
    public SpendSummary GetSummary()
    {
        lock (_totalsLock)
        {
            return new SpendSummary(RequestCount: _requestCount, TotalPromptTokens: _totalPromptTokens,
                TotalCompletionTokens: _totalCompletionTokens, TotalCostUsd: _totalCostUsd,
                UnpricedRequests: _unpricedRequests);
        }
    }
}

/// <summary>
/// Safe no-op default for callers that don't opt into spend tracking (e.g. tests constructing
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/> directly without a DI container) - mirrors the
/// "fresh, private, harmless default" pattern every other optional
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>
/// dependency already follows.
/// </summary>
public sealed class NullSpendTracker : ISpendTracker
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static readonly NullSpendTracker Instance = new();

    /// <summary>Private: use <see cref="Instance"/> instead of constructing new instances.</summary>
    private NullSpendTracker()
    {
    }

    /// <inheritdoc/>
    public Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens,
        decimal? estimatedCostUsd, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(default(SpendSummary));
    }

    /// <inheritdoc/>
    public SpendSummary GetSummary()
    {
        return default;
    }
}