using System.Diagnostics;
using System.Runtime.Versioning;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// Launches a jailed interpreter run on Linux: <c>unshare(1)</c> establishes the isolating namespaces
/// (empty network namespace = air-gap; PID/mount/UTS/IPC), a per-run cgroup v2 leaf enforces memory/pids/
/// CPU ceilings, and an external supervisor hard-kills the process tree when the wall-clock ceiling is hit.
/// The guest cannot extend or disable the timeout.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxJailLauncher : IJailLauncher
{
    // A process terminated by a signal exits with 128 + signal number; SIGSYS (31) is what a seccomp
    // SCMP_ACT_KILL_PROCESS raises, so exit code 159 indicates a seccomp denial. Plain int convention —
    // no native call needed to detect it.
    private const int SeccompKillExitCode = 128 + 31;

    private readonly CgroupManager _cgroups;
    private readonly ILogger<LinuxJailLauncher> _logger;

    /// <summary>Initializes a new instance of the <see cref="LinuxJailLauncher"/> class.</summary>
    /// <param name="cgroups">The cgroup manager.</param>
    /// <param name="logger">The logger.</param>
    public LinuxJailLauncher(CgroupManager cgroups, ILogger<LinuxJailLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(cgroups);
        ArgumentNullException.ThrowIfNull(logger);
        _cgroups = cgroups;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcome> RunAsync(JailSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var cgroupName = "run-" + Guid.NewGuid().ToString("N");
        var leaf = _cgroups.Create(cgroupName, spec);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process();
        ConfigureStartInfo(process.StartInfo, spec);

        var timedOut = false;
        try
        {
            process.Start();

            if (leaf is not null)
            {
                _cgroups.AddProcess(leaf, process.Id);
            }

            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(spec.TimeoutMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                KillProcessTree(process);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The external cancellationToken fired (not merely the timeout). The tree must not
                // outlive this method, so kill it before propagating - WaitForExitAsync can't take
                // cancellationToken here since it is already cancelled.
                KillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await DrainQuietlyAsync(stdoutTask).ConfigureAwait(false);
                await DrainQuietlyAsync(stderrTask).ConfigureAwait(false);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            stopwatch.Stop();

            var exitCode = process.ExitCode;
            var oomKilled = _cgroups.ReadOomKills(leaf) > 0;
            var peak = _cgroups.ReadPeakMemory(leaf);

            return new ExecutionOutcome
            {
                ExitCode = timedOut ? null : exitCode,
                TimedOut = timedOut,
                OomKilled = oomKilled,
                SeccompDenied = !timedOut && exitCode == SeccompKillExitCode,
                Stdout = stdout,
                Stderr = stderr,
                WallClockMs = stopwatch.ElapsedMilliseconds,
                PeakMemoryBytes = peak,
            };
        }
        finally
        {
            _cgroups.Remove(leaf);
        }
    }

    /// <summary>Populates a <see cref="ProcessStartInfo"/> to launch the interpreter under <c>unshare</c> with the jail spec's namespace flags and redirected I/O streams.</summary>
    private static void ConfigureStartInfo(ProcessStartInfo startInfo, JailSpec spec)
    {
        // Launch via unshare so the interpreter self-isolates into fresh namespaces. Network isolation is
        // the empty --net namespace: no veth, no route out.
        startInfo.FileName = "unshare";
        foreach (var flag in spec.UnshareFlags)
        {
            startInfo.ArgumentList.Add(flag);
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(spec.Interpreter);
        startInfo.ArgumentList.Add(spec.ScriptFileName);

        startInfo.WorkingDirectory = spec.WorkingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.UseShellExecute = false;
    }

    /// <summary>Kills the jailed process and its entire tree, treating an already-exited process as a non-error.</summary>
    private void KillProcessTree(Process process)
    {
        try
        {
            // Kill the whole tree: unshare's --kill-child also cascades SIGKILL to the jailed child, so the
            // managed Process.Kill is the external supervisor's authority the guest cannot revoke.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Process already exited before the supervisor kill.");
        }
    }

    /// <summary>Awaits a stdout/stderr drain task, swallowing any exception since the stream may be torn down mid-read as the process exits.</summary>
    private static async Task DrainQuietlyAsync(Task<string> drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The stream is being torn down with the process; draining is best-effort.
        }
    }
}

