using TotallyHot.ArcRouter.Sandbox.Capability;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Extraction;
using TotallyHot.ArcRouter.Sandbox.Firecracker;
using TotallyHot.ArcRouter.Sandbox.Ingress;
using TotallyHot.ArcRouter.Sandbox.Parsing;
using TotallyHot.ArcRouter.Sandbox.Scoring;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TotallyHot.ArcRouter.Sandbox.DependencyInjection;

/// <summary>
/// Registration helpers for the sandboxed executor. The host application calls <see cref="AddSandbox"/>
/// after registering its own <see cref="IRouterScoreObserver"/> adapter.
/// </summary>
public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sandbox executor, its Tier-0 pipeline, the bounded work queue, the ingress façade,
    /// and the background execution service. Binds <see cref="SandboxOptions"/> from the
    /// <c>Sandbox</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSandbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SandboxOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(SandboxOptions.SectionName).Bind(options));

        services.TryAddSingleton<ISandboxHostFacts, SystemSandboxHostFacts>();
        services.TryAddSingleton<ISandboxCapabilityProbe, SandboxCapabilityProbe>();
        services.TryAddSingleton<IStructuralParser, StructuralParser>();
        services.TryAddSingleton<IVerifierScorer, VerifierScorer>();
        services.TryAddSingleton<IDimensionInferrer, KeywordDimensionInferrer>();
        services.TryAddSingleton<ISignalExtractor, CodeBlockSignalExtractor>();
        services.TryAddSingleton<ITierSelector, TierSelector>();
        services.TryAddSingleton<ISandboxExecutor, SandboxExecutor>();
        services.TryAddSingleton<ISandboxQueue, SandboxWorkQueue>();
        services.TryAddSingleton<ISandboxIngress, SandboxIngress>();

        // Safe default when the host has not supplied its own observer (e.g. tests).
        services.TryAddSingleton<IRouterScoreObserver, NullRouterScoreObserver>();

        // Tier-1 native jail is Linux-only. The OperatingSystem.IsLinux() guard both prevents constructing
        // the platform-specific launcher off Linux and satisfies the platform-compatibility analyzer.
        if (OperatingSystem.IsLinux())
        {
            services.TryAddSingleton<CgroupManager>();
            services.TryAddSingleton<IJailLauncher, LinuxJailLauncher>();
            services.AddSingleton<ISandboxPool, NativeJailPool>();
            services.AddSingleton<ISandboxRuntime, Tier1JailRuntime>();

            // Tier-2 Firecracker requires KVM hardware; register on its presence. The actual binary is
            // resolved at runtime by the launcher via FirecrackerLocator.Find(options.Firecracker.BinaryPath),
            // so a configured (non-PATH) BinaryPath is honored; if it can't be found or the snapshot isn't
            // configured, the executor falls back to the Tier-0 signal rather than losing the request.
            if (File.Exists("/dev/kvm"))
            {
                services.TryAddSingleton<IGuestAgentClient, VsockGuestAgentClient>();
                services.TryAddSingleton<IFirecrackerClientFactory, FirecrackerClientFactory>();
                services.TryAddSingleton<IMicroVmLauncher, FirecrackerMicroVmLauncher>();
                services.AddSingleton<ISandboxPool, FirecrackerSnapshotPool>();
                services.AddSingleton<ISandboxRuntime, Tier2MicroVmRuntime>();
            }
        }

        services.AddHostedService<SandboxExecutionService>();

        return services;
    }
}

