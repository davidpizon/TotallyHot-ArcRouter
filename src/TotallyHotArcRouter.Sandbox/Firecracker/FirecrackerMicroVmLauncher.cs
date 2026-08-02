using System.Diagnostics;
using System.Runtime.Versioning;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Runs a snippet in a Firecracker microVM: starts Firecracker on a per-run API socket, applies the
/// configured vCPU/memory sizing, restores the pre-booted (network-free) snapshot, executes the snippet
/// through the in-guest agent over vsock, and tears the VM down. An external supervisor bounds the
/// wall-clock time and destroys the VM on breach.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class FirecrackerMicroVmLauncher : IMicroVmLauncher
{
    private readonly IGuestAgentClient _guestAgent;
    private readonly IFirecrackerClientFactory _clientFactory;
    private readonly SandboxOptions _options;
    private readonly ILogger<FirecrackerMicroVmLauncher> _logger;

    /// <summary>Initializes a new instance of the <see cref="FirecrackerMicroVmLauncher"/> class.</summary>
    /// <param name="guestAgent">The in-guest exec agent client.</param>
    /// <param name="clientFactory">Creates the per-run Firecracker API client.</param>
    /// <param name="options">The sandbox options (Firecracker paths, vsock).</param>
    /// <param name="logger">The logger.</param>
    public FirecrackerMicroVmLauncher(
        IGuestAgentClient guestAgent,
        IFirecrackerClientFactory clientFactory,
        IOptions<SandboxOptions> options,
        ILogger<FirecrackerMicroVmLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(guestAgent);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _guestAgent = guestAgent;
        _clientFactory = clientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcome> RunAsync(MicroVmSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var fc = _options.Firecracker;
        var binary = FirecrackerLocator.Find(fc.BinaryPath)
            ?? throw new InvalidOperationException("Firecracker binary not found.");

        if (string.IsNullOrEmpty(fc.SnapshotPath) || string.IsNullOrEmpty(fc.MemorySnapshotPath))
        {
            throw new InvalidOperationException("Firecracker snapshot paths are not configured.");
        }

        var apiSocket = Path.Combine(spec.WorkingDirectory, "firecracker-api.sock");
        var vsockUds = Path.Combine(spec.WorkingDirectory, "firecracker-vsock.sock");
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process();
        ConfigureStartInfo(process.StartInfo, binary, apiSocket, spec.WorkingDirectory);

        var timedOut = false;
        Task<string> drainStdout = Task.FromResult(string.Empty);
        Task<string> drainStderr = Task.FromResult(string.Empty);
        try
        {
            process.Start();

            // Continuously drain Firecracker's stdout/stderr so a full pipe buffer can never block the
            // process (and hang the sandbox worker). These complete when the process is torn down below.
            drainStdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            drainStderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await WaitForSocketAsync(
                apiSocket, "Firecracker API socket", RemainingBudget(stopwatch, spec.TimeoutMs, fc.ApiSocketWaitMs), cancellationToken)
                .ConfigureAwait(false);

            using var client = _clientFactory.Create(apiSocket);

            // Applied before the snapshot restores so a sizing mismatch against the golden snapshot
            // surfaces as a loud failure here rather than a silent no-op - EnsureSuccessStatusCode inside
            // SendAsync throws on rejection, which is intentional: a drifted snapshot is a real signal.
            var machineConfigPayload = FirecrackerArgumentBuilder.BuildMachineConfig(fc.VcpuCount, spec.MemoryMaxBytes);
            await client.ConfigureMachineAsync(machineConfigPayload, cancellationToken).ConfigureAwait(false);

            // Also applied before the snapshot restores - Firecracker's vsock device must be configured
            // pre-boot, but (unlike machine sizing) it is *not* baked into the snapshot: a restored guest
            // gets a fresh UDS endpoint each run, so this genuinely takes effect per-run rather than being
            // a best-effort no-op.
            var vsockConfigPayload = FirecrackerArgumentBuilder.BuildVsockConfig(fc.VsockGuestCid, vsockUds);
            await client.ConfigureVsockAsync(vsockConfigPayload, cancellationToken).ConfigureAwait(false);

            var payload = FirecrackerArgumentBuilder.BuildSnapshotLoadPayload(
                fc.SnapshotPath, fc.MemorySnapshotPath, resumeVm: true);
            await client.LoadSnapshotAsync(payload, cancellationToken).ConfigureAwait(false);

            // Firecracker only creates and starts listening on the vsock UDS once the guest actually
            // resumes (LoadSnapshotAsync's resume_vm: true above) - not at PUT /vsock config time - so the
            // guest agent below would otherwise race a socket file that doesn't exist yet.
            await WaitForSocketAsync(
                vsockUds, "Firecracker vsock UDS", RemainingBudget(stopwatch, spec.TimeoutMs, fc.VsockSocketWaitMs), cancellationToken)
                .ConfigureAwait(false);

            using var execCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Whatever's left of spec.TimeoutMs after the setup above - not a fresh full budget - so the
            // total (setup + guest exec) can never exceed the run's configured wall-clock ceiling.
            execCts.CancelAfter(RemainingBudget(stopwatch, spec.TimeoutMs, int.MaxValue));

            GuestExecResult guest;
            try
            {
                guest = await _guestAgent.ExecuteAsync(
                    vsockUds, fc.VsockPort, spec.Language, spec.Code, spec.TimeoutMs, execCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                guest = new GuestExecResult(0, string.Empty, string.Empty, TimedOut: true, PeakMemoryBytes: 0);
            }

            stopwatch.Stop();
            var effectiveTimeout = timedOut || guest.TimedOut;

            return new ExecutionOutcome
            {
                ExitCode = effectiveTimeout ? null : guest.ExitCode,
                TimedOut = effectiveTimeout,
                Stdout = guest.Stdout,
                Stderr = guest.Stderr,
                WallClockMs = stopwatch.ElapsedMilliseconds,
                PeakMemoryBytes = guest.PeakMemoryBytes,
            };
        }
        finally
        {
            TryDestroy(process);
            await DrainQuietlyAsync(drainStdout).ConfigureAwait(false);
            await DrainQuietlyAsync(drainStderr).ConfigureAwait(false);
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

    /// <summary>Populates a <see cref="ProcessStartInfo"/> to launch the Firecracker binary with its API socket arguments and redirected output streams.</summary>
    private static void ConfigureStartInfo(ProcessStartInfo startInfo, string binary, string apiSocket, string workingDirectory)
    {
        startInfo.FileName = binary;
        foreach (var arg in FirecrackerArgumentBuilder.BuildFirecrackerArgs(apiSocket))
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
    }

    /// <summary>Polls for the given Unix socket file to appear on disk, using a monotonic stopwatch so a system clock change cannot skew the timeout, and always checking at least once even if the timeout budget is already zero.</summary>
    private static async Task WaitForSocketAsync(string socketPath, string description, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Stopwatch (monotonic) rather than DateTime.UtcNow: a system clock adjustment (NTP, VM clock
        // sync) must not make this loop run longer or shorter than intended.
        //
        // do/while, not while: timeout is derived from the run's remaining wall-clock budget (see
        // RemainingBudget) and can legitimately be TimeSpan.Zero if earlier setup consumed it all. A plain
        // while-loop would then skip File.Exists entirely and always throw - even if the socket already
        // exists. At least one check must always happen before the timeout applies to retries.
        var waitStopwatch = Stopwatch.StartNew();
        do
        {
            if (File.Exists(socketPath))
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        while (waitStopwatch.Elapsed < timeout);

        throw new TimeoutException($"{description} '{socketPath}' did not appear within {timeout}.");
    }

    /// <summary>
    /// The lesser of <paramref name="phaseCeilingMs"/> and however much of <paramref name="totalBudgetMs"/>
    /// remains on <paramref name="stopwatch"/> - never negative. Used so no individual phase (socket
    /// waits, guest exec) can push the run's total wall clock past its configured ceiling, regardless of
    /// how much of that ceiling earlier phases already consumed.
    /// </summary>
    private static TimeSpan RemainingBudget(Stopwatch stopwatch, int totalBudgetMs, int phaseCeilingMs)
    {
        var remainingMs = totalBudgetMs - stopwatch.ElapsedMilliseconds;
        var boundedMs = Math.Min(remainingMs, phaseCeilingMs);
        return boundedMs > 0 ? TimeSpan.FromMilliseconds(boundedMs) : TimeSpan.Zero;
    }

    /// <summary>Kills the Firecracker process tree to tear down the guest kernel and its state, treating an already-exited process as a non-error.</summary>
    private void TryDestroy(Process process)
    {
        try
        {
            // Destroying the Firecracker process tears down the guest kernel and all its state.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Firecracker process already exited before teardown.");
        }
    }
}

