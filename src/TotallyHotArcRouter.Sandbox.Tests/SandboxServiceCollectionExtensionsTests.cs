using TotallyHot.ArcRouter.Sandbox.Capability;
using TotallyHot.ArcRouter.Sandbox.DependencyInjection;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Extraction;
using TotallyHot.ArcRouter.Sandbox.Firecracker;
using TotallyHot.ArcRouter.Sandbox.Ingress;
using TotallyHot.ArcRouter.Sandbox.Parsing;
using TotallyHot.ArcRouter.Sandbox.Scoring;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="SandboxServiceCollectionExtensions.AddSandbox"/>'s registration surface. Only the
/// OS-agnostic base pipeline is exercised end to end here - the Tier-1/Tier-2 branches are guarded by
/// <c>OperatingSystem.IsLinux()</c> and a KVM device probe, so on this Windows dev machine they resolve to
/// "not registered" by design, which this test also asserts rather than skipping.
/// </summary>
public class SandboxServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSandbox_RejectsNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => SandboxServiceCollectionExtensions.AddSandbox(null!));
    }

    [Fact]
    public void AddSandbox_RegistersBasePipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSandbox();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SystemSandboxHostFacts>(provider.GetRequiredService<ISandboxHostFacts>());
        Assert.IsType<SandboxCapabilityProbe>(provider.GetRequiredService<ISandboxCapabilityProbe>());
        Assert.IsType<StructuralParser>(provider.GetRequiredService<IStructuralParser>());
        Assert.IsType<VerifierScorer>(provider.GetRequiredService<IVerifierScorer>());
        Assert.IsType<KeywordDimensionInferrer>(provider.GetRequiredService<IDimensionInferrer>());
        Assert.IsType<CodeBlockSignalExtractor>(provider.GetRequiredService<ISignalExtractor>());
        Assert.IsType<TierSelector>(provider.GetRequiredService<ITierSelector>());
        Assert.IsType<SandboxExecutor>(provider.GetRequiredService<ISandboxExecutor>());
        Assert.IsType<SandboxWorkQueue>(provider.GetRequiredService<ISandboxQueue>());
        Assert.IsType<SandboxIngress>(provider.GetRequiredService<ISandboxIngress>());
        Assert.IsType<NullRouterScoreObserver>(provider.GetRequiredService<IRouterScoreObserver>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is SandboxExecutionService);
    }

    [Fact]
    public void AddSandbox_HonorsPreRegisteredObserver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var custom = new NullRouterScoreObserver();
        services.AddSingleton<IRouterScoreObserver>(custom);

        services.AddSandbox();
        using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IRouterScoreObserver>());
    }

    [Fact]
    public void AddSandbox_DoesNotRegisterLinuxOnlyServicesOffLinux()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSandbox();
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IJailLauncher>());
        Assert.Null(provider.GetService<IGuestAgentClient>());
        Assert.Null(provider.GetService<IFirecrackerClientFactory>());
        Assert.Empty(provider.GetServices<ISandboxRuntime>());
        Assert.Empty(provider.GetServices<ISandboxPool>());
    }
}

