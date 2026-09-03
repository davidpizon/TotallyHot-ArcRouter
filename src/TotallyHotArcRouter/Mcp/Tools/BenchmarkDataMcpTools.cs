using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Mcp.Tools;

/// <summary>
/// MCP tools for the CodeRouterBench corpus's sync state (docs/router/coderouterbench-sqlite-migration-plan.md
/// Phase 6): report freshness and trigger a sync. Alongside <see cref="PriceSourceMcpTools"/> and
/// <see cref="TelemetryMcpTools"/>. Carries no licensing constraint the way price data does (D5) - the
/// corpus is public benchmark data - so both tools return full detail.
/// </summary>
[McpServerToolType]
public sealed class BenchmarkDataMcpTools
{
    private readonly BenchmarkSyncOptions _options;
    private readonly BenchmarkDataStatusService _statusService;
    private readonly BenchmarkSyncService _syncService;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataMcpTools"/> class.</summary>
    public BenchmarkDataMcpTools(
        BenchmarkDataStatusService statusService,
        BenchmarkSyncService syncService,
        IOptions<BenchmarkSyncOptions> options)
    {
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(syncService);
        ArgumentNullException.ThrowIfNull(options);

        _statusService = statusService;
        _syncService = syncService;
        _options = options.Value;
    }

    /// <summary>
    /// Reports the corpus's cached freshness state, computing one first if none has ever run (e.g. this
    /// tool is called before <c>StartupHealthCheckHostedService</c>'s own startup probe completes).
    /// </summary>
    [McpServerTool(Name = "get_benchmark_data_status")]
    [Description(
        "Reports the CodeRouterBench corpus's sync freshness relative to the published Hugging Face dataset: Current, Update, or CheckFailed.")]
    public async Task<BenchmarkDataStatus> GetBenchmarkDataStatusAsync(CancellationToken cancellationToken)
    {
        return _statusService.Current ?? await _statusService.RecheckAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one corpus sync now, downloading, verifying, and importing every file.</summary>
    [McpServerTool(Name = "sync_benchmark_data")]
    [Description(
        "Downloads, verifies, and imports the CodeRouterBench corpus from Hugging Face now, replacing any existing rows for each file that succeeds. A per-file failure (checksum or row-count mismatch) leaves that file's prior data untouched and is reported in the result rather than thrown.")]
    public Task<BenchmarkSyncResult> SyncBenchmarkDataAsync(CancellationToken cancellationToken)
    {
        return _syncService.SyncAsync(datasetRef: _options.DatasetRef, null, cancellationToken: cancellationToken);
    }
}