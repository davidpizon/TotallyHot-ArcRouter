using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Checksums;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Downloads, verifies, parses, and imports every stale file in <see cref="BenchmarkFileSpec.All"/> - a
/// file whose ledger checksum already matches the published one is skipped entirely, matching
/// <see cref="BenchmarkDataStatusService.RecheckAsync"/>'s freshness comparison
/// (docs/router/coderouterbench-sqlite-migration-plan.md, Phase 2). Each downloaded file is handled
/// independently: a failure at any step - download, checksum mismatch, row-count mismatch, or a parse
/// error - aborts that file only. Its table rows and ledger entry stay exactly as they were before the
/// sync started (the "fail loudly on import" ground rule), and the failure is reported in the returned
/// outcome rather than thrown.
/// Every file is streamed to a per-run temporary directory rather than buffered in memory, verified
/// there, and only promoted (moved) into its final on-disk name once its checksum matches - the same
/// stream/verify/promote shape <c>LlmRouterModelSyncService</c> uses for the ONNX voter's files. The
/// temporary directory is deleted once the run completes, successfully or not.
/// </summary>
public sealed class BenchmarkSyncService
{
    private const string DownloadUrlTemplate = "https://huggingface.co/datasets/{0}/resolve/{1}/{2}";

    // Reported no more often than this many bytes, or this often, whichever comes first - streaming
    // every buffer-sized read as its own progress event would flood the gRPC stream for a
    // multi-thousand-row CSV.
    private const long ProgressReportIntervalBytes = 256 * 1024;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);
    private readonly BenchmarkDatabase _database;
    private readonly IReadOnlyList<BenchmarkFileSpec> _fileSpecs;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BenchmarkFileLedger _ledger;
    private readonly ILogger<BenchmarkSyncService> _logger;
    private readonly BenchmarkChecksumProbe _probe;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkSyncService"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Used to create a fresh <see cref="BenchmarkChecksumProbe.HttpClientName"/> client per file download,
    /// mirroring <c>OnnxEmbeddingClient</c>'s pattern rather than capturing one factory-created client for
    /// the singleton's lifetime (which would opt this service out of the factory's handler rotation).
    /// </param>
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
        IHttpClientFactory httpClientFactory,
        BenchmarkChecksumProbe probe,
        BenchmarkDatabase database,
        BenchmarkFileLedger ledger,
        ILogger<BenchmarkSyncService> logger,
        IReadOnlyList<BenchmarkFileSpec>? fileSpecs = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _probe = probe;
        _database = database;
        _ledger = ledger;
        _logger = logger;
        _fileSpecs = fileSpecs ?? BenchmarkFileSpec.All;
    }

    /// <summary>
    /// Syncs every stale file in <see cref="BenchmarkFileSpec.All"/> from <paramref name="datasetRef"/> -
    /// one whose ledger checksum no longer matches the just-fetched published tree, or that has never
    /// synced. A file that already matches is skipped without a network request and reported as a
    /// succeeded, <see cref="BenchmarkFileSyncOutcome.Skipped"/> outcome.
    /// </summary>
    /// <param name="datasetRef">The dataset ref (branch, tag, or commit) to sync from, e.g. <c>"main"</c>.</param>
    /// <param name="progress">An optional progress reporter for streaming per-file status.</param>
    /// <param name="cancellationToken">A token to cancel the sync.</param>
    /// <param name="planProgress">
    /// An optional reporter for the up-front plan (the stale files and their combined size), invoked
    /// exactly once before any file's progress is reported, so a cumulative progress display has a
    /// stable denominator from the first byte.
    /// </param>
    /// <exception cref="HttpRequestException">The checksum probe itself failed; no file was synced.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// The checksum probe's response body was not valid JSON; no file was
    /// synced.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The checksum probe's response content type was not supported for JSON
    /// deserialization; no file was synced.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled while a file was downloading, importing, or being probed.
    /// Unlike a per-file network or parse failure, caller cancellation aborts the whole sync rather than being
    /// recorded as a failed <see cref="BenchmarkFileSyncOutcome"/>.
    /// </exception>
    public async Task<BenchmarkSyncResult> SyncAsync(
        string datasetRef,
        IProgress<BenchmarkSyncProgress>? progress,
        CancellationToken cancellationToken,
        IProgress<BenchmarkSyncPlan>? planProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRef);

        _database.EnsureCreated();

        var probeResult = await _probe.FetchAsync(datasetRef: datasetRef, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var ledgerEntries = _ledger.GetAll()
            .ToDictionary(keySelector: entry => entry.FileName, comparer: StringComparer.Ordinal);

        List<BenchmarkFileSpec> staleSpecs = [];
        List<BenchmarkFileSyncOutcome> outcomes = [];
        foreach (var spec in _fileSpecs)
            if (ledgerEntries.TryGetValue(key: spec.FileName, value: out var ledgerEntry) &&
                probeResult.Files.TryGetValue(key: spec.FileName, value: out var publishedForSkipCheck) &&
                string.Equals(a: ledgerEntry.PublishedOid, b: publishedForSkipCheck.PublishedOid,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    message: "CodeRouterBench sync skipping {FileName}: already current at checksum {Oid}.",
                    spec.FileName,
                    ledgerEntry.PublishedOid);
                outcomes.Add(new BenchmarkFileSyncOutcome(
                    FileName: spec.FileName, true, RowCount: ledgerEntry.RowCount, null, true));
            }
            else
            {
                staleSpecs.Add(spec);
            }

        var planFiles = staleSpecs
            .Select(spec => probeResult.Files.TryGetValue(key: spec.FileName, value: out var published)
                ? new BenchmarkSyncPlanFile(FileName: spec.FileName, SizeBytes: published.Size)
                : new BenchmarkSyncPlanFile(FileName: spec.FileName, 0))
            .ToList();
        planProgress?.Report(new BenchmarkSyncPlan(Files: planFiles, TotalBytes: planFiles.Sum(f => f.SizeBytes)));

        var tempDirectory = Directory.CreateTempSubdirectory("arcrouter-bench-");
        try
        {
            foreach (var spec in staleSpecs)
                outcomes.Add(await SyncFileAsync(spec: spec, datasetRef: datasetRef, probeResult: probeResult,
                        tempDirectory: tempDirectory.FullName, progress: progress, cancellationToken: cancellationToken)
                    .ConfigureAwait(false));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only, mirroring LlmRouterModelSyncService's SafeDelete: a file the
                // OS hasn't released yet (e.g. a lingering antivirus scan) must not fail the sync.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Preserve BenchmarkFileSpec.All's order in the result regardless of which files were skipped
        // vs. synced, since consumers (the gRPC service's BuildFiles-independent outcome loop) assume
        // one outcome per spec in manifest order.
        var orderedOutcomes = _fileSpecs
            .Select(spec => outcomes.First(outcome => outcome.FileName == spec.FileName))
            .ToList();

        return new BenchmarkSyncResult(RepoCommit: probeResult.RepoCommit, Files: orderedOutcomes);
    }

    /// <summary>
    /// Downloads, verifies, and imports one file, reporting each stage through <paramref name="progress"/>.
    /// A missing published entry, a checksum mismatch, or an exception caught below all end this file's
    /// sync with a failed outcome rather than propagating - the per-file isolation this class's summary
    /// describes.
    /// </summary>
    /// <param name="spec">The file to sync.</param>
    /// <param name="datasetRef">The dataset ref the file is downloaded from.</param>
    /// <param name="probeResult">The already-fetched published checksums, used to verify the download.</param>
    /// <param name="tempDirectory">The run's temporary directory the file is streamed into before verification.</param>
    /// <param name="progress">An optional progress reporter for streaming this file's status.</param>
    /// <param name="cancellationToken">A token to cancel the download or import.</param>
    /// <returns>The file's outcome: succeeded with a row count, or failed with a reason.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    private async Task<BenchmarkFileSyncOutcome> SyncFileAsync(
        BenchmarkFileSpec spec,
        string datasetRef,
        BenchmarkChecksumProbeResult probeResult,
        string tempDirectory,
        IProgress<BenchmarkSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!probeResult.Files.TryGetValue(key: spec.FileName, value: out var published))
        {
            var missingMessage = $"'{spec.FileName}' was not present in the published dataset tree.";
            _logger.LogWarning(message: "CodeRouterBench sync failed for {FileName}: {Reason}", spec.FileName,
                missingMessage);
            progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Failed));
            return new BenchmarkFileSyncOutcome(FileName: spec.FileName, false, null, ErrorMessage: missingMessage);
        }

        // A GUID-suffixed temp name so two files with the same base name (there are none today, but
        // this mirrors LlmRouterModelSyncService's convention) never collide within the run's directory.
        var partPath = Path.Combine(path1: tempDirectory, path2: $"{spec.FileName}.{Guid.NewGuid():N}.part");
        try
        {
            progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Downloading,
                TotalBytes: published.Size));
            var url = string.Format(
                provider: CultureInfo.InvariantCulture,
                format: DownloadUrlTemplate,
                arg0: BenchmarkChecksumProbe.DatasetRepo,
                arg1: Uri.EscapeDataString(datasetRef),
                arg2: spec.FileName);
            using var httpClient = _httpClientFactory.CreateClient(BenchmarkChecksumProbe.HttpClientName);

            long downloadedLength;
            using (var response = await httpClient
                       .GetAsync(requestUri: url, completionOption: HttpCompletionOption.ResponseHeadersRead,
                           cancellationToken: cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var source =
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = File.Create(partPath);

                var buffer = new byte[81920];
                long totalRead = 0;
                var lastReportedBytes = 0L;
                var lastReportedAt = DateTime.UtcNow;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer: buffer, cancellationToken: cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer: buffer.AsMemory(0, length: bytesRead),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    totalRead += bytesRead;

                    var now = DateTime.UtcNow;
                    if (totalRead - lastReportedBytes >= ProgressReportIntervalBytes ||
                        now - lastReportedAt >= ProgressReportInterval)
                    {
                        progress?.Report(new BenchmarkSyncProgress(
                            FileName: spec.FileName, Stage: BenchmarkSyncStage.Downloading, BytesTransferred: totalRead,
                            TotalBytes: published.Size));
                        lastReportedBytes = totalRead;
                        lastReportedAt = now;
                    }
                }

                downloadedLength = totalRead;
            }

            progress?.Report(new BenchmarkSyncProgress(
                FileName: spec.FileName, Stage: BenchmarkSyncStage.Downloading, BytesTransferred: downloadedLength,
                TotalBytes: published.Size));

            progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Verifying,
                TotalBytes: published.Size));
            string actualOid;
            await using (var hashStream = File.OpenRead(partPath))
            {
                actualOid = PublishedChecksumHasher.Compute(content: hashStream, length: downloadedLength,
                    algorithm: published.Algorithm, cancellationToken: cancellationToken);
            }

            if (!string.Equals(a: actualOid, b: published.PublishedOid,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                var mismatchMessage =
                    $"Checksum mismatch for '{spec.FileName}': expected {published.PublishedOid}, computed {actualOid}.";
                _logger.LogError(message: "CodeRouterBench sync rejected {FileName}: {Reason}", spec.FileName,
                    mismatchMessage);
                progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Failed,
                    TotalBytes: published.Size));
                SafeDelete(partPath);
                return new BenchmarkFileSyncOutcome(FileName: spec.FileName, false, null,
                    ErrorMessage: mismatchMessage);
            }

            // Promote only once the checksum has verified - the "copied over once the checksum matches"
            // step - so a mismatched download never leaves a same-named file behind to be confused with
            // a verified one.
            var verifiedPath = Path.Combine(path1: tempDirectory, path2: spec.FileName);
            File.Move(sourceFileName: partPath, destFileName: verifiedPath, true);

            progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Importing,
                TotalBytes: published.Size));
            var rowCount = ImportAndRecord(spec: spec, filePath: verifiedPath, actualOid: actualOid,
                repoCommit: probeResult.RepoCommit);

            progress?.Report(new BenchmarkSyncProgress(
                FileName: spec.FileName, Stage: BenchmarkSyncStage.Completed, RowsImported: rowCount,
                TotalBytes: published.Size));
            _logger.LogInformation(
                message: "CodeRouterBench sync imported {RowCount} row(s) from {FileName} at commit {RepoCommit}.",
                rowCount,
                spec.FileName,
                probeResult.RepoCommit);
            return new BenchmarkFileSyncOutcome(FileName: spec.FileName, true, RowCount: rowCount, null);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or FormatException or SqliteException or JsonException
                or NotSupportedException or ArgumentException ||
            (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError(exception: ex, message: "CodeRouterBench sync failed for {FileName}.", spec.FileName);
            progress?.Report(new BenchmarkSyncProgress(FileName: spec.FileName, Stage: BenchmarkSyncStage.Failed,
                TotalBytes: published.Size));
            SafeDelete(partPath);
            return new BenchmarkFileSyncOutcome(FileName: spec.FileName, false, null, ErrorMessage: ex.Message);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(partPath);
            throw;
        }
    }

    /// <summary>
    /// Parses the file at <paramref name="filePath"/> with the importer <paramref name="spec"/>'s kind
    /// selects, asserts its row count, and - only if both succeed - writes the ledger row, all on one
    /// transaction. A row-count mismatch throws before the transaction commits, so the whole import
    /// (table rows and ledger row alike) rolls back and the prior state is untouched.
    /// </summary>
    /// <exception cref="FormatException">
    /// The imported row count did not match <see cref="BenchmarkFileSpec.ExpectedRowCount"/>
    /// .
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">A JSON or JSONL file's content was not valid JSON.</exception>
    /// <exception cref="NotSupportedException"><paramref name="spec"/>'s <see cref="BenchmarkFileSpec.Kind"/> has no importer.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="spec"/>'s <see cref="BenchmarkFileSpec.Split"/> is required by its
    /// importer but null or blank.
    /// </exception>
    private int ImportAndRecord(BenchmarkFileSpec spec, string filePath, string actualOid, string repoCommit)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var fileStream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream: fileStream, encoding: Encoding.UTF8);
        var rowCount = spec.Kind switch
        {
            BenchmarkFileKind.IdResultsCsv => BenchmarkIdResultsCsvImporter.Import(reader: reader, split: spec.Split!,
                connection: connection, transaction: transaction),
            BenchmarkFileKind.OodResultsCsv => BenchmarkOodResultsCsvImporter.Import(reader: reader,
                connection: connection, transaction: transaction),
            BenchmarkFileKind.IdTasksJsonl => BenchmarkIdTasksJsonlImporter.Import(reader: reader, split: spec.Split!,
                connection: connection, transaction: transaction),
            BenchmarkFileKind.OodTasksJsonl => BenchmarkOodTasksJsonlImporter.Import(reader: reader,
                connection: connection, transaction: transaction),
            BenchmarkFileKind.ModelsJson => BenchmarkModelsJsonImporter.Import(json: reader.ReadToEnd(),
                connection: connection, transaction: transaction),
            BenchmarkFileKind.SummaryJson => BenchmarkSummaryJsonImporter.Import(json: reader.ReadToEnd(),
                connection: connection, transaction: transaction),
            _ => throw new NotSupportedException($"Unsupported benchmark file kind '{spec.Kind}'.")
        };

        var fileSizeBytes = new FileInfo(filePath).Length;
        if (spec.ExpectedRowCount is int expected && rowCount != expected)
            throw new FormatException($"'{spec.FileName}' has {rowCount} data row(s) but expected {expected}.");

        _ledger.Upsert(
            entry: new BenchmarkFileLedgerEntry(FileName: spec.FileName, PublishedOid: actualOid,
                SizeBytes: fileSizeBytes, RowCount: rowCount, RepoCommit: repoCommit,
                SyncedAtUtc: DateTimeOffset.UtcNow),
            connection: connection,
            transaction: transaction);

        transaction.Commit();
        return rowCount;
    }

    /// <summary>Deletes <paramref name="path"/> if it exists, swallowing failures - best-effort cleanup only.</summary>
    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}