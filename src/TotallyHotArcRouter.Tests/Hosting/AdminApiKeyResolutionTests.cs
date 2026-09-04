using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey"/> and
/// <see cref="PriceCatalogServiceCollectionExtensions.BuildCostReconcilers"/>: the stored-secret-first, then-env-var
/// resolution order for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §7).
/// </summary>
public sealed class AdminApiKeyResolutionTests
{
    [Fact]
    public void TryResolveAdminApiKey_StoredSecretPresent_PrefersStoreOverEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new Dictionary<string, ProviderReconciliationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new() { AdminApiKeyEnvVar = "ANTHROPIC_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>(MockBehavior.Strict);
        var secretReader = new Mock<ISecretReader>();
        var stored = "sk-ant-admin-from-store";
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out stored)).Returns(true);

        var resolved = PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey(
            options: options, environment: environment.Object, secretReader: secretReader.Object, provider: "anthropic",
            adminApiKey: out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal(expected: "sk-ant-admin-from-store", actual: adminApiKey);
        // Strict mock: if the resolver had consulted the environment, this test would throw instead of
        // reaching here - proving the store takes priority.
    }

    [Fact]
    public void TryResolveAdminApiKey_NoStoredSecret_FallsBackToEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new Dictionary<string, ProviderReconciliationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { AdminApiKeyEnvVar = "OPENAI_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("OPENAI_ADMIN_KEY")).Returns("sk-openai-from-env");
        var secretReader = new Mock<ISecretReader>();
        var empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:openai:admin-key", out empty)).Returns(false);

        var resolved = PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey(
            options: options, environment: environment.Object, secretReader: secretReader.Object, provider: "openai",
            adminApiKey: out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal(expected: "sk-openai-from-env", actual: adminApiKey);
    }

    [Fact]
    public void TryResolveAdminApiKey_NoSecretReaderSupplied_FallsBackToEnvVar()
    {
        var options = new CostReconciliationOptions
        {
            Providers = new Dictionary<string, ProviderReconciliationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { AdminApiKeyEnvVar = "OPENAI_ADMIN_KEY" }
            }
        };
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("OPENAI_ADMIN_KEY")).Returns("sk-openai-from-env");

        var resolved = PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey(
            options: options, environment: environment.Object, null, provider: "openai",
            adminApiKey: out var adminApiKey);

        Assert.True(resolved);
        Assert.Equal(expected: "sk-openai-from-env", actual: adminApiKey);
    }

    [Fact]
    public void TryResolveAdminApiKey_NeitherStoredNorEnvVarConfigured_ReturnsFalse()
    {
        var options = new CostReconciliationOptions();
        var environment = new Mock<IEnvironmentVariableProvider>();
        var secretReader = new Mock<ISecretReader>();
        var empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out empty)).Returns(false);

        var resolved = PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey(
            options: options, environment: environment.Object, secretReader: secretReader.Object, provider: "anthropic",
            adminApiKey: out var adminApiKey);

        Assert.False(resolved);
        Assert.Equal(expected: string.Empty, actual: adminApiKey);
    }

    [Fact]
    public void BuildCostReconcilers_NoKeysResolvable_ReturnsEmpty()
    {
        var reconcilers = PriceCatalogServiceCollectionExtensions.BuildCostReconcilers(BuildServiceProvider(
            options: new CostReconciliationOptions(), null));

        Assert.Empty(reconcilers);
    }

    [Fact]
    public void BuildCostReconcilers_StoredAnthropicAdminKey_AddsAnthropicReconciler_WithNoEnvVarConfigured()
    {
        var secretReader = new Mock<ISecretReader>();
        var stored = "sk-ant-admin-from-store";
        secretReader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out stored)).Returns(true);
        var empty = string.Empty;
        secretReader.Setup(r => r.TryRead("reconciliation:openai:admin-key", out empty)).Returns(false);

        var reconcilers = PriceCatalogServiceCollectionExtensions.BuildCostReconcilers(BuildServiceProvider(
            options: new CostReconciliationOptions(), secretReader: secretReader.Object));

        var reconciler = Assert.Single(reconcilers);
        Assert.Equal(expected: "anthropic", actual: reconciler.Provider);
    }

    private static IServiceProvider BuildServiceProvider(CostReconciliationOptions options, ISecretReader? secretReader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Mock.Of<IEnvironmentVariableProvider>());
        services.AddSingleton<HttpClient>();
        if (secretReader is not null) services.AddSingleton(secretReader);

        return services.BuildServiceProvider();
    }
}