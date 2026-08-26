using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Production <see cref="IUpdateApplier"/>: downloads both release zips to temp files, verifies each
/// against the SHA256 the release published, refreshes the sibling <c>...\Updater\</c> directory from the
/// Updater zip, and only then launches <c>TotallyHotArcRouter.Updater.exe</c> as a detached process to
/// perform the Router stop/swap/restart (docs/router/auto-update-plan.md Phase 2). This process cannot
/// overwrite its own running files, which is exactly why the Router swap is delegated to a separate
/// helper process - and symmetrically, the Updater's files are safe for *this* process to replace,
/// because the Updater is not running while the Router is.
/// </summary>
/// <remarks>
/// <para>
/// Expects the canonical install layout <c>%ProgramFiles%\TotallyHotArcRouter\Router\</c> (this process)
/// beside <c>%ProgramFiles%\TotallyHotArcRouter\Updater\</c> (the helper) - i.e. <c>Updater.exe</c> is
/// resolved as a sibling directory of this process's own <see cref="AppContext.BaseDirectory"/>, not a
/// configurable path. If it is not found there, <see cref="ApplyAsync"/> fails without downloading
/// anything, so a broken deployment layout is caught before any file is touched.
/// </para>
/// <para>
/// <b>Refresh-before-use ordering.</b> The Updater directory is replaced *before* the Updater is invoked,
/// so the binary that performs the Router swap is always the one shipped with the version being
/// installed. A Router release that requires new Updater behavior therefore gets it in the same apply,
/// rather than needing a two-release dance. Any failure while swapping the Updater restores the backup
/// and aborts without touching the Router at all.
/// </para>
/// <para>
/// <b>Double verification is deliberate.</b> The SHA256 check performed here on the Router zip is
/// repeated by <c>UpdaterService.RunAsync</c> on the other side of the handoff. This one is a fail-fast
/// that avoids spawning the updater at all for a corrupt download; that one is the authoritative check at
/// the privilege boundary, since anything able to exec <c>Updater.exe</c> must not be able to place
/// arbitrary files into <c>%ProgramFiles%</c>. Do not remove either as "redundant".
/// </para>
/// </remarks>
public sealed class UpdateApplier : IUpdateApplier
{
    private const string UpdaterExecutableName = "TotallyHotArcRouter.Updater.exe";
    private const string UpdaterDirectoryName = "Updater";

    private readonly HttpClient _httpClient;
    private readonly UpdateOptions _options;
    private readonly ILogger<UpdateApplier> _logger;
    private readonly IUpdaterProcessLauncher _launcher;
    private readonly IUpdateFileOperations _fileOperations;

    /// <summary>Initializes a new instance of the <see cref="UpdateApplier"/> class.</summary>
    /// <param name="httpClient">The HTTP client used to download the release assets.</param>
    /// <param name="options">Auto-update configuration, including the Windows Service name.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="launcher">
    /// Launches the detached updater process. Defaults to <see cref="ProcessUpdaterLauncher"/>; not
    /// registered in DI, so this parameter exists purely as the test seam - unit tests construct
    /// <see cref="UpdateApplier"/> with a fake here instead of ever spawning a real process.
    /// </param>
    /// <param name="fileOperations">
    /// Performs the Updater directory's backup/extract/restore. Defaults to
    /// <see cref="RealUpdateFileOperations"/>; like <paramref name="launcher"/> it is not registered in
    /// DI and exists so unit tests can drive those failure paths without real <c>%ProgramFiles%</c> access.
    /// </param>
    public UpdateApplier(
        HttpClient httpClient,
        IOptions<UpdateOptions> options,
        ILogger<UpdateApplier> logger,
        IUpdaterProcessLauncher? launcher = null,
        IUpdateFileOperations? fileOperations = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _launcher = launcher ?? new ProcessUpdaterLauncher();
        _fileOperations = fileOperations ?? new RealUpdateFileOperations();
    }

    /// <inheritdoc />
    public async Task<ApplyUpdateResult> ApplyAsync(ReleaseCheckResult update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!update.IsUpdateAvailable ||
            update.AssetDownloadUrl is null ||
            update.AssetSha256 is null ||
            update.UpdaterAssetDownloadUrl is null ||
            update.UpdaterAssetSha256 is null)
        {
            return ApplyUpdateResult.Failure("No verified update is available to apply.");
        }

        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDirectory = Path.GetDirectoryName(installDirectory);
        if (parentDirectory is null)
        {
            return ApplyUpdateResult.Failure($"Could not determine the install directory's parent from '{installDirectory}'.");
        }

        var updaterDirectory = Path.Combine(parentDirectory, UpdaterDirectoryName);
        var updaterPath = Path.Combine(updaterDirectory, UpdaterExecutableName);
        if (!_fileOperations.FileExists(updaterPath))
        {
            _logger.LogError("Update apply aborted: updater not found at {UpdaterPath}.", updaterPath);
            return ApplyUpdateResult.Failure($"Updater executable not found at '{updaterPath}'.");
        }

        var routerZipPath = Path.Combine(Path.GetTempPath(), $"totallyhotarcrouter-update-{Guid.NewGuid():N}.zip");
        var updaterZipPath = Path.Combine(Path.GetTempPath(), $"totallyhotarcrouter-updater-{Guid.NewGuid():N}.zip");

        var routerDownload = await DownloadAndVerifyAsync(update.AssetDownloadUrl, update.AssetSha256, routerZipPath, "Router", cancellationToken).ConfigureAwait(false);
        if (routerDownload is not null)
        {
            TryDelete(routerZipPath);
            return routerDownload;
        }

        var updaterDownload = await DownloadAndVerifyAsync(update.UpdaterAssetDownloadUrl, update.UpdaterAssetSha256, updaterZipPath, "Updater", cancellationToken).ConfigureAwait(false);
        if (updaterDownload is not null)
        {
            TryDelete(routerZipPath);
            TryDelete(updaterZipPath);
            return updaterDownload;
        }

        var updaterSwap = SwapUpdaterDirectory(updaterDirectory, updaterPath, updaterZipPath);
        TryDelete(updaterZipPath);
        if (updaterSwap is not null)
        {
            TryDelete(routerZipPath);
            return updaterSwap;
        }

        try
        {
            _launcher.Launch(updaterPath, installDirectory, routerZipPath, _options.ServiceName, update.AssetSha256);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update apply failed: could not launch the updater process.");
            TryDelete(routerZipPath);
            return ApplyUpdateResult.Failure($"Could not launch the updater: {ex.Message}");
        }

        _logger.LogInformation(
            "Update handoff succeeded for version {LatestVersion}; the {ServiceName} service will restart shortly.",
            update.LatestVersion,
            _options.ServiceName);

        return ApplyUpdateResult.Handoff(
            $"Verified download handed off to the updater. The {_options.ServiceName} service will restart shortly.");
    }

    /// <summary>
    /// Replaces <paramref name="updaterDirectory"/> with the contents of <paramref name="updaterZipPath"/>,
    /// keeping a timestamped backup aside (the same <c>&lt;dir&gt;.backup-&lt;UTC&gt;</c> convention
    /// <c>UpdaterService</c> uses for the Router) until the new contents are confirmed to contain the
    /// updater executable. Returns <see langword="null"/> on success, or the failure result to return to
    /// the caller - in which case the Router is guaranteed not to have been touched and nothing has been
    /// launched.
    /// </summary>
    /// <remarks>
    /// The extraction is unconditional on every apply: there is deliberately no "skip if unchanged" marker
    /// file or persisted hash, because a stale marker is its own failure mode and re-extracting a few
    /// megabytes costs nothing next to a service restart.
    /// </remarks>
    private ApplyUpdateResult? SwapUpdaterDirectory(string updaterDirectory, string updaterPath, string updaterZipPath)
    {
        var backupDirectory = updaterDirectory + ".backup-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        try
        {
            _fileOperations.MoveDirectory(updaterDirectory, backupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Update apply aborted: could not back up {UpdaterDirectory}. The Router was not touched.", updaterDirectory);
            return ApplyUpdateResult.Failure($"Could not back up the updater directory '{updaterDirectory}'. No files were changed.");
        }

        try
        {
            _fileOperations.ExtractZip(updaterZipPath, updaterDirectory);

            if (!_fileOperations.FileExists(updaterPath))
            {
                throw new InvalidDataException($"The Updater zip did not contain '{UpdaterExecutableName}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to install the new updater into {UpdaterDirectory}; restoring the previous updater.", updaterDirectory);
            return RestoreUpdaterBackup(updaterDirectory, backupDirectory, ex.Message);
        }

        try
        {
            _fileOperations.DeleteDirectory(backupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover backup directory is cosmetic - the new updater is in place and correct, so this
            // must not abort the apply.
            _logger.LogWarning(ex, "Could not delete the updater backup at {BackupDirectory}; continuing.", backupDirectory);
        }

        _logger.LogInformation("Updater refreshed at {UpdaterDirectory} before handing off the Router swap.", updaterDirectory);
        return null;
    }

    /// <summary>
    /// Rolls <paramref name="updaterDirectory"/> back to <paramref name="backupDirectory"/> after a failed
    /// updater install. If the restore itself fails, the previous updater is unrecoverable by this process,
    /// so it is logged as critical and the apply still aborts - never proceeding to the Router swap, which
    /// would then have no working updater to run it.
    /// </summary>
    private ApplyUpdateResult RestoreUpdaterBackup(string updaterDirectory, string backupDirectory, string originalFailure)
    {
        try
        {
            _fileOperations.DeleteDirectory(updaterDirectory);
            _fileOperations.MoveDirectory(backupDirectory, updaterDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(
                ex,
                "Could not restore the previous updater from {BackupDirectory} to {UpdaterDirectory}. Manual intervention is required; the Router was not touched.",
                backupDirectory,
                updaterDirectory);
            return ApplyUpdateResult.Failure(
                $"The updater could not be installed and the previous one could not be restored from '{backupDirectory}'. Manual intervention is required. The Router was not modified.");
        }

        return ApplyUpdateResult.Failure(
            $"The updater could not be installed ({originalFailure}); the previous updater was restored and the Router was not modified.");
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/> and checks it against
    /// <paramref name="expectedSha256"/>. Returns <see langword="null"/> when the file is downloaded and
    /// verified, or the failure result to return to the caller.
    /// </summary>
    private async Task<ApplyUpdateResult?> DownloadAndVerifyAsync(
        string url,
        string expectedSha256,
        string destinationPath,
        string assetLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await DownloadAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Update apply failed: could not download the {AssetLabel} release asset.", assetLabel);
            return ApplyUpdateResult.Failure($"Download failed for the {assetLabel} asset: {ex.Message}");
        }

        var actualSha256 = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Update apply aborted: {AssetLabel} checksum mismatch (expected {Expected}, got {Actual}). Nothing was touched.",
                assetLabel,
                expectedSha256,
                actualSha256);
            return ApplyUpdateResult.Failure($"The downloaded {assetLabel} asset failed checksum verification. No files were touched.");
        }

        return null;
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="destinationPath"/>, streaming rather than buffering the whole asset in memory.</summary>
    private async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Computes the lowercase-hex SHA256 of the file at <paramref name="path"/>.</summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Best-effort cleanup of a temp download on a failure path - never lets a cleanup error mask the real failure.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
