using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// The update swap sequence <c>Updater.exe</c> performs, entirely behind three seams
/// (<see cref="IProcessWaiter"/>, <see cref="IServiceController"/>, <see cref="IUpdateFileSystem"/>) so it
/// is unit-testable without spawning a real process, an installed Windows service, or touching anything
/// outside a temp directory (docs/router/auto-update-plan.md Phase 2).
/// </summary>
/// <remarks>
/// Sequence: re-verify the zip's SHA256 -&gt; wait for the caller PID to exit -&gt; stop the service -&gt; rename
/// the install directory aside
/// as a backup -&gt; extract the verified zip into a fresh install directory -&gt; start the service -&gt; verify
/// it reaches Running within a bounded timeout -&gt; delete the backup on success, or roll back to it on any
/// failure from the stop step onward. The backup is never deleted until the new version has proven it
/// starts, so a failure at any later step still leaves a working install to restore.
/// </remarks>
public sealed class UpdaterService
{
    /// <summary>How long <see cref="RunAsync"/> waits for the caller process to exit before failing.</summary>
    public static readonly TimeSpan CallerExitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long each service stop/start wait is bounded to.</summary>
    public static readonly TimeSpan ServiceOperationTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessWaiter _processWaiter;
    private readonly IServiceController _serviceController;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly ILogger<UpdaterService> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdaterService"/> class.</summary>
    /// <param name="processWaiter">Waits for the caller Router process to exit.</param>
    /// <param name="serviceController">Stops and restarts the Windows Service around the swap.</param>
    /// <param name="fileSystem">Performs the backup/extract/cleanup filesystem operations.</param>
    /// <param name="logger">The logger.</param>
    public UpdaterService(
        IProcessWaiter processWaiter,
        IServiceController serviceController,
        IUpdateFileSystem fileSystem,
        ILogger<UpdaterService> logger)
    {
        ArgumentNullException.ThrowIfNull(processWaiter);
        ArgumentNullException.ThrowIfNull(serviceController);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);

        _processWaiter = processWaiter;
        _serviceController = serviceController;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full swap sequence. Returns 0 on success, matching <c>Program.cs</c>'s
    /// <c>Environment.ExitCode = 1</c> convention for every failure path (a non-zero, non-1 code is never
    /// used - any failure is uniformly reported as 1).
    /// </summary>
    /// <remarks>
    /// The first thing this does is re-hash <see cref="UpdaterArguments.ZipPath"/> and compare it against
    /// <see cref="UpdaterArguments.ExpectedSha256"/>, before the caller-exit wait and therefore well before
    /// the service stop - a mismatch leaves the running Router untouched and still running. This repeats a
    /// check the Router's <c>UpdateApplier</c> already performed, and that duplication is deliberate: the
    /// Router's copy is a fail-fast that avoids spawning this process at all, while this one is the
    /// authoritative check at the privilege boundary, because anything able to exec <c>Updater.exe</c>
    /// would otherwise get arbitrary file placement into <c>%ProgramFiles%</c> plus a service restart for
    /// free. Neither check makes the other redundant; do not "simplify" either away.
    /// </remarks>
    public async Task<int> RunAsync(UpdaterArguments arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        _logger.LogInformation(
            "Updater starting: ServiceName={ServiceName} InstallDirectory={InstallDirectory} WaitPid={WaitPid}",
            arguments.ServiceName,
            arguments.InstallDirectory,
            arguments.WaitPid);

        string actualSha256;
        try
        {
            actualSha256 = _fileSystem.ComputeSha256(arguments.ZipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not hash {ZipPath} for verification. Aborting before touching any files.", arguments.ZipPath);
            return 1;
        }

        if (!string.Equals(actualSha256, arguments.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Update zip {ZipPath} failed SHA256 re-verification (expected {Expected}, got {Actual}). Aborting; the service was left running and nothing was touched.",
                arguments.ZipPath,
                arguments.ExpectedSha256,
                actualSha256);
            return 1;
        }

        var exited = await _processWaiter
            .WaitForExitAsync(arguments.WaitPid, CallerExitTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!exited)
        {
            _logger.LogError(
                "Caller process {WaitPid} did not exit within {Timeout}. Aborting before touching any files.",
                arguments.WaitPid,
                CallerExitTimeout);
            return 1;
        }

        var backupDirectory = arguments.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + $".backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        try
        {
            _serviceController.Stop(arguments.ServiceName, ServiceOperationTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to stop service {ServiceName}. Aborting before touching any files.", arguments.ServiceName);
            return 1;
        }

        try
        {
            _fileSystem.MoveDirectory(arguments.InstallDirectory, backupDirectory);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to back up {InstallDirectory}; attempting to restart the service on the unmodified install.", arguments.InstallDirectory);
            TryRestartWithoutSwap(arguments.ServiceName);
            return 1;
        }

        try
        {
            _fileSystem.ExtractZip(arguments.ZipPath, arguments.InstallDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _logger.LogError(ex, "Failed to extract the update zip; rolling back to the backup.");
            RollBack(arguments, backupDirectory);
            return 1;
        }

        try
        {
            _serviceController.Start(arguments.ServiceName, ServiceOperationTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger.LogError(ex, "The updated service failed to start; rolling back to the backup.");
            RollBack(arguments, backupDirectory);
            return 1;
        }

        if (!_serviceController.IsRunning(arguments.ServiceName))
        {
            _logger.LogError("The updated service did not reach the Running state; rolling back to the backup.");
            RollBack(arguments, backupDirectory);
            return 1;
        }

        _fileSystem.DeleteDirectory(backupDirectory);
        _logger.LogInformation("Update applied successfully; service {ServiceName} is running.", arguments.ServiceName);
        return 0;
    }

    /// <summary>Restores the pre-swap install directory from the backup and restarts the service, for a failure that occurred before or during extraction.</summary>
    private void RollBack(UpdaterArguments arguments, string backupDirectory)
    {
        try
        {
            _fileSystem.DeleteDirectory(arguments.InstallDirectory);
            _fileSystem.MoveDirectory(backupDirectory, arguments.InstallDirectory);
        }
        catch (IOException ex)
        {
            _logger.LogCritical(ex, "Rollback failed: could not restore {InstallDirectory} from {BackupDirectory}. Manual intervention is required.", arguments.InstallDirectory, backupDirectory);
            return;
        }

        TryRestartWithoutSwap(arguments.ServiceName);
    }

    /// <summary>Best-effort restart of the service on whatever install directory is currently in place - never lets a restart failure mask the original error being logged.</summary>
    private void TryRestartWithoutSwap(string serviceName)
    {
        try
        {
            _serviceController.Start(serviceName, ServiceOperationTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger.LogCritical(ex, "Could not restart service {ServiceName} after a failed update. Manual intervention is required.", serviceName);
        }
    }
}
