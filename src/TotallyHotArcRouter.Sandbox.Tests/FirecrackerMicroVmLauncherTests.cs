using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Firecracker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="FirecrackerMicroVmLauncher"/>'s orchestration - most importantly, that machine sizing
/// is applied before the snapshot restores, and that a rejected sizing call is fatal rather than silently
/// ignored. Uses a real launched process (a stand-in "Firecracker" shell script) with a faked
/// <see cref="IFirecrackerClient"/>, since no KVM device is available in CI/sandboxes to run the real
/// Firecracker binary.
/// </summary>
/// <remarks>
/// Marked <see cref="SupportedOSPlatformAttribute"/>("linux") to satisfy CA1416 for every call site in
/// this class, including the one inside <c>Assert.ThrowsAsync</c>'s lambda in
/// <see cref="RunAsync_MachineConfigRejected_PropagatesAndNeverLoadsSnapshot"/> - the analyzer's
/// guard-clause recognition (<c>if (!OperatingSystem.IsLinux()) return;</c>) does not flow through a
/// lambda closure, only through the enclosing method's own control flow. The per-method runtime guards
/// are kept regardless, since this attribute is compile-time-only and does not stop the test from being
/// discovered/executed by xUnit on a non-Linux dev machine.
/// </remarks>
[Trait("Category", "Integration")]
[SupportedOSPlatform("linux")]
public sealed class FirecrackerMicroVmLauncherTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(), "acrouter-fc-test-" + Guid.NewGuid().ToString("N"));

    private readonly string _scriptPath;

    public FirecrackerMicroVmLauncherTests()
    {
        Directory.CreateDirectory(_workingDirectory);
        _scriptPath = Path.Combine(_workingDirectory, "fake-firecracker.sh");

        // Stands in for the real firecracker binary: touches the api-sock path it's given (so
        // WaitForSocketAsync's File.Exists check succeeds) and then idles until torn down. The real REST
        // exchange never happens over this socket - IFirecrackerClient is faked below - so the file only
        // needs to exist, not actually serve anything.
        File.WriteAllText(_scriptPath, "#!/bin/sh\ntouch \"$2\"\nsleep 30\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                _scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    private sealed class FakeFirecrackerClient : IFirecrackerClient
    {
        public List<string> Calls { get; } = [];

        public string? MachineConfigPayload { get; private set; }

        public string? VsockConfigPayload { get; private set; }

        public bool ThrowOnConfigureMachine { get; set; }

        public bool CreateVsockUdsOnSnapshotLoad { get; set; } = true;

        public int ConfigureMachineDelayMs { get; set; }

        public async Task ConfigureMachineAsync(string payload, CancellationToken cancellationToken)
        {
            Calls.Add("ConfigureMachine");
            MachineConfigPayload = payload;

            if (ConfigureMachineDelayMs > 0)
            {
                await Task.Delay(ConfigureMachineDelayMs, cancellationToken).ConfigureAwait(false);
            }

            if (ThrowOnConfigureMachine)
            {
                throw new HttpRequestException("machine-config rejected: sizing mismatch");
            }
        }

        public Task ConfigureVsockAsync(string payload, CancellationToken cancellationToken)
        {
            Calls.Add("ConfigureVsock");
            VsockConfigPayload = payload;
            return Task.CompletedTask;
        }

        public Task LoadSnapshotAsync(string payload, CancellationToken cancellationToken)
        {
            Calls.Add("LoadSnapshot");

            // Mirrors real Firecracker: the vsock UDS is created once the guest actually resumes, not at
            // PUT /vsock config time - so this fake creates the file here (after the already-recorded
            // config call), letting the launcher's post-LoadSnapshot wait find it exactly as it would in
            // production. CreateVsockUdsOnSnapshotLoad lets a test simulate Firecracker never creating it.
            if (CreateVsockUdsOnSnapshotLoad && VsockConfigPayload is not null)
            {
                using var doc = JsonDocument.Parse(VsockConfigPayload);
                var udsPath = doc.RootElement.GetProperty("uds_path").GetString();
                if (udsPath is not null)
                {
                    File.WriteAllText(udsPath, string.Empty);
                }
            }

            return Task.CompletedTask;
        }

        public Task SetVmStateAsync(string payload, CancellationToken cancellationToken)
        {
            Calls.Add("SetVmState");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeFirecrackerClientFactory(FakeFirecrackerClient client) : IFirecrackerClientFactory
    {
        public string? RequestedSocketPath { get; private set; }

        public IFirecrackerClient Create(string apiSocketPath)
        {
            RequestedSocketPath = apiSocketPath;
            return client;
        }
    }

    private sealed class FakeGuestAgentClient(GuestExecResult result) : IGuestAgentClient
    {
        public Action? OnExecute { get; init; }

        public int ExecuteDelayMs { get; init; }

        public async Task<GuestExecResult> ExecuteAsync(
            string vsockUdsPath,
            uint port,
            SandboxLanguage language,
            string code,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            OnExecute?.Invoke();
            if (ExecuteDelayMs > 0)
            {
                await Task.Delay(ExecuteDelayMs, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private SandboxOptions BuildOptions(int vcpuCount = 2, long memoryMaxBytes = 268435456) => new()
    {
        MemoryMaxBytes = memoryMaxBytes,
        Firecracker = new FirecrackerOptions
        {
            BinaryPath = _scriptPath,
            SnapshotPath = "snapshot.bin",
            MemorySnapshotPath = "memory.bin",
            VcpuCount = vcpuCount,
            // The fakes create their socket files synchronously (well within either ceiling), so small
            // values here don't affect the passing-path tests - they only matter for
            // RunAsync_VsockUdsNeverAppears_ThrowsTimeoutBeforeGuestExec, which would otherwise burn the
            // full production default (5s) waiting for a file that test deliberately never creates.
            ApiSocketWaitMs = 200,
            VsockSocketWaitMs = 200,
        },
    };

    private MicroVmSpec BuildSpec(long memoryMaxBytes = 268435456) => new()
    {
        Language = SandboxLanguage.Python,
        Code = "print('hi')",
        WorkingDirectory = _workingDirectory,
        TimeoutMs = 5000,
        MemoryMaxBytes = memoryMaxBytes,
    };

    [Fact]
    public async Task RunAsync_ConfiguresMachineBeforeLoadingSnapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fakeClient = new FakeFirecrackerClient();
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, "hi", string.Empty, TimedOut: false, PeakMemoryBytes: 4096));
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions(vcpuCount: 2, memoryMaxBytes: 268435456)), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        var outcome = await launcher.RunAsync(BuildSpec(memoryMaxBytes: 268435456), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ConfigureMachine", "ConfigureVsock", "LoadSnapshot" }, fakeClient.Calls);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("hi", outcome.Stdout);
        Assert.False(outcome.TimedOut);
        Assert.Equal(Path.Combine(_workingDirectory, "firecracker-api.sock"), factory.RequestedSocketPath);

        using var doc = JsonDocument.Parse(fakeClient.MachineConfigPayload!);
        Assert.Equal(2, doc.RootElement.GetProperty("vcpu_count").GetInt32());
        Assert.Equal(256, doc.RootElement.GetProperty("mem_size_mib").GetInt64());
    }

    [Fact]
    public async Task RunAsync_ConfiguresVsockBeforeLoadingSnapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fakeClient = new FakeFirecrackerClient();
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, "hi", string.Empty, TimedOut: false, PeakMemoryBytes: 0));
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions()), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        await launcher.RunAsync(BuildSpec(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ConfigureMachine", "ConfigureVsock", "LoadSnapshot" }, fakeClient.Calls);

        using var doc = JsonDocument.Parse(fakeClient.VsockConfigPayload!);
        Assert.Equal(3u, doc.RootElement.GetProperty("guest_cid").GetUInt32());
        Assert.Equal(Path.Combine(_workingDirectory, "firecracker-vsock.sock"), doc.RootElement.GetProperty("uds_path").GetString());
    }

    [Fact]
    public async Task RunAsync_VsockUdsNeverAppears_ThrowsTimeoutBeforeGuestExec()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Simulates Firecracker accepting the vsock config but never actually creating the UDS on
        // resume (e.g. a guest kernel that fails to bring up its vsock device) - the launcher must not
        // hang or silently proceed to a guest-agent call that can never connect.
        var fakeClient = new FakeFirecrackerClient { CreateVsockUdsOnSnapshotLoad = false };
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgentCalled = false;
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, string.Empty, string.Empty, TimedOut: false, PeakMemoryBytes: 0))
        {
            OnExecute = () => guestAgentCalled = true,
        };
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions()), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => launcher.RunAsync(BuildSpec(), TestContext.Current.CancellationToken));

        Assert.False(guestAgentCalled, "The guest agent must not be invoked before the vsock UDS is confirmed to exist.");
    }

    [Fact]
    public async Task RunAsync_SetupConsumesBudget_GuestExecOnlyGetsRemainingTime()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // spec.TimeoutMs (1000ms) is the TOTAL wall-clock budget for setup + guest exec combined, not a
        // fresh allowance handed to guest exec alone. ConfigureMachineAsync deliberately consumes most of
        // it (700ms); the guest agent's own 500ms delay would comfortably fit inside a *fresh* 1000ms
        // budget but must not fit inside the ~300ms actually left - proving execCts.CancelAfter uses the
        // remaining budget, not spec.TimeoutMs verbatim.
        var fakeClient = new FakeFirecrackerClient { ConfigureMachineDelayMs = 700 };
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, "hi", string.Empty, TimedOut: false, PeakMemoryBytes: 0))
        {
            ExecuteDelayMs = 500,
        };
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions()), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        var outcome = await launcher.RunAsync(BuildSpec() with { TimeoutMs = 1000 }, TestContext.Current.CancellationToken);

        Assert.True(outcome.TimedOut, "Guest exec should be cancelled once the run's total wall-clock budget is exhausted, not given a fresh allowance.");
    }

    [Fact]
    public async Task RunAsync_UsesSpecMemoryNotGlobalOptionsMemory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The spec's per-run MemoryMaxBytes must drive the payload, not the global SandboxOptions value -
        // they're deliberately different here to prove which one wins.
        var fakeClient = new FakeFirecrackerClient();
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, string.Empty, string.Empty, TimedOut: false, PeakMemoryBytes: 0));
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions(memoryMaxBytes: 999999999)), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        await launcher.RunAsync(BuildSpec(memoryMaxBytes: 134217728), TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(fakeClient.MachineConfigPayload!);
        Assert.Equal(128, doc.RootElement.GetProperty("mem_size_mib").GetInt64());
    }

    [Fact]
    public async Task RunAsync_MachineConfigRejected_PropagatesAndNeverLoadsSnapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fakeClient = new FakeFirecrackerClient { ThrowOnConfigureMachine = true };
        var factory = new FakeFirecrackerClientFactory(fakeClient);
        var guestAgent = new FakeGuestAgentClient(new GuestExecResult(0, string.Empty, string.Empty, TimedOut: false, PeakMemoryBytes: 0));
        var launcher = new FirecrackerMicroVmLauncher(
            guestAgent, factory, Options.Create(BuildOptions()), NullLogger<FirecrackerMicroVmLauncher>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => launcher.RunAsync(BuildSpec(), TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "ConfigureMachine" }, fakeClient.Calls);
    }
}

