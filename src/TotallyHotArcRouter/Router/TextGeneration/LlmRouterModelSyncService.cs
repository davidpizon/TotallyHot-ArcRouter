using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// Downloads, and checksum-verifies where possible, every file in <see cref="LlmRouterModelFiles.All"/>
/// for the llm_router voter's currently active model (docs/router - Governance → Benchmark Data panel's
/// "Local Voter Model" section). Each file is handled independently: a download failure or checksum
/// mismatch aborts that file only, reported in the returned outcome rather than thrown - the same
/// per-file-isolation convention <see cref="BenchmarkSyncService"/> uses for the CodeRouterBench corpus.
/// </summary>
public sealed class LlmRouterModelSyncService
{
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
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled while a file was downloading or being verified.
    /// Unlike a per-file network or checksum failure, caller cancellation aborts the whole sync rather
    /// than being recorded as a failed <see cref="LlmRouterModelFileSyncOutcome"/>.
    /// </exception>
    public async Task<LlmRouterModelSyncResult> SyncAsync(
        IProgress<LlmRouterModelSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var activeOverride = _overrideStore.Snapshot.Override;
        var cacheDirectory = activeOverride.ResolveCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);

        var probeResult = await _probe.TryFetchAsync(activeOverride.BaseUrl, cancellationToken).ConfigureAwait(false);

        List<LlmRouterModelFileSyncOutcome> outcomes = [];
        foreach (var fileName in LlmRouterModelFiles.All)
        {
            outcomes.Add(await SyncFileAsync(activeOverride.BaseUrl, cacheDirectory, fileName, probeResult, progress, cancellationToken)
                .ConfigureAwait(false));
        }

        _lastVerification = new LlmRouterModelVerificationSnapshot(
            activeOverride.BaseUrl,
            new ReadOnlyDictionary<string, bool>(
                outcomes.ToDictionary(o => o.FileName, o => o.ChecksumVerified, StringComparer.Ordinal)));

        return new LlmRouterModelSyncResult(activeOverride.BaseUrl, outcomes);
    }

    private async Task<LlmRouterModelFileSyncOutcome> SyncFileAsync(
        string baseUrl,
        string cacheDirectory,
        string fileName,
        LlmRouterModelChecksumProbeResult? probeResult,
        IProgress<LlmRouterModelSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(cacheDirectory, fileName);
        if (File.Exists(destinationPath))
        {
            // Already cached from a prior sync (or the lazy OnnxTextGenerationClient fallback); an
            // explicit "Update" only fills in what's missing rather than re-downloading a
            // multi-hundred-megabyte file on every click. But when a published checksum is available,
            // verify the cached bytes against it first - a corrupted or tampered cached file must not be
            // silently reported as succeeded - and fall through to re-download on a mismatch instead of
            // trusting stale bytes.
            if (probeResult is not null && probeResult.Files.TryGetValue(fileName, out var cachedPublished))
            {
                progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Verifying));

                // Isolate this file's failure per the class-level contract, same as the download path
                // below: the cached file may be locked by a concurrent OnnxTextGenerationClient inference
                // (IOException), fail an ACL check (UnauthorizedAccessException), or have been deleted
                // between the File.Exists check above and here (FileNotFoundException).
                string cachedOid;
                try
                {
                    var cachedLength = new FileInfo(destinationPath).Length;
                    await using var hashStream = File.OpenRead(destinationPath);
                    cachedOid = GitBlobHash.Compute(hashStream, cachedLength, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "llm_router cached model file verification failed for {FileName}.", fileName);
                    progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed));
                    return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, ex.Message);
                }

                if (string.Equals(cachedOid, cachedPublished.PublishedOid, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed));
                    return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: true, ErrorMessage: null);
                }

                _logger.LogWarning(
                    "llm_router cached model file {FileName} failed checksum verification (expected {ExpectedOid}, computed {ActualOid}); re-downloading.",
                    fileName,
                    cachedPublished.PublishedOid,
                    cachedOid);

                // Quarantine the known-bad file now, before attempting the re-download: if the
                // re-download itself then fails, File.Exists must not keep reporting this mismatched
                // file as Synced (status checks and OnnxTextGenerationClient's lazy loader both only
                // check File.Exists, not checksum validity).
                SafeDelete(destinationPath);
            }
            else
            {
                progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed));
                return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, ChecksumVerified: false, ErrorMessage: null);
            }
        }

        // A GUID-suffixed temp name, not a fixed "<destination>.download" - two concurrent downloads of
        // the same file (two admin clients clicking Update, or a sync overlapping the lazy
        // OnnxTextGenerationClient fallback) must not race on the same temp path.
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Downloading));
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
            // llm_router file (especially model.onnx.data) can run hundreds of MB.
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            var downloadedLength = new FileInfo(temporaryPath).Length;
            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Downloading, downloadedLength));

            var checksumVerified = false;
            if (probeResult is not null && probeResult.Files.TryGetValue(fileName, out var published))
            {
                progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Verifying));
                string actualOid;
                await using (var hashStream = File.OpenRead(temporaryPath))
                {
                    actualOid = GitBlobHash.Compute(hashStream, downloadedLength, cancellationToken);
                }

                if (!string.Equals(actualOid, published.PublishedOid, StringComparison.OrdinalIgnoreCase))
                {
                    var mismatchMessage =
                        $"Checksum mismatch for '{fileName}': expected {published.PublishedOid}, computed {actualOid}.";
                    _logger.LogError("llm_router model sync rejected {FileName}: {Reason}", fileName, mismatchMessage);
                    progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Failed));
                    SafeDelete(temporaryPath);
                    return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: false, ChecksumVerified: false, mismatchMessage);
                }

                checksumVerified = true;
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);

            progress?.Report(new LlmRouterModelSyncProgress(fileName, LlmRouterModelSyncStage.Completed));
            _logger.LogInformation(
                "llm_router model sync downloaded {FileName} from {Url} (checksum verified: {ChecksumVerified}).",
                fileName,
                url,
                checksumVerified);
            return new LlmRouterModelFileSyncOutcome(fileName, Succeeded: true, checksumVerified, ErrorMessage: null);
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
