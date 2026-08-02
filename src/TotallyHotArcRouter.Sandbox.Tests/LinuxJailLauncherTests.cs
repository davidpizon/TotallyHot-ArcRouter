using System.Diagnostics;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="LinuxJailLauncher"/> against a real <c>unshare(1)</c>-launched process, focused on the
/// process-tree cleanup guarantee on cancellation. Skips itself off Linux since the launcher is Linux-only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LinuxJailLauncherTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(), "acrouter-jail-test-" + Guid.NewGuid().ToString("N"));

    public LinuxJailLauncherTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_KillsProcessTreeAndDoesNotOrphan()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var scriptPath = Path.Combine(_workingDirectory, "run.sh");
        await File.WriteAllTextAsync(scriptPath, "sleep 30\n", TestContext.Current.CancellationToken);

        var spec = new JailSpec
        {
            Interpreter = "sh",
            UnshareFlags = JailCommandBuilder.DefaultUnshareFlags,
            ScriptFileName = "run.sh",
            WorkingDirectory = _workingDirectory,
            TimeoutMs = 30_000,
            MemoryMax = "134217728",
            PidsMax = "32",
            CpuMax = "100000 100000",
        };

        var launcher = new LinuxJailLauncher(
            new CgroupManager(NullLogger<CgroupManager>.Instance),
            NullLogger<LinuxJailLauncher>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = launcher.RunAsync(spec, cts.Token);

        var started = await PollUntilAsync(() => JailedProcessIsRunning(_workingDirectory), TimeSpan.FromSeconds(15));
        if (!started)
        {
            // A common cause is the host restricting unprivileged user namespaces (e.g. Ubuntu 24.04's
            // kernel.apparmor_restrict_unprivileged_userns=1), which makes unshare fail fast rather than
            // hang - RunAsync then completes quickly with a nonzero exit rather than throwing, so surface
            // its outcome here instead of just "never started", which hides the actual cause.
            var diagnostic = runTask.IsCompleted
                ? await DescribeOutcomeAsync(runTask)
                : "the launcher call is still pending (process may be slow to start, or hung).";
            Assert.Fail($"The jailed process never started within the polling window. Launcher outcome: {diagnostic}");
        }

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        // The launcher must kill the tree before the cancelled call returns, not just eventually - so
        // this should already be false without needing to wait out the full polling window.
        var stillRunning = await PollUntilAsync(() => JailedProcessIsRunning(_workingDirectory), TimeSpan.FromSeconds(15));
        Assert.False(stillRunning, "Jailed process tree was not cleaned up after external cancellation.");
    }

    private static async Task<bool> PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    private static async Task<string> DescribeOutcomeAsync(Task<ExecutionOutcome> task)
    {
        try
        {
            var outcome = await task;
            return $"ExitCode={outcome.ExitCode}, TimedOut={outcome.TimedOut}, Stderr='{outcome.Stderr}'";
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks whether any process on the host currently has <paramref name="workingDirectory"/> as its
    /// current working directory, via <c>/proc/&lt;pid&gt;/cwd</c>. This - not a command-line marker - is
    /// the reliable signal here: <c>unshare(1)</c>'s launched command line is always the fixed
    /// <c>unshare ... -- sh run.sh</c> (the working directory is set via <see cref="ProcessStartInfo.WorkingDirectory"/>,
    /// a process attribute, not an argv element), so a <c>pgrep -f &lt;working-directory-path&gt;</c> can
    /// never match it - confirmed the hard way (this test failed in CI matching against a marker that was
    /// never actually present in any process's command line). <c>unshare --pid</c> gives the launched
    /// process a new PID namespace, but that only affects the PID *numbers* it sees internally; from this
    /// (host-namespace) process's point of view /proc still reports its real host-visible pid and cwd.
    /// </summary>
    private static bool JailedProcessIsRunning(string workingDirectory)
    {
        foreach (var pidDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(pidDirectory), out _))
            {
                continue;
            }

            try
            {
                var cwdLink = File.ResolveLinkTarget(Path.Combine(pidDirectory, "cwd"), returnFinalTarget: false);
                if (cwdLink?.FullName == workingDirectory)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process may have exited between enumeration and the readlink, or /proc/<pid>/cwd
                // may be unreadable for a process we don't own - neither indicates our jailed process.
            }
        }

        return false;
    }
}

