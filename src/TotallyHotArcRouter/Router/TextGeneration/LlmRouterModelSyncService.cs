using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// Downloads and checksum-verifies every required file in <see cref="LlmRouterModelFiles.All"/> for the
/// llm_router voter's currently active model (docs/router - Governance → Benchmark Data panel's "Local
/// Voter Model" section). Unlike an earlier version of this service, a file is never installed without a
/// published checksum to verify it against - a model source the checksum probe cannot reach or parse
/// fails every required file's sync rather than trusting unverified bytes. Each file is otherwise handled
/// independently: a download failure or checksum mismatch aborts that file only, reported in the returned
/// outcome rather than thrown - the same per-file-isolation convention <see cref="BenchmarkSyncService"/>
/// uses for the CodeRouterBench corpus.
/// </summary>
public sealed class LlmRouterModelSyncService
{
    // Reported no more often than this many bytes, or this often, whichever comes first - streaming
    // every buffer-sized read as its own progress event would flood the gRPC stream for a
    // multi-hundred-megabyte model.onnx.data.
    private const long ProgressReportIntervalBytes = 256 * 1024;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmRouterModelChecksumProbe _probe;
    private readonly ILlmRouterModelOverrideStore _overrideStore;
    private readonly ILogger<LlmRouterModelSyncService> _logger;

    private volatile LlmRouterModelVerificationSnapshot _lastVerification =
        new(BaseUrl: string.Empty, Files: new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool>(StringComparer.Ordinal)));

    /// <summary>Initializes a new instance of the <see cref="LlmRouterModelSyncService"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Used to create a fresh <see cref="LlmRouterModelChecksumProbe.HttpClientName"/> client per file
    /// download, mirroring <see cref="BenchmarkSyncService"/>'s pattern.
    /// </param>
    /// <param name="probe">Attempts to fetch published checksums each downloaded file is verified against, when the model's URL supports it.</param>
    /// <param name="overrideStore">The active model whose files this service downloads.</param>
    /// <param name="logger">The logger.</param>
    public LlmRouterModelSyncService(
        IHttpClientFactory httpClientFactory,
        LlmRouterModelChecksumProbe probe,
        ILlmRouterModelOverrideStore overrideStore,
        ILogger<LlmRouterModelSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _probe = probe;
        _overrideStore = overrideStore;
        _logger = logger;
    }

    /// <summary>
    /// Whether each file was checksum-verified during the most recent <see cref="SyncAsync"/> call, keyed
    /// by file name, together with the base URL of the model that sync was for. There is no persisted
    /// ledger for this (unlike <see cref="BenchmarkFileLedger"/>) - this is a best-effort, in-memory
    /// record of the last sync only, reset on restart. The base URL lets
    /// <see cref="LlmRouterModelAdminGrpcService"/> discard this record when the active model has since
    /// switched, instead of misreporting a stale sync's verification state as belonging to the new model.
    /// </summary>
    public LlmRouterModelVerificationSnapshot LastVerification => _lastVerification;

    /// <summary>
    /// Syncs every file in <see cref="LlmRouterModelFiles.All"/> for the model that is active when this
    /// call starts, reporting per-file progress through <paramref name="progress"/> when supplied.
    /// </summary>
    /// <remarks>
    /// The active override is read once, at the start of the call, into a stable local value - not
    /// re-read per file - so a model switch that lands mid-sync cannot cause this call to write some
    /// files into one model's cache directory and the rest into another's.
    /// </remarks>
    /// <param name="progress">An optional progress reporter for streaming per-file status.</param>
    /// <param name="cancellationToken">A token to cancel the sync.</param>
    /// <param name="planProgress">
    /// An optional reporter for the one-time plan - which files are stale and how many bytes the run
    /// will transfer - published once, before any file starts downloading, so a progress bar has a
    /// stable denominator from the first byte.
    /// </param>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled while a file was downloading or being verified.
    /// Unlike a per-file network or checksum failure, caller cancellation aborts the whole sync rather
    /// than being recorded as a failed <see cref="LlmRouterModelFileSyncOutcome"/>.
    /// </exception>
    public async Task<LlmRouterModelSyncResult> SyncAsync(
        IProgress<LlmRouterModelSyncProgress>? progress,
        CancellationToken cancellationToken,
        IProgress<LlmRouterModelSyncPlan>? planProgress = null)
    {
        var activeOverride = _overrideStore.Snapshot.Override;
        var cacheDirectory = activeOverride.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);

        var probeResult = await _probe.TryFetchAsync(activeOverride.BaseUrl, cancellationToken).ConfigureAwait(false);

        var (outcomes, staleFiles, deferredProgress) = await ClassifyAsync(cacheDirectory, probeResult, cancellationToken)
            .ConfigureAwait(false);

        var planFiles = staleFiles
            .Select(file => new LlmRouterModelSyncPlanFile(file.FileName, file.Published.Size))
            .ToList();
        planProgress?.Report(new LlmRouterModelSyncPlan(planFiles, planFiles.Sum(file => file.SizeBytes)));

        // Flushed only now, after the plan: ClassifyAsync itself must stay silent on progress so that no
        // event - a skipped optional file, a failed required file, or a cached file's Verifying/Completed
        // transition - can reach the caller before the plan and leave a progress bar with a 0-byte
        // denominator.
        foreach (var deferredEvent in deferredProgress)
        {
            progress?.Report(deferredEvent);
        }

        foreach (var (fileName, published) in staleFiles)
        {
            outcomes[fileName] = await SyncFileAsync(activeOverride.BaseUrl, cacheDirectory, fileName, published, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var orderedOutcomes = LlmRouterModelFiles.All.Select(fileName => outcomes[fileName]).ToList();

        _lastVerification = new LlmRouterModelVerificationSnapshot(
            activeOverride.BaseUrl,
            new ReadOnlyDictionary<string, bool>(
                orderedOutcomes.ToDictionary(o => o.FileName, o => o.ChecksumVerified, StringComparer.Ordinal)));

        return new LlmRouterModelSyncResult(activeOverride.BaseUrl, orderedOutcomes);
    }

    /// <summary>
    /// Decides every file's fate without downloading anything, so the caller can publish a one-time plan
    /// before the download loop starts. A file is resolved immediately (no entry in the returned stale
    /// list) when: it is optional and unpublished (skipped), it is required but no published checksum
    /// exists to verify it against (failed - this pipeline never installs unverified bytes), or it is
    /// already cached with a matching size and checksum (completed, verified). Everything else -
    /// missing, size-mismatched, or checksum-mismatched - is returned in <c>StaleFiles</c> for the
    /// download loop to fetch.
    /// </summary>
    /// <param name="cacheDirectory">The active model's cache directory.</param>
    /// <param name="probeResult">The already-fetched published checksums, or <see langword="null"/> if the model source could not be probed.</param>
    /// <param name="cancellationToken">A token to cancel a cached-file hash in progress.</param>
    /// <returns>
    /// The outcomes and stale-file list, plus every progress event this method would have reported for an
    /// immediately-resolved file. These are returned rather than reported directly so <see cref="SyncAsync"/>
    /// can publish the one-time plan first and only then flush them - preserving the "plan arrives before
    /// any progress" contract even though classification itself can resolve a file's fate outright.
    /// </returns>
    private async Task<(Dictionary<string, LlmRouterModelFileSyncOutcome> Outcomes, List<(string FileName, LlmRouterModelPublishedFile Published)> StaleFiles, List<LlmRouterModelSyncProgress> DeferredProgress)> ClassifyAsync(
        string cacheDirectory,
        LlmRouterModelChecksumProbeResult? probeResult,
        CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<string, LlmRouterModelFileSyncOutcome>(StringComparer.Ordinal);
        List<(string FileName, LlmRouterModelPublishedFile Published)> staleFiles = [];
        List<LlmRouterModelSyncProgress> deferredProgress = [];

        foreach (var fileName in LlmRouterModelFiles.All)
        {
            var isOptional = LlmRouterModelFiles.IsOptional(fileName);

            if (probeResult is null || !probeResult.Files.TryGetValue(fileName, out var published))
            {
                if (isOptional)
                {
                    // An optional file's absence from the published tree is expected - some exports
                    // inline all weights into model.onnx and never publish a separate external-data
                    // file - so it is skipped rather than failed, mirroring the 404-during-download case.
                    deferredProgress.Add(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed));
                    outcomes[fileName] = new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: false, ErrorMessage: null);
                }
                else
                {
                    var reason = probeResult is null
                        ? "No published checksums are available for this model source; refusing to install unverified files."
                        : $"'{fileName}' was not present in the published model tree.";
                    _logger.LogWarning("llm_router model sync skipped {FileName}: {Reason}", fileName, reason);
                    deferredProgress.Add(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed));
                    outcomes[fileName] = new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, reason);
                }

                continue;
            }

            var destinationPath = Path.Combine(cacheDirectory, fileName);
            if (!File.Exists(destinationPath))
            {
                staleFiles.Add((fileName, published));
                continue;
            }

            // Size pre-filter, before paying for a hash of a potentially-hundreds-of-MB file: a cached
            // file whose length already disagrees with the published size cannot possibly match the
            // published checksum, so it is quarantined and queued for re-download without hashing it.
            var cachedLength = new FileInfo(destinationPath).Length;
            if (cachedLength != published.Size)
            {
                SafeDelete(destinationPath);
                staleFiles.Add((fileName, published));
                continue;
            }

            deferredProgress.Add(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Verifying, TotalBytes: published.Size));

            // Isolate this file's failure per the class-level contract: the cached file may be locked by
            // a concurrent OnnxTextGenerationClient inference (IOException), fail an ACL check
            // (UnauthorizedAccessException), or have been deleted between the File.Exists check above and
            // here (FileNotFoundException).
            string cachedOid;
            try
            {
                await using var hashStream = File.OpenRead(destinationPath);
                cachedOid = PublishedChecksumHasher.Compute(hashStream, cachedLength, published.Algorithm, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "llm_router cached model file verification failed for {FileName}.", fileName);
                deferredProgress.Add(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed));
                outcomes[fileName] = new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, ex.Message);
                continue;
            }

            if (string.Equals(cachedOid, published.PublishedOid, StringComparison.OrdinalIgnoreCase))
            {
                deferredProgress.Add(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed, TotalBytes: published.Size));
                outcomes[fileName] = new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: true, ErrorMessage: null);
                continue;
            }

            _logger.LogWarning(
                "llm_router cached model file {FileName} failed checksum verification (expected {ExpectedOid}, computed {ActualOid}); re-downloading.",
                fileName,
                published.PublishedOid,
                cachedOid);

            // Quarantine the known-bad file now, before attempting the re-download: if the re-download
            // itself then fails, File.Exists must not keep reporting this mismatched file as synced
            // (status checks and OnnxTextGenerationClient's lazy loader both only check File.Exists, not
            // checksum validity).
            SafeDelete(destinationPath);
            staleFiles.Add((fileName, published));
        }

        return (outcomes, staleFiles, deferredProgress);
    }

    /// <summary>
    /// Downloads one known-stale file to a temporary sibling of its destination, verifies it against
    /// <paramref name="published"/>'s checksum, and promotes it (atomic same-volume rename) only on a
    /// match - a checksum mismatch, like any other failure, leaves no file at the destination and is
    /// reported in this file's outcome rather than propagating, per the class-level per-file isolation
    /// contract.
    /// </summary>
    /// <param name="baseUrl">The active model's base URL the file is downloaded from.</param>
    /// <param name="cacheDirectory">The active model's cache directory.</param>
    /// <param name="fileName">The file to download.</param>
    /// <param name="published">The file's published checksum and size, used to report progress and verify the download.</param>
    /// <param name="progress">An optional progress reporter for streaming this file's status.</param>
    /// <param name="cancellationToken">A token to cancel the download or verification.</param>
    private async Task<LlmRouterModelFileSyncOutcome> SyncFileAsync(
        string baseUrl,
        string cacheDirectory,
        string fileName,
        LlmRouterModelPublishedFile published,
        IProgress<LlmRouterModelSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(cacheDirectory, fileName);

        // A GUID-suffixed temp name, not a fixed "<destination>.download" - two concurrent downloads of
        // the same file (two admin clients clicking Update, or a sync overlapping the lazy
        // OnnxTextGenerationClient fallback) must not race on the same temp path. It stays a sibling of
        // the destination (not under a system temp directory) so the eventual promotion is an atomic
        // same-volume rename rather than a cross-volume copy of a potentially-hundreds-of-MB file.
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Downloading, TotalBytes: published.Size));
            var url = $"{baseUrl}/{fileName}";
            using var httpClient = _httpClientFactory.CreateClient(LlmRouterModelChecksumProbe.HttpClientName);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound && LlmRouterModelFiles.IsOptional(fileName))
            {
                progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed));
                _logger.LogInformation(
                    "llm_router optional model file {FileName} is not published at {Url}; skipping (the model's weights are presumably inlined in model.onnx).",
                    fileName,
                    url);
                return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: false, ErrorMessage: null);
            }

            response.EnsureSuccessStatusCode();

            // Stream straight to a temp file rather than buffering the whole artifact in memory - a
            // llm_router file (especially model.onnx.data) can run hundreds of MB - reporting
            // incremental byte progress along the way rather than only before/after.
            long downloadedLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                var lastReportedBytes = 0L;
                var lastReportedAt = DateTime.UtcNow;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalRead += bytesRead;

                    var now = DateTime.UtcNow;
                    if (totalRead - lastReportedBytes >= ProgressReportIntervalBytes ||
                        now - lastReportedAt >= ProgressReportInterval)
                    {
                        progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Downloading, totalRead, published.Size));
                        lastReportedBytes = totalRead;
                        lastReportedAt = now;
                    }
                }

                downloadedLength = totalRead;
            }

            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Downloading, downloadedLength, published.Size));

            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Verifying, TotalBytes: published.Size));
            string actualOid;
            await using (var hashStream = File.OpenRead(temporaryPath))
            {
                actualOid = PublishedChecksumHasher.Compute(hashStream, downloadedLength, published.Algorithm, cancellationToken);
            }

            if (!string.Equals(actualOid, published.PublishedOid, StringComparison.OrdinalIgnoreCase))
            {
                var mismatchMessage =
                    $"Checksum mismatch for '{fileName}': expected {published.PublishedOid}, computed {actualOid}.";
                _logger.LogError("llm_router model sync rejected {FileName}: {Reason}", fileName, mismatchMessage);
                progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed, TotalBytes: published.Size));
                SafeDelete(temporaryPath);
                return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, mismatchMessage);
            }

            // Promote only once the checksum has verified - "copy over once the checksum matches" - so a
            // mismatched download never leaves a same-named file behind to be confused with a verified one.
            File.Move(temporaryPath, destinationPath, overwrite: true);

            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed, TotalBytes: published.Size));
            _logger.LogInformation(
                "llm_router model sync downloaded and verified {FileName} from {Url}.",
                fileName,
                url);
            return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: true, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(temporaryPath);
            throw;
        }
        catch (Exception ex)
        {
            // Isolate this file's failure per the class-level contract: any failure short of caller
            // cancellation (HttpRequestException, IOException, UnauthorizedAccessException, etc.) is
            // reported in this file's outcome rather than aborting the whole sync.
            _logger.LogError(ex, "llm_router model sync failed for {FileName}.", fileName);
            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed));
            SafeDelete(temporaryPath);
            return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, ex.Message);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort cleanup of a partial download; a failure here (IOException,
            // UnauthorizedAccessException, or anything else File.Delete can throw) doesn't change the
            // sync outcome.
        }
    }
}
