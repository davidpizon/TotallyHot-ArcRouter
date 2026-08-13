using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// The CodeRouterBench corpus's freshness relative to the published Hugging Face dataset
/// (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 3).
/// </summary>
public enum BenchmarkDataState
{
    /// <summary>Every file in <see cref="BenchmarkFileSpec.All"/> has a ledger row matching its published checksum.</summary>
    Current,

    /// <summary>At least one file has never synced, or its ledger row no longer matches the published checksum.</summary>
    Update,

    /// <summary>The checksum probe could not reach Hugging Face; freshness is unknown.</summary>
    CheckFailed,
}

/// <summary>The corpus's freshness state, as last computed by <see cref="BenchmarkDataStatusService.RecheckAsync"/>.</summary>
/// <param name="State">The computed state.</param>
/// <param name="Reason">The probe failure reason, when <paramref name="State"/> is <see cref="BenchmarkDataState.CheckFailed"/>; otherwise <see langword="null"/>.</param>
/// <param name="CheckedAtUtc">When this status was computed.</param>
public sealed record BenchmarkDataStatus(BenchmarkDataState State, string? Reason, DateTimeOffset CheckedAtUtc);

/// <summary>
/// Caches the CodeRouterBench corpus's <see cref="BenchmarkDataState"/> in memory, computed by comparing
/// a fresh <see cref="BenchmarkChecksumProbe"/> result against <see cref="BenchmarkFileLedger"/>
/// (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 3). Recomputed at startup by
/// <c>StartupHealthCheckHostedService</c> and on demand by Phase 4's "Recheck" action; read by the
/// Governance panel.
/// </summary>
public sealed class BenchmarkDataStatusService
{
    private readonly BenchmarkChecksumProbe _probe;
    private readonly BenchmarkFileLedger _ledger;
    private readonly BenchmarkSyncOptions _options;
    private readonly ILogger<BenchmarkDataStatusService> _logger;
    private readonly object _lock = new();
    private BenchmarkDataStatus? _current;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataStatusService"/> class.</summary>
    /// <param name="probe">Fetches the published checksums the ledger is compared against.</param>
    /// <param name="ledger">The per-file sync ledger read to compute the state.</param>
    /// <param name="options">The dataset ref the probe targets.</param>
    /// <param name="logger">The logger.</param>
    public BenchmarkDataStatusService(
        BenchmarkChecksumProbe probe,
        BenchmarkFileLedger ledger,
        IOptions<BenchmarkSyncOptions> options,
        ILogger<BenchmarkDataStatusService> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _probe = probe;
        _ledger = ledger;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Gets the last computed status, or <see langword="null"/> if <see cref="RecheckAsync"/> has never run.</summary>
    public BenchmarkDataStatus? Current
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>
    /// Probes Hugging Face for the currently published checksums, compares them against
    /// <see cref="BenchmarkFileLedger"/>, computes the resulting <see cref="BenchmarkDataState"/>, caches
    /// it, and returns it. A probe failure - an unreachable host or a malformed tree response - is caught
    /// here and reported as <see cref="BenchmarkDataState.CheckFailed"/> rather than thrown or silently
    /// treated as <see cref="BenchmarkDataState.Current"/> - the "fail open at startup" ground rule in
    /// docs/router/coderouterbench-sqlite-migration-plan.md. Caller cancellation is not a probe failure:
    /// it propagates instead of being recorded as <see cref="BenchmarkDataState.CheckFailed"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<BenchmarkDataStatus> RecheckAsync(CancellationToken cancellationToken)
    {
        BenchmarkDataStatus status;
        try
        {
            var probeResult = await _probe.FetchAsync(_options.DatasetRef, cancellationToken).ConfigureAwait(false);
            var ledgerEntries = _ledger.GetAll().ToDictionary(entry => entry.FileName, StringComparer.Ordinal);

            var isCurrent = BenchmarkFileSpec.All.All(spec =>
                ledgerEntries.TryGetValue(spec.FileName, out var entry) &&
                probeResult.Files.TryGetValue(spec.FileName, out var published) &&
                string.Equals(entry.PublishedOid, published.PublishedOid, StringComparison.OrdinalIgnoreCase));

            status = new BenchmarkDataStatus(
                isCurrent ? BenchmarkDataState.Current : BenchmarkDataState.Update,
                Reason: null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (
            (ex is HttpRequestException or JsonException or NotSupportedException) ||
            (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "CodeRouterBench checksum probe could not reach Hugging Face; corpus freshness is unknown.");
            status = new BenchmarkDataStatus(BenchmarkDataState.CheckFailed, ex.Message, DateTimeOffset.UtcNow);
        }

        lock (_lock)
        {
            _current = status;
        }

        return status;
    }
}
