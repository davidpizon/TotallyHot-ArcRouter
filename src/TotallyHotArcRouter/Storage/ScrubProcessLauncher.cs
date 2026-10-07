using System.Diagnostics;

namespace TotallyHot.ArcRouter.Storage;

/// <summary>
/// Runs <see cref="SqliteScrub"/> in a child copy of the router (#184, ADR-0024) so SQLite's process-wide
/// temporary directory is never changed inside the live host, where other connections are using it.
/// </summary>
/// <remarks>
/// <para>
/// <c>VACUUM</c>'s transient copy of the database must land in the protected data directory, not in a
/// temp folder other accounts can read. SQLite picks that folder per process, from <c>TMP</c>/<c>TEMP</c>
/// on Windows and <c>SQLITE_TMPDIR</c>/<c>TMPDIR</c> elsewhere, and offers only a process-wide setter that
/// SQLite documents as unsafe to change while other threads use the library. A child process gets the right
/// environment from its first instruction and shares nothing with the host's connections.
/// </para>
/// <para>
/// The child is cancellable, unlike an in-process <c>VACUUM</c>: stopping the host kills it, and SQLite
/// rolls the interrupted rebuild back.
/// </para>
/// </remarks>
internal static class ScrubProcessLauncher
{
    /// <summary>The environment variables through which the operating systems choose SQLite's temp folder.</summary>
    private static readonly string[] TempVariables = ["TMP", "TEMP", "TMPDIR", "SQLITE_TMPDIR"];

    /// <summary>
    /// Builds the child's start info: this executable, the scrub command, and a temp folder inside the
    /// protected directory.
    /// </summary>
    /// <param name="executablePath">The router executable to run.</param>
    /// <param name="databasePath">The database to scrub.</param>
    /// <param name="markerPath">The completion marker.</param>
    /// <param name="tempDirectory">The protected folder for SQLite's temporary files.</param>
    /// <returns>The start info, with output captured so the host can log it.</returns>
    private static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string databasePath,
        string markerPath,
        string tempDirectory)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(ScrubDatabaseCommand.FlagName);
        info.ArgumentList.Add(databasePath);
        info.ArgumentList.Add(markerPath);
        foreach (var variable in TempVariables) info.Environment[variable] = tempDirectory;
        return info;
    }

    /// <summary>
    /// Runs the scrub in a child process and waits for it.
    /// </summary>
    /// <param name="databasePath">The database to scrub.</param>
    /// <param name="markerPath">The completion marker.</param>
    /// <param name="tempDirectory">The protected folder for SQLite's temporary files; created first and emptied afterwards.</param>
    /// <param name="logger">Receives the child's output and the outcome.</param>
    /// <param name="cancellationToken">Cancelling kills the child.</param>
    /// <param name="runProcess">Overrides how the process is run, for tests; <see langword="null"/> starts a real child.</param>
    /// <returns><see langword="true"/> when the scrub finished or was not needed; <see langword="false"/> when it was deferred or failed.</returns>
    internal static async Task<bool> RunAsync(
        string databasePath,
        string markerPath,
        string tempDirectory,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runProcess = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            logger.LogWarning("The one-time transcript scrub cannot start: the router's executable path is unknown.");
            return false;
        }

        Directory.CreateDirectory(tempDirectory);
        try
        {
            var info = BuildStartInfo(executablePath: executable, databasePath: databasePath, markerPath: markerPath,
                tempDirectory: tempDirectory);
            var exitCode = await (runProcess ?? ((start, token) => StartAsync(start: start, logger: logger, cancellationToken: token)))
                .Invoke(info, cancellationToken).ConfigureAwait(false);

            if (exitCode == ScrubDatabaseCommand.DoneExitCode) return true;

            logger.LogWarning("The one-time transcript scrub did not finish (exit code {ExitCode}); it will be retried at the next start.",
                exitCode);
            return false;
        }
        finally
        {
            ClearDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// Starts a real child process, forwards its output to <paramref name="logger"/>, and waits for it.
    /// Cancelling kills the child and its descendants.
    /// </summary>
    private static async Task<int> StartAsync(ProcessStartInfo start, ILogger logger, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = start;
        process.OutputDataReceived += (_, e) => Forward(logger: logger, line: e.Data);
        process.ErrorDataReceived += (_, e) => Forward(logger: logger, line: e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode;
    }

    private static void Forward(ILogger logger, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) logger.LogInformation("Transcript scrub: {Line}", line);
    }

    private static void ClearDirectory(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best effort: SQLite deletes its own temporary files on close, so this only sweeps a crash's leftovers.
        }
    }
}
