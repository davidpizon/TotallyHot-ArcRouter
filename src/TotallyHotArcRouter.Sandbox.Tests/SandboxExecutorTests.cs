using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Parsing;
using TotallyHot.ArcRouter.Sandbox.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers end-to-end evaluation: Tier-0 fallback, executed path, and seccomp escalation.</summary>
public class SandboxExecutorTests
{
    private sealed class FakeRuntime(SandboxTier tier, ExecutionOutcome outcome) : ISandboxRuntime
    {
        public SandboxTier Tier => tier;

        public int Invocations { get; private set; }

        public Task<ExecutionOutcome> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(outcome);
        }
    }

    private static ISandboxCapabilityProbe Probe(bool execution, bool kvm)
    {
        var mock = new Mock<ISandboxCapabilityProbe>();
        mock.SetupGet(p => p.IsExecutionAvailable).Returns(execution);
        mock.SetupGet(p => p.IsKvmAvailable).Returns(kvm);
        mock.SetupGet(p => p.DegradedReason).Returns(execution ? null : "host-not-linux");
        return mock.Object;
    }

    private static SandboxExecutor CreateExecutor(
        ISandboxCapabilityProbe probe,
        SandboxOptions options,
        params ISandboxRuntime[] runtimes)
    {
        var opts = Options.Create(options);
        return new SandboxExecutor(
            probe,
            new TierSelector(probe, opts),
            new StructuralParser(),
            new VerifierScorer(opts),
            runtimes,
            opts,
            NullLogger<SandboxExecutor>.Instance);
    }

    private static SandboxRequest Request(SandboxLanguage language, string code = "print('hi')") =>
        new(code, language, "code_generation", "gpt-5.4", "corr", "sess");

    [Fact]
    public async Task ExecuteAsync_ExecutionUnavailable_ProducesTier0Result()
    {
        var executor = CreateExecutor(Probe(execution: false, kvm: false), new SandboxOptions());

        var result = await executor.ExecuteAsync(Request(SandboxLanguage.Python), TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal(nameof(SandboxTier.Tier0Static), result.Tier);
        Assert.True(result.SyntaxValid);
        Assert.Equal("host-not-linux", result.DegradedReason);
        Assert.Equal(1.0, result.UnifiedScore);
        Assert.Equal("gpt-5.4", result.Model);
    }

    [Fact]
    public async Task ExecuteAsync_Tier1CleanExit_ProducesExecutedResult()
    {
        var runtime = new FakeRuntime(SandboxTier.Tier1Jail, new ExecutionOutcome { ExitCode = 0, WallClockMs = 12 });
        var options = new SandboxOptions
        {
            DimensionWeights = { ["code_generation"] = new DimensionWeightOptions { Syntax = 0.2, Exec = 0.8 } },
        };
        var executor = CreateExecutor(Probe(execution: true, kvm: false), options, runtime);

        var result = await executor.ExecuteAsync(Request(SandboxLanguage.Python), TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Assert.Equal(nameof(SandboxTier.Tier1Jail), result.Tier);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1.0, result.UnifiedScore);
        Assert.Equal(1, runtime.Invocations);
    }

    [Fact]
    public async Task ExecuteAsync_NoRuntimeForSelectedTier_DegradesToStatic()
    {
        // Execution available and language executable, but no Tier 1 runtime registered.
        var executor = CreateExecutor(Probe(execution: true, kvm: false), new SandboxOptions());

        var result = await executor.ExecuteAsync(Request(SandboxLanguage.Python), TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal("no-runtime-registered", result.DegradedReason);
    }

    private sealed class ThrowingRuntime(SandboxTier tier) : ISandboxRuntime
    {
        public SandboxTier Tier => tier;

        public Task<ExecutionOutcome> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("runtime boom");
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeThrows_FallsBackToTier0()
    {
        var runtime = new ThrowingRuntime(SandboxTier.Tier1Jail);
        var executor = CreateExecutor(Probe(execution: true, kvm: false), new SandboxOptions(), runtime);

        var result = await executor.ExecuteAsync(Request(SandboxLanguage.Python), TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal("tier-execution-failed", result.DegradedReason);
        Assert.True(result.SyntaxValid);
    }

    [Fact]
    public async Task ExecuteAsync_SeccompDenial_EscalatesToTier2()
    {
        var tier1 = new FakeRuntime(SandboxTier.Tier1Jail, new ExecutionOutcome { SeccompDenied = true });
        var tier2 = new FakeRuntime(SandboxTier.Tier2MicroVm, new ExecutionOutcome { ExitCode = 0 });
        var options = new SandboxOptions { EscalateOnSeccompDenial = true };
        var executor = CreateExecutor(Probe(execution: true, kvm: false), options, tier1, tier2);

        var result = await executor.ExecuteAsync(Request(SandboxLanguage.Python), TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Assert.Equal(nameof(SandboxTier.Tier2MicroVm), result.Tier);
        Assert.Equal(1, tier1.Invocations);
        Assert.Equal(1, tier2.Invocations);
    }
}

