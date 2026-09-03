using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Launches an elevated process. An abstraction over
/// <see cref="Process.Start(ProcessStartInfo)"/> so <see cref="MsiUpdateApplier"/>'s download-and-verify
/// logic can be exercised in a unit test without ever spawning a real (UAC-prompting) process, mirroring
/// the Router's now-deleted <c>IUpdaterProcessLauncher</c> seam one level up.
/// </summary>
public interface IElevatedProcessLauncher
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/>, requesting elevation
    /// (<c>UseShellExecute = true</c>, <c>Verb = "runas"</c>) - this is what triggers the single UAC
    /// prompt the operator sees.
    /// </summary>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    void Launch(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>
/// Production <see cref="IElevatedProcessLauncher"/>, wrapping <see cref="Process.Start(ProcessStartInfo)"/>
/// directly.
/// </summary>
public sealed class ElevatedProcessLauncher : IElevatedProcessLauncher
{
    /// <inheritdoc/>
    public void Launch(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
            Verb = "runas"
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException($"Process.Start returned null for '{fileName}'.");
    }
}

/// <summary>
/// The outcome of one <see cref="IMsiUpdateApplier.ApplyAsync"/> call. A successful launch does not mean
/// the update finished installing - it means the elevated <c>msiexec</c> process was started and will
/// stop the Router's Windows Service, replace both install directories, and restart the service; this
/// process is expected to exit immediately afterward (see <see cref="MsiUpdateApplier"/>'s remarks), so
/// nothing past the launch is reported here.
/// </summary>
/// <param name="Succeeded">Whether the download, checksum verification, and <c>msiexec</c> launch all succeeded.</param>
/// <param name="Message">A human-readable outcome, for the GUI.</param>
public sealed record MsiApplyResult(bool Succeeded, string Message)
{
    /// <summary>Builds a successful-launch result.</summary>
    public static MsiApplyResult Launched(string message)
    {
        return new MsiApplyResult(true, Message: message);
    }

    /// <summary>
    /// Builds a failure result - the download or checksum failed, or the installer could not be launched. Nothing was
    /// touched.
    /// </summary>
    public static MsiApplyResult Failure(string message)
    {
        return new MsiApplyResult(false, Message: message);
    }
}

/// <summary>
/// Downloads and checksum-verifies the release's MSI installer, then launches it elevated. The seam
/// <c>UpdateStore.ApplyAsync</c> is tested against, so a unit test never needs to spawn a real process or
/// trigger a real UAC prompt. Lives in this plain <c>net10.0</c> library (not the Windows-only MAUI
/// project) purely so it is unit-testable in CI, mirroring <see cref="UpdateAdminClient"/>.
/// </summary>
/// <remarks>
/// This is the GUI-elevated design decided for the MSI packaging switch
/// (docs/router/packaging-and-distribution.md): the GUI downloads, verifies, and launches
/// <c>msiexec /i &lt;path&gt; /qn REBOOT=ReallySuppress /l*v &lt;logpath&gt;</c> via
/// <see cref="IElevatedProcessLauncher"/>, which is what triggers the single UAC elevation prompt. The
/// caller (<c>TotallyHot.ArcRouter.Gui.Services.UpdateStore</c>) must exit the GUI process immediately
/// after a successful <see cref="ApplyAsync"/> - this process cannot hold its own files locked while the
/// MSI tries to replace <c>...\Gui\</c>. This class only launches; it never observes or waits for that
/// exit, matching the Windows Installer transaction being opaque to this process once started.
/// </remarks>
public sealed class MsiUpdateApplier : IMsiUpdateApplier
{
    private readonly HttpClient _httpClient;
    private readonly IElevatedProcessLauncher _launcher;
    private readonly ILogger<MsiUpdateApplier> _logger;

    /// <summary>Initializes a new instance of the <see cref="MsiUpdateApplier"/> class.</summary>
    /// <param name="httpClient">The HTTP client used to download the installer asset.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="launcher">
    /// Launches the elevated installer process. Defaults to <see cref="ElevatedProcessLauncher"/>; not
    /// registered in DI, so this parameter exists purely as the test seam - unit tests construct
    /// <see cref="MsiUpdateApplier"/> with a fake here instead of ever triggering a UAC prompt.
    /// </param>
    public MsiUpdateApplier(HttpClient httpClient, ILogger<MsiUpdateApplier> logger,
        IElevatedProcessLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
        _launcher = launcher ?? new ElevatedProcessLauncher();
    }

    /// <inheritdoc/>
    public async Task<MsiApplyResult> ApplyAsync(
        string assetDownloadUrl,
        string assetSha256,
        string latestVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDownloadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestVersion);

        var msiPath = Path.Combine(path1: Path.GetTempPath(),
            path2: $"totallyhotarcrouter-update-{Guid.NewGuid():N}.msi");
        var logPath = Path.Combine(path1: Path.GetTempPath(),
            path2: $"totallyhotarcrouter-update-{Guid.NewGuid():N}.log");

        try
        {
            await DownloadAsync(url: assetDownloadUrl, destinationPath: msiPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(exception: ex, message: "Update apply failed: could not download the installer.");
            TryDelete(msiPath);
            return MsiApplyResult.Failure($"Download failed: {ex.Message}");
        }

        var actualSha256 = await ComputeSha256Async(path: msiPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(a: actualSha256, b: assetSha256, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                message:
                "Update apply aborted: installer checksum mismatch (expected {Expected}, got {Actual}). Nothing was touched.",
                assetSha256,
                actualSha256);
            TryDelete(msiPath);
            return MsiApplyResult.Failure(
                "The downloaded installer failed checksum verification. No files were touched.");
        }

        // TODO(signing): once a code-signing certificate exists (docs/router/packaging-and-distribution.md
        // lists this as an open prerequisite), also verify the MSI's Authenticode signature here before
        // launching it. Today's SHA256-published-by-the-release check is the only integrity guarantee -
        // this seam is where a WinVerifyTrust-based check would be added without touching any caller.

        try
        {
            _launcher.Launch(
                fileName: "msiexec.exe",
                arguments: ["/i", msiPath, "/qn", "REBOOT=ReallySuppress", "/l*v", logPath]);
        }
        catch (Exception ex)
        {
            _logger.LogError(exception: ex, message: "Update apply failed: could not launch the installer.");
            TryDelete(msiPath);
            return MsiApplyResult.Failure($"Could not launch the installer: {ex.Message}");
        }

        _logger.LogInformation(
            message: "Installer launched for version {LatestVersion}; exiting so the file swap can proceed.",
            latestVersion);

        // The downloaded MSI is intentionally not deleted here - msiexec is still reading it in the
        // elevated process this just launched. It is left in the temp directory for the OS's normal temp
        // cleanup, the same way a browser leaves a downloaded installer behind after handing it to the OS.
        return MsiApplyResult.Launched(
            $"Installer launched for version {latestVersion}. Approve the administrator prompt to continue - this application will close and restart.");
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>, streaming rather than buffering the
    /// whole asset in memory.
    /// </summary>
    private async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(requestUri: url, completionOption: HttpCompletionOption.ResponseHeadersRead,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination: destination, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Computes the lowercase-hex SHA256 of the file at <paramref name="path"/>.</summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(source: stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Best-effort cleanup of a temp download on a failure path - never lets a cleanup error mask the real failure.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Downloads, verifies, and launches an installer for a verified update. The seam
/// <c>TotallyHot.ArcRouter.Gui.Services.UpdateStore</c> is tested against; the sole implementation is
/// <see cref="MsiUpdateApplier"/>.
/// </summary>
public interface IMsiUpdateApplier
{
    /// <summary>
    /// Downloads the MSI at <paramref name="assetDownloadUrl"/>, verifies it against
    /// <paramref name="assetSha256"/>, and - only if that succeeds - launches it elevated via
    /// <c>msiexec</c>. Never throws; every failure (download, checksum mismatch, launch failure) is
    /// reported via <see cref="MsiApplyResult.Succeeded"/> being <see langword="false"/>, and in that case
    /// nothing on disk relevant to the install has been touched and no installer was launched.
    /// </summary>
    /// <param name="assetDownloadUrl">The release's MSI asset direct download URL.</param>
    /// <param name="assetSha256">The MSI's published SHA256 (lowercase hex).</param>
    /// <param name="latestVersion">The version being installed, for logging.</param>
    /// <param name="cancellationToken">
    /// Cancels the download only; once the installer is launched, applying is out of this
    /// process's hands.
    /// </param>
    Task<MsiApplyResult> ApplyAsync(string assetDownloadUrl, string assetSha256, string latestVersion,
        CancellationToken cancellationToken = default);
}