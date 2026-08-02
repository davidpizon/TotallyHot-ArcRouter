using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Firecracker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers Tier-2 runtime orchestration and the snapshot pool (OS-agnostic parts, via fakes).</summary>
public class Tier2MicroVmRuntimeTests
{
    private sealed class FakeLease : ISandboxLease
    {
        public FakeLease()
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "acrouter-vm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
        }

        public SandboxTier Tier => SandboxTier.Tier2MicroVm;

        public string WorkingDirectory { get; }

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            try
            {
                if (Directory.Exists(WorkingDirectory))
                {
                    Directory.Delete(WorkingDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Ignore in tests.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePool(FakeLease lease) : ISandboxPool
    {
        public SandboxTier Tier => SandboxTier.Tier2MicroVm;

        public ValueTask<ISandboxLease> LeaseAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISandboxLease>(lease);
    }

    private sealed class FakeLauncher(ExecutionOutcome outcome) : IMicroVmLauncher
    {
        public MicroVmSpec? CapturedSpec { get; private set; }

        public Task<ExecutionOutcome> RunAsync(MicroVmSpec spec, CancellationToken cancellationToken)
        {
            CapturedSpec = spec;
            return Task.FromResult(outcome);
        }
    }

    private static SandboxRequest Request() =>
        new("print('hi')", SandboxLanguage.Python, "algorithm_design", "gpt-5.4", "corr", "sess");

    [Fact]
    public async Task ExecuteAsync_RunsLauncherWithLeaseDirectory()
    {
        var lease = new FakeLease();
        var launcher = new FakeLauncher(new ExecutionOutcome { ExitCode = 0 });
        var options = new SandboxOptions { MaxWallClockMs = 4000, MemoryMaxBytes = 256 };
        var runtime = new Tier2MicroVmRuntime(
            [new FakePool(lease)],
            launcher,
            Options.Create(options),
            NullLogger<Tier2MicroVmRuntime>.Instance);

        var outcome = await runtime.ExecuteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.NotNull(launcher.CapturedSpec);
        Assert.Equal(lease.WorkingDirectory, launcher.CapturedSpec!.WorkingDirectory);
        Assert.Equal(4000, launcher.CapturedSpec.TimeoutMs);
        Assert.Equal(256, launcher.CapturedSpec.MemoryMaxBytes);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Tier_IsTier2MicroVm()
    {
        var runtime = new Tier2MicroVmRuntime(
            [new FakePool(new FakeLease())],
            new FakeLauncher(new ExecutionOutcome()),
            Options.Create(new SandboxOptions()),
            NullLogger<Tier2MicroVmRuntime>.Instance);

        Assert.Equal(SandboxTier.Tier2MicroVm, runtime.Tier);
    }

    [Fact]
    public void Constructor_WithoutTier2Pool_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new Tier2MicroVmRuntime(
            [],
            new FakeLauncher(new ExecutionOutcome()),
            Options.Create(new SandboxOptions()),
            NullLogger<Tier2MicroVmRuntime>.Instance));
    }

    [Fact]
    public async Task Pool_LeaseCreatesAndResetsDirectory()
    {
        using var pool = new FirecrackerSnapshotPool(
            Options.Create(new SandboxOptions { WarmPool = new WarmPoolOptions { Tier2 = 1 } }),
            NullLogger<FirecrackerSnapshotPool>.Instance);

        var lease = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var dir = lease.WorkingDirectory;
        Assert.True(Directory.Exists(dir));
        Assert.Equal(SandboxTier.Tier2MicroVm, lease.Tier);

        await lease.DisposeAsync();
        Assert.False(Directory.Exists(dir));
    }
}

