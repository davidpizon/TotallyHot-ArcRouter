using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Downloads, verifies, parses, and imports every file in <see cref="BenchmarkFileSpec.All"/>
/// (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 2). Each file is handled independently:
/// a failure at any step - download, checksum mismatch, row-count mismatch, or a parse error - aborts
/// that file only. Its table rows and ledger entry stay exactly as they were before the sync started
/// (the "fail loudly on import" ground rule), and the failure is reported in the returned outcome rather
/// than thrown.
/// </summary>
public sealed class BenchmarkSyncService
{
    private const string DownloadUrlTemplate = "https://huggingface.co/datasets/{0}/resolve/{1}/{2}";

    private readonly HttpClient _httpClient;
    private readonly BenchmarkChecksumProbe _probe;
    private readonly BenchmarkDatabase _database;
    private readonly BenchmarkFileLedger _ledger;
    private readonly ILogger<BenchmarkSyncService> _logger;
    private readonly IReadOnlyList<BenchmarkFileSpec> _fileSpecs;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkSyncService"/> class.</summary>
    /// <param name="httpClient">The client used to download source files from Hugging Face.</param>
    /// <param name="probe">Fetches the published checksums each downloaded file is verified against.</param>
    /// <param name="database">The corpus database files are imported into.</param>
    /// <param name="ledger">The per-file sync ledger, updated once a file's import commits.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fileSpecs">
    /// The files to sync. Defaults to <see cref="BenchmarkFileSpec.All"/>; overridable so tests can drive
    /// the full download/verify/import/ledger pipeline against small fixture bytes without needing to
    /// satisfy the production manifest's five- and six-figure row-count assertions.
    /// </param>
    public BenchmarkSyncService(
        HttpClient httpClient,
        BenchmarkChecksumProbe probe,
        BenchmarkDatabase database,
        BenchmarkFileLedger ledger,
        ILogger<BenchmarkSyncService> logger,
        IReadOnlyList<BenchmarkFileSpec>? fileSpecs = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _probe = probe;
        _database = database;
        _ledger = ledger;
        _logger = logger;
        _fileSpecs = fileSpecs ?? BenchmarkFileSpec.All;
    }

    /// <summary>
    /// Syncs every file in <see cref="BenchmarkFileSpec.All"/> from <paramref name="datasetRef"/>,
    /// reporting per-file progress through <paramref name="progress"/> when supplied.
    /// </summary>
    /// <param name="datasetRef">The dataset ref (branch, tag, or commit) to sync from, e.g. <c>"main"</c>.</param>
    /// <param name="progress">An optional progress reporter for streaming per-file status.</param>
    /// <param name="cancellationToken">A token to cancel the sync.</param>
    /// <exception cref="HttpRequestException">The checksum probe itself failed; no file was synced.</exception>
    public async Task<BenchmarkSyncResult> SyncAsync(
        string datasetRef,
        IProgress<BenchmarkSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRef);

        _database.EnsureCreated();

        var probeResult = await _probe.FetchAsync(datasetRef, cancellationToken).ConfigureAwait(false);

        List<BenchmarkFileSyncOutcome> outcomes = [];
        foreach (var spec in _fileSpecs)
        {
            outcomes.Add(await SyncFileAsync(spec, datasetRef, probeResult, progress, cancellationToken).ConfigureAwait(false));
        }

        return new BenchmarkSyncResult(probeResult.RepoCommit, outcomes);
    }

    private async Task<BenchmarkFileSyncOutcome> SyncFileAsync(
        BenchmarkFileSpec spec,
        string datasetRef,
        BenchmarkChecksumProbeResult probeResult,
        IProgress<BenchmarkSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!probeResult.Files.TryGetValue(spec.FileName, out var published))
        {
            var missingMessage = $"'{spec.FileName}' was not present in the published dataset tree.";
            _logger.LogWarning("CodeRouterBench sync skipped {FileName}: {Reason}", spec.FileName, missingMessage);
            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Failed));
            return new BenchmarkFileSyncOutcome(spec.FileName, Succeeded: false, RowCount: null, missingMessage);
        }

        try
        {
            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Downloading));
            var url = string.Format(
                CultureInfo.InvariantCulture,
                DownloadUrlTemplate,
                BenchmarkChecksumProbe.DatasetRepo,
                Uri.EscapeDataString(datasetRef),
                spec.FileName);
            var bytes = await _httpClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Downloading, bytes.LongLength));

            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Verifying));
            var actualOid = GitBlobHash.Compute(bytes);
            if (!string.Equals(actualOid, published.PublishedOid, StringComparison.OrdinalIgnoreCase))
            {
                var mismatchMessage =
                    $"Checksum mismatch for '{spec.FileName}': expected {published.PublishedOid}, computed {actualOid}.";
                _logger.LogError("CodeRouterBench sync rejected {FileName}: {Reason}", spec.FileName, mismatchMessage);
                progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Failed));
                return new BenchmarkFileSyncOutcome(spec.FileName, Succeeded: false, RowCount: null, mismatchMessage);
            }

            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Importing));
            var rowCount = ImportAndRecord(spec, bytes, actualOid, probeResult.RepoCommit);

            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Completed, RowsImported: rowCount));
            _logger.LogInformation(
                "CodeRouterBench sync imported {RowCount} row(s) from {FileName} at commit {RepoCommit}.",
                rowCount,
                spec.FileName,
                probeResult.RepoCommit);
            return new BenchmarkFileSyncOutcome(spec.FileName, Succeeded: true, rowCount, ErrorMessage: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or TaskCanceledException or SqliteException)
        {
            _logger.LogError(ex, "CodeRouterBench sync failed for {FileName}.", spec.FileName);
            progress?.Report(new BenchmarkSyncProgress(spec.FileName, BenchmarkSyncStage.Failed));
            return new BenchmarkFileSyncOutcome(spec.FileName, Succeeded: false, RowCount: null, ex.Message);
        }
    }

    /// <summary>
    /// Parses <paramref name="bytes"/> with the importer <paramref name="spec"/>'s kind selects, asserts
    /// its row count, and - only if both succeed - writes the ledger row, all on one transaction. A
    /// row-count mismatch throws before the transaction commits, so the whole import (table rows and
    /// ledger row alike) rolls back and the prior state is untouched.
    /// </summary>
    /// <exception cref="FormatException">Parsing failed, or the imported row count did not match <see cref="BenchmarkFileSpec.ExpectedRowCount"/>.</exception>
    private int ImportAndRecord(BenchmarkFileSpec spec, byte[] bytes, string actualOid, string repoCommit)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8);
        var rowCount = spec.Kind switch
        {
            BenchmarkFileKind.IdResultsCsv => BenchmarkIdResultsCsvImporter.Import(reader, spec.Split!, connection, transaction),
            BenchmarkFileKind.OodResultsCsv => BenchmarkOodResultsCsvImporter.Import(reader, connection, transaction),
            BenchmarkFileKind.IdTasksJsonl => BenchmarkIdTasksJsonlImporter.Import(reader, spec.Split!, connection, transaction),
            BenchmarkFileKind.OodTasksJsonl => BenchmarkOodTasksJsonlImporter.Import(reader, connection, transaction),
            BenchmarkFileKind.ModelsJson => BenchmarkModelsJsonImporter.Import(reader.ReadToEnd(), connection, transaction),
            BenchmarkFileKind.SummaryJson => BenchmarkSummaryJsonImporter.Import(reader.ReadToEnd(), connection, transaction),
            _ => throw new NotSupportedException($"Unsupported benchmark file kind '{spec.Kind}'."),
        };

        if (spec.ExpectedRowCount is int expected && rowCount != expected)
        {
            throw new FormatException($"'{spec.FileName}' has {rowCount} data row(s) but expected {expected}.");
        }

        _ledger.Upsert(
            new BenchmarkFileLedgerEntry(spec.FileName, actualOid, bytes.LongLength, rowCount, repoCommit, DateTimeOffset.UtcNow),
            connection,
            transaction);

        transaction.Commit();
        return rowCount;
    }
}
