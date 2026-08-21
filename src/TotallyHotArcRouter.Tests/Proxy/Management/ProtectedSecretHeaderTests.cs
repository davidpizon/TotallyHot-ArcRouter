using System.Runtime.InteropServices;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade"/>'s Phase 2 behavior (<c>docs/router/secrets-at-rest-plan.md</c>
/// §5): a locked literal header write moves into the <see cref="ProtectedSecretStore"/> rather than
/// <c>model-routing.json</c>, is reported as <see cref="HeaderValueSource.Protected"/>, and is cleaned up
/// on removal/unlock/source-switch/drop. Windows-only, since the store itself refuses to write elsewhere
/// (<see cref="ProtectedSecretStoreTests"/> covers that refusal directly).
/// </summary>
public sealed class ProtectedSecretHeaderTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string TempStorePath() =>
        Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "secrets.dat");

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelRoutingOptions SeedOptions(ProviderHeader header) => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new ProviderOptions
            {
                BaseUrl = "https://api.anthropic.com",
                AuthHeaderName = "x-api-key",
                Headers = [header]
            }
        },
        ModelList = [new ModelRouteEntry { ModelName = "claude", Provider = "anthropic", ProviderModelId = "claude" }]
    };

    private static ManagementFacade CreateFacade(IProviderConfigStore store, ProtectedSecretStore secretStore) =>
        new(store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(),
            new ManagementFacadeDependencies { SecretWriter = secretStore, SecretReader = secretStore });

    [Fact]
    public async Task UpsertProviderAsync_LockedLiteralHeader_IsStoredProtectedNotLiteral()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            var configStore = new InMemoryProviderConfigStore(SeedOptions(
                new ProviderHeader { Name = "x-api-key", Value = null }));
            var facade = CreateFacade(configStore, secretStore);

            var result = await facade.UpsertProviderAsync(
                "anthropic",
                new ProviderWriteRequest(
                    "https://api.anthropic.com",
                    "x-api-key",
                    [new HeaderWriteRequest("x-api-key", "sk-ant-secret", null, Locked: true)]),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);

            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Null(storedHeader.Value);
            Assert.NotNull(storedHeader.ValueSecretRef);
            Assert.True(storedHeader.Locked);

            Assert.True(secretStore.TryRead(storedHeader.ValueSecretRef!, out var storedValue));
            Assert.Equal("sk-ant-secret", storedValue);

            var view = Assert.Single(Assert.Single(result.Value!.Providers).Headers);
            Assert.Equal(HeaderValueSource.Protected, view.Source);
            Assert.Null(view.Value);
            Assert.True(view.Locked);
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_BlankWriteOnAProtectedHeader_PreservesTheReference()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(configStore, secretStore);

            // Blank literal + blank envVar + Locked omitted (defaults true) - the preserve-on-blank path.
            var result = await facade.UpsertProviderAsync(
                "anthropic",
                new ProviderWriteRequest(
                    "https://api.anthropic.com",
                    "x-api-key",
                    [new HeaderWriteRequest("x-api-key", null, null)]),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Equal("provider:anthropic:header:x-api-key", storedHeader.ValueSecretRef);
            Assert.True(storedHeader.Locked);
            Assert.True(secretStore.TryRead("provider:anthropic:header:x-api-key", out var stillThere));
            Assert.Equal("sk-ant-secret", stillThere);
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_UnlockingAProtectedHeader_DeletesTheSecretAndClearsTheReference()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(configStore, secretStore);

            var result = await facade.UpsertProviderAsync(
                "anthropic",
                new ProviderWriteRequest(
                    "https://api.anthropic.com",
                    "x-api-key",
                    [new HeaderWriteRequest("x-api-key", null, null, Locked: false)]),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Null(storedHeader.ValueSecretRef);
            Assert.False(storedHeader.Locked);
            Assert.False(secretStore.TryRead("provider:anthropic:header:x-api-key", out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_SwitchingAProtectedHeaderToEnvVar_DeletesTheOldSecret()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(configStore, secretStore);

            var result = await facade.UpsertProviderAsync(
                "anthropic",
                new ProviderWriteRequest(
                    "https://api.anthropic.com",
                    "x-api-key",
                    [new HeaderWriteRequest("x-api-key", null, "ANTHROPIC_API_KEY")]),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Equal("ANTHROPIC_API_KEY", storedHeader.ValueEnvVar);
            Assert.Null(storedHeader.ValueSecretRef);
            Assert.False(secretStore.TryRead("provider:anthropic:header:x-api-key", out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_DroppingAProtectedHeaderEntirely_DeletesTheOldSecret()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(configStore, secretStore);

            // The full header set no longer includes x-api-key at all.
            var result = await facade.UpsertProviderAsync(
                "anthropic",
                new ProviderWriteRequest("https://api.anthropic.com", "x-api-key", []),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Empty(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.False(secretStore.TryRead("provider:anthropic:header:x-api-key", out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task RemoveProviderAsync_CascadesDeleteOfEveryStoredSecretForThatProvider()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");
            secretStore.Write("provider:openai:header:authorization", "sk-openai-secret");

            var configStore = new InMemoryProviderConfigStore(new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic"] = new ProviderOptions
                    {
                        BaseUrl = "https://api.anthropic.com",
                        Headers = [new ProviderHeader { Name = "x-api-key", ValueSecretRef = "provider:anthropic:header:x-api-key", Locked = true }]
                    },
                    ["openai"] = new ProviderOptions
                    {
                        BaseUrl = "https://api.openai.com",
                        Headers = [new ProviderHeader { Name = "authorization", ValueSecretRef = "provider:openai:header:authorization", Locked = true }]
                    }
                }
            });
            var facade = CreateFacade(configStore, secretStore);

            var result = await facade.RemoveProviderAsync("anthropic", TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.False(secretStore.TryRead("provider:anthropic:header:x-api-key", out _));
            // The other provider's secret is untouched - only its own prefix was deleted.
            Assert.True(secretStore.TryRead("provider:openai:header:authorization", out var stillThere));
            Assert.Equal("sk-openai-secret", stillThere);
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task ListProviders_WholeResponseJson_NeverContainsAProtectedHeaderValue()
    {
        if (!IsWindows)
        {
            return;
        }

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write("provider:anthropic:header:x-api-key", "sk-ant-super-secret-marker");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(configStore, secretStore);

            var response = facade.ListProviders();
            var json = JsonSerializer.Serialize(response);

            Assert.DoesNotContain("sk-ant-super-secret-marker", json, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(storePath);
        }
    }
}
