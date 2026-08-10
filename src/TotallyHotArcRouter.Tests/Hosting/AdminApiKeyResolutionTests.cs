using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="ServiceCollectionExtensions.TryResolveAdminApiKey"/> and
/// <see cref="ServiceCollectionExtensions.BuildCostReconcilers"/>: the stored-secret-first, then-env-var
/// resolution order for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §7).
/// </summary>
public sealed class AdminApiKeyResolutionTests
{
    [Fact]
    public void TryResolveAdminApiKey_StoredSecretPresent_PrefersStoreOverEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new ProviderReconciliationOptions { AdminApiKeyEnvVar = "ANTHROPIC_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>(MockBehavior.Strict);
        var secretReader = new Mock<ISecretReader>();
        string stored = "sk-ant-admin-from-store";
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out stored)).Returns(true);

        var resolved = ServiceCollectionExtensions.TryResolveAdminApiKey(
            options, environment.Object, secretReader.Object, "anthropic", out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal("sk-ant-admin-from-store", adminApiKey);
        // Strict mock: if the resolver had consulted the environment, this test would throw instead of
        // reaching here - proving the store takes priority.
    }

    [Fact]
    public void TryResolveAdminApiKey_NoStoredSecret_FallsBackToEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderReconciliationOptions { AdminApiKeyEnvVar = "OPENAI_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("OPENAI_ADMIN_KEY")).Returns("sk-openai-from-env");
        var secretReader = new Mock<ISecretReader>();
        string empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:openai:admin-key", out empty)).Returns(false);

        var resolved = ServiceCollectionExtensions.TryResolveAdminApiKey(
            options, environment.Object, secretReader.Object, "openai", out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal("sk-openai-from-env", adminApiKey);
    }

    [Fact]
    public void TryResolveAdminApiKey_NoSecretReaderSupplied_FallsBackToEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderReconciliationOptions { AdminApiKeyEnvVar = "OPENAI_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("OPENAI_ADMIN_KEY")).Returns("sk-openai-from-env");

        var resolved = ServiceCollectionExtensions.TryResolveAdminApiKey(
            options, environment.Object, secretReader: null, "openai", out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal("sk-openai-from-env", adminApiKey);
    }

    [Fact]
    public void TryResolveAdminApiKey_NeitherStoredNorEnvVarConfigured_ReturnsFalse()
    {
        var options = new CostReconciliationOptions();
        var environment = new Mock<IEnvironmentVariableProvider>();
        var secretReader = new Mock<ISecretReader>();
        string empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out empty)).Returns(false);

        var resolved = ServiceCollectionExtensions.TryResolveAdminApiKey(
            options, environment.Object, secretReader.Object, "anthropic", out var adminApiKey);

        Assert.False(resolved);
        Assert.Equal(string.Empty, adminApiKey);
    }

    [Fact]
    public void BuildCostReconcilers_NoKeysResolvable_ReturnsEmpty()
    {
        var reconcilers = ServiceCollectionExtensions.BuildCostReconcilers(BuildServiceProvider(
            new CostReconciliationOptions(), secretReader: null));

        Assert.Empty(reconcilers);
    }

    [Fact]
    public void BuildCostReconcilers_StoredAnthropicAdminKey_AddsAnthropicReconciler_WithNoEnvVarConfigured()
    {
        var secretReader = new Mock<ISecretReader>();
        string stored = "sk-ant-admin-from-store";
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out stored)).Returns(true);
        string empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:openai:admin-key", out empty)).Returns(false);

        var reconcilers = ServiceCollectionExtensions.BuildCostReconcilers(BuildServiceProvider(
            new CostReconciliationOptions(), secretReader.Object));

        var reconciler = Assert.Single(reconcilers);
        Assert.Equal("anthropic", reconciler.Provider);
    }

    private static IServiceProvider BuildServiceProvider(CostReconciliationOptions options, ISecretReader? secretReader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Mock.Of<IEnvironmentVariableProvider>());
        services.AddSingleton<HttpClient>();
        if (secretReader is not null)
        {
            services.AddSingleton(secretReader);
        }

        return services.BuildServiceProvider();
    }
}
