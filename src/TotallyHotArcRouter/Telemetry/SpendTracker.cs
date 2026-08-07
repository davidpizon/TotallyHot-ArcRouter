using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Tracks a personal-scale running spend total across the process lifetime - not a team/org billing
/// system, just enough for a single developer to see what they've spent. Every recorded request's
/// running total is logged (visible as terminal output through the configured Serilog Console sink)
/// and appended to a local JSON Lines file, giving both a terminal view and a simple local file log.
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
    Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens, decimal? estimatedCostUsd, CancellationToken cancellationToken = default);

    /// <summary>Gets the current running total without recording a new request.</summary>
    SpendSummary GetSummary();
}

/// <summary>Running spend/usage totals accumulated since process start.</summary>
public readonly record struct SpendSummary(int RequestCount, long TotalPromptTokens, long TotalCompletionTokens, decimal TotalCostUsd);

/// <inheritdoc cref="ISpendTracker" />
public sealed class SpendTracker : ISpendTracker, IDisposable
{
    private readonly ILogger<SpendTracker> _logger;
    private readonly SpendTrackingOptions _options;
    private readonly string _logFilePath;

    // Guards the four running totals below (fast, synchronous - never held across an await).
    private readonly object _totalsLock = new();

    // Serializes file appends only, so two requests completing at nearly the same time can't
    // interleave their JSON Lines writes into a corrupted line.
    private readonly SemaphoreSlim _fileMutex = new(1, 1);

    private int _requestCount;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;
    private decimal _totalCostUsd;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpendTracker"/> class.
    /// </summary>
    public SpendTracker(ILogger<SpendTracker> logger, IOptions<SpendTrackingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _options = options.Value;

        _logFilePath = Path.IsPathRooted(_options.LogPath)
            ? _options.LogPath
            : Path.Combine(AppContext.BaseDirectory, _options.LogPath);
    }

    /// <inheritdoc />
    public async Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens, decimal? estimatedCostUsd, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return GetSummary();
        }

        SpendSummary summary;
        lock (_totalsLock)
        {
            _requestCount++;
            _totalPromptTokens += promptTokens ?? 0;
            _totalCompletionTokens += completionTokens ?? 0;
            _totalCostUsd += estimatedCostUsd ?? 0m;
            summary = new SpendSummary(_requestCount, _totalPromptTokens, _totalCompletionTokens, _totalCostUsd);
        }

        // Logged as two separate calls (rather than pre-formatting the decimals to strings, e.g. via
        // ToString("F6")) so CostUsd/RunningTotalUsd stay structured decimal properties for any
        // sink that captures them, not culture-dependent, query-unfriendly strings - the ":F6" format
        // specifier only affects the rendered text, not the captured property value.
        if (estimatedCostUsd is decimal cost)
        {
            _logger.LogInformation(
                "[SPEND] model={Model} cost=${CostUsd:F6} runningTotal=${RunningTotalUsd:F6} requests={RequestCount}",
                model,
                cost,
                summary.TotalCostUsd,
                summary.RequestCount);
        }
        else
        {
            _logger.LogInformation(
                "[SPEND] model={Model} cost=unknown runningTotal=${RunningTotalUsd:F6} requests={RequestCount}",
                model,
                summary.TotalCostUsd,
                summary.RequestCount);
        }

        // Best-effort, like every other telemetry sink in this codebase (see ProxyMiddleware's
        // PublishTelemetryAsync try/catch) - a disk/permission failure here must never affect request
        // handling, so failures are logged at Debug and otherwise swallowed.
        try
        {
            await AppendLogLineAsync(model, promptTokens, completionTokens, estimatedCostUsd, summary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to append spend log entry to {LogFilePath}.", _logFilePath);
        }

        return summary;
    }

    /// <inheritdoc />
    public SpendSummary GetSummary()
    {
        lock (_totalsLock)
        {
            return new SpendSummary(_requestCount, _totalPromptTokens, _totalCompletionTokens, _totalCostUsd);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _fileMutex.Dispose();

    /// <summary>
    /// Serializes a <see cref="SpendLogEntry"/> for the given request and appends it as a line to the
    /// log file, creating the containing directory if needed and serializing access via <see cref="_fileMutex"/>.
    /// </summary>
    private async Task AppendLogLineAsync(string model, int? promptTokens, int? completionTokens, decimal? estimatedCostUsd, SpendSummary summary, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var entry = new SpendLogEntry(DateTimeOffset.UtcNow, model, promptTokens, completionTokens, estimatedCostUsd, summary.TotalCostUsd, summary.RequestCount);
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;

        await _fileMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileMutex.Release();
        }
    }

    /// <summary>
    /// A single line of the JSON-lines spend log, recording one request's usage and cost alongside the
    /// running totals at the time it was recorded.
    /// </summary>
    /// <param name="TimestampUtc">The UTC time the request was recorded.</param>
    /// <param name="Model">The model used to serve the request.</param>
    /// <param name="PromptTokens">The number of prompt tokens consumed, if known.</param>
    /// <param name="CompletionTokens">The number of completion tokens produced, if known.</param>
    /// <param name="CostUsd">The estimated cost in USD for this request, if known.</param>
    /// <param name="RunningTotalCostUsd">The cumulative cost in USD across all recorded requests, including this one.</param>
    /// <param name="RequestCount">The cumulative number of requests recorded, including this one.</param>
    private sealed record SpendLogEntry(
        DateTimeOffset TimestampUtc,
        string Model,
        int? PromptTokens,
        int? CompletionTokens,
        decimal? CostUsd,
        decimal RunningTotalCostUsd,
        int RequestCount);
}

/// <summary>
/// Safe no-op default for callers that don't opt into spend tracking (e.g. tests constructing
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/> directly without a DI container) - mirrors the
/// "fresh, private, harmless default" pattern every other optional <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>
/// dependency already follows.
/// </summary>
public sealed class NullSpendTracker : ISpendTracker
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static readonly NullSpendTracker Instance = new();

    private NullSpendTracker()
    {
    }

    /// <inheritdoc />
    public Task<SpendSummary> RecordAsync(string model, int? promptTokens, int? completionTokens, decimal? estimatedCostUsd, CancellationToken cancellationToken = default) =>
        Task.FromResult(default(SpendSummary));

    /// <inheritdoc />
    public SpendSummary GetSummary() => default;
}

