using Moq;
using System.Runtime.InteropServices;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;

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

    private static string TempStorePath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"),
            path4: "secrets.dat");
    }

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(path: directory, true);
    }

    private static ModelRoutingOptions SeedOptions(ProviderHeader header)
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    AuthHeaderName = "x-api-key",
                    Headers = [header]
                }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "claude", Provider = "anthropic", ProviderModelId = "claude" }
            ]
        };
    }

    private static ManagementFacade CreateFacade(IProviderConfigStore store, ProtectedSecretStore secretStore)
    {
        return new ManagementFacade(store: store, environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies { SecretWriter = secretStore, SecretReader = secretStore });
    }

    [Fact]
    public async Task UpsertProviderAsync_LockedLiteralHeader_IsStoredProtectedNotLiteral()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            var configStore = new InMemoryProviderConfigStore(SeedOptions(
                new ProviderHeader { Name = "x-api-key", Value = null }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            var result = await facade.UpsertProviderAsync(
                key: "anthropic",
                request: new ProviderWriteRequest(
                    BaseUrl: "https://api.anthropic.com",
                    AuthHeaderName: "x-api-key",
                    Headers: [new HeaderWriteRequest(Name: "x-api-key", Value: "sk-ant-secret", null, true)]),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);

            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Null(storedHeader.Value);
            Assert.NotNull(storedHeader.ValueSecretRef);
            Assert.True(storedHeader.Locked);

            Assert.True(secretStore.TryRead(name: storedHeader.ValueSecretRef!, value: out var storedValue));
            Assert.Equal(expected: "sk-ant-secret", actual: storedValue);

            var view = Assert.Single(Assert.Single(result.Value!.Providers).Headers);
            Assert.Equal(expected: HeaderValueSource.Protected, actual: view.Source);
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
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            // Blank literal + blank envVar + Locked omitted (defaults true) - the preserve-on-blank path.
            var result = await facade.UpsertProviderAsync(
                key: "anthropic",
                request: new ProviderWriteRequest(
                    BaseUrl: "https://api.anthropic.com",
                    AuthHeaderName: "x-api-key",
                    Headers: [new HeaderWriteRequest(Name: "x-api-key", null, null)]),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Equal(expected: "provider:anthropic:header:x-api-key", actual: storedHeader.ValueSecretRef);
            Assert.True(storedHeader.Locked);
            Assert.True(secretStore.TryRead(name: "provider:anthropic:header:x-api-key", value: out var stillThere));
            Assert.Equal(expected: "sk-ant-secret", actual: stillThere);
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_UnlockingAProtectedHeader_DeletesTheSecretAndClearsTheReference()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            var result = await facade.UpsertProviderAsync(
                key: "anthropic",
                request: new ProviderWriteRequest(
                    BaseUrl: "https://api.anthropic.com",
                    AuthHeaderName: "x-api-key",
                    Headers: [new HeaderWriteRequest(Name: "x-api-key", null, null, false)]),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Null(storedHeader.ValueSecretRef);
            Assert.False(storedHeader.Locked);
            Assert.False(secretStore.TryRead(name: "provider:anthropic:header:x-api-key", value: out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_SwitchingAProtectedHeaderToEnvVar_DeletesTheOldSecret()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            var result = await facade.UpsertProviderAsync(
                key: "anthropic",
                request: new ProviderWriteRequest(
                    BaseUrl: "https://api.anthropic.com",
                    AuthHeaderName: "x-api-key",
                    Headers: [new HeaderWriteRequest(Name: "x-api-key", null, ValueEnvVar: "ANTHROPIC_API_KEY")]),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var storedHeader = Assert.Single(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.Equal(expected: "ANTHROPIC_API_KEY", actual: storedHeader.ValueEnvVar);
            Assert.Null(storedHeader.ValueSecretRef);
            Assert.False(secretStore.TryRead(name: "provider:anthropic:header:x-api-key", value: out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task UpsertProviderAsync_DroppingAProtectedHeaderEntirely_DeletesTheOldSecret()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                Value = null,
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            // The full header set no longer includes x-api-key at all.
            var result = await facade.UpsertProviderAsync(
                key: "anthropic",
                request: new ProviderWriteRequest(BaseUrl: "https://api.anthropic.com", AuthHeaderName: "x-api-key",
                    Headers: []),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Empty(configStore.Snapshot.Options.Providers["anthropic"].Headers);
            Assert.False(secretStore.TryRead(name: "provider:anthropic:header:x-api-key", value: out _));
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task RemoveProviderAsync_CascadesDeleteOfEveryStoredSecretForThatProvider()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");
            secretStore.Write(name: "provider:openai:header:authorization", value: "sk-openai-secret");

            var configStore = new InMemoryProviderConfigStore(new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic"] = new()
                    {
                        BaseUrl = "https://api.anthropic.com",
                        Headers =
                        [
                            new ProviderHeader
                            {
                                Name = "x-api-key", ValueSecretRef = "provider:anthropic:header:x-api-key",
                                Locked = true
                            }
                        ]
                    },
                    ["openai"] = new()
                    {
                        BaseUrl = "https://api.openai.com",
                        Headers =
                        [
                            new ProviderHeader
                            {
                                Name = "authorization", ValueSecretRef = "provider:openai:header:authorization",
                                Locked = true
                            }
                        ]
                    }
                }
            });
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            var result = await facade.RemoveProviderAsync(key: "anthropic",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.False(secretStore.TryRead(name: "provider:anthropic:header:x-api-key", value: out _));
            // The other provider's secret is untouched - only its own prefix was deleted.
            Assert.True(secretStore.TryRead(name: "provider:openai:header:authorization", value: out var stillThere));
            Assert.Equal(expected: "sk-openai-secret", actual: stillThere);
        }
        finally
        {
            CleanUp(storePath);
        }
    }

    [Fact]
    public async Task ListProviders_WholeResponseJson_NeverContainsAProtectedHeaderValue()
    {
        if (!IsWindows) return;

        var storePath = TempStorePath();
        try
        {
            var secretStore = new ProtectedSecretStore(storePath);
            secretStore.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-super-secret-marker");
            var configStore = new InMemoryProviderConfigStore(SeedOptions(new ProviderHeader
            {
                Name = "x-api-key",
                ValueSecretRef = "provider:anthropic:header:x-api-key",
                Locked = true
            }));
            var facade = CreateFacade(store: configStore, secretStore: secretStore);

            var response = facade.ListProviders();
            var json = JsonSerializer.Serialize(response);

            Assert.DoesNotContain(expectedSubstring: "sk-ant-super-secret-marker", actualString: json,
                comparisonType: StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(storePath);
        }
    }
}