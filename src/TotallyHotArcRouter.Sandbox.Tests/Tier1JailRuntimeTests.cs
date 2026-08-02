using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the Tier-1 runtime orchestration with a fake pool and launcher (no real jail).</summary>
public class Tier1JailRuntimeTests
{
    private sealed class FakeLease : ISandboxLease
    {
        public FakeLease()
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "acrouter-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
        }

        public SandboxTier Tier => SandboxTier.Tier1Jail;

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
        public SandboxTier Tier => SandboxTier.Tier1Jail;

        public ValueTask<ISandboxLease> LeaseAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISandboxLease>(lease);
    }

    private sealed class FakeLauncher(ExecutionOutcome outcome) : IJailLauncher
    {
        public JailSpec? CapturedSpec { get; private set; }

        public bool ScriptExistedAtLaunch { get; private set; }

        public Task<ExecutionOutcome> RunAsync(JailSpec spec, CancellationToken cancellationToken)
        {
            CapturedSpec = spec;
            ScriptExistedAtLaunch = File.Exists(Path.Combine(spec.WorkingDirectory, spec.ScriptFileName));
            return Task.FromResult(outcome);
        }
    }

    private static SandboxRequest Request() =>
        new("print('hi')", SandboxLanguage.Python, "code_generation", "gpt-5.4", "corr", "sess");

    [Fact]
    public async Task ExecuteAsync_WritesScriptAndRunsLauncher()
    {
        var lease = new FakeLease();
        var launcher = new FakeLauncher(new ExecutionOutcome { ExitCode = 0, WallClockMs = 5 });
        var options = new SandboxOptions { MaxWallClockMs = 2500, MemoryMaxBytes = 99, PidsMax = 32 };
        var runtime = new Tier1JailRuntime(
            [new FakePool(lease)],
            launcher,
            Options.Create(options),
            NullLogger<Tier1JailRuntime>.Instance);

        var outcome = await runtime.ExecuteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(launcher.ScriptExistedAtLaunch);
        Assert.NotNull(launcher.CapturedSpec);
        Assert.Equal("python3", launcher.CapturedSpec!.Interpreter);
        Assert.Equal(2500, launcher.CapturedSpec.TimeoutMs);
        Assert.Equal("99", launcher.CapturedSpec.MemoryMax);
        Assert.Equal("32", launcher.CapturedSpec.PidsMax);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Tier_IsTier1Jail()
    {
        var runtime = new Tier1JailRuntime(
            [new FakePool(new FakeLease())],
            new FakeLauncher(new ExecutionOutcome()),
            Options.Create(new SandboxOptions()),
            NullLogger<Tier1JailRuntime>.Instance);

        Assert.Equal(SandboxTier.Tier1Jail, runtime.Tier);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedLanguage_ReturnsDefensiveOutcomeWithoutLeasingOrLaunching()
    {
        var lease = new FakeLease();
        var launcher = new FakeLauncher(new ExecutionOutcome { ExitCode = 0 });
        var runtime = new Tier1JailRuntime(
            [new FakePool(lease)],
            launcher,
            Options.Create(new SandboxOptions()),
            NullLogger<Tier1JailRuntime>.Instance);

        // C# has no Tier-1 (subprocess-interpreter) runtime - the tier selector should never route it
        // here, so this exercises the defensive guard rather than a real production path.
        var request = new SandboxRequest("class C {}", SandboxLanguage.CSharp, "code_generation", "gpt-5.4", "corr", "sess");

        var outcome = await runtime.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains("No Tier-1 runtime", outcome.Stderr);
        Assert.Null(launcher.CapturedSpec);
        Assert.False(lease.Disposed, "The pool must not be leased when the guard short-circuits.");
    }

    [Fact]
    public void Constructor_WithoutTier1Pool_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new Tier1JailRuntime(
            [],
            new FakeLauncher(new ExecutionOutcome()),
            Options.Create(new SandboxOptions()),
            NullLogger<Tier1JailRuntime>.Instance));
    }
}

