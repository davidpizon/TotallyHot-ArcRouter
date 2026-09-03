using Microsoft.Extensions.Options;
using Moq;
using System.Runtime.InteropServices;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProviderConfigStore"/>'s one-time migration of an already-persisted locked literal
/// provider header into the protected secret store (<c>docs/router/secrets-at-rest-plan.md</c> §5).
/// Windows-only, since the store itself refuses to write elsewhere - <see cref="Constructor_NoSecretWriter_LeavesTheLiteralInPlace"/>
/// covers the platform-agnostic non-migrating path.
/// </summary>
public sealed class SecretHeaderMigrationTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"model-routing-{Guid.NewGuid():N}.json");
    private readonly string _secretStorePath = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "secrets.dat");

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static ModelRoutingOptions OptionsWithLockedLiteral() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new ProviderOptions
            {
                BaseUrl = "https://api.anthropic.com",
                AuthHeaderName = "x-api-key",
                Headers =
                [
                    new ProviderHeader { Name = "x-api-key", Value = "sk-ant-secret", Locked = true },
                    new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01", Locked = false }
                ]
            }
        },
        ModelList = [new ModelRouteEntry { ModelName = "claude", Provider = "anthropic", ProviderModelId = "claude" }]
    };

    private ProviderConfigStore CreateStore(ISecretWriter? secretWriter) => new(
        Mock.Of<Microsoft.Extensions.Logging.ILogger<ProviderConfigStore>>(),
        Options.Create(new ModelRoutingOptions()),
        Options.Create(new ProviderConfigStoreOptions { FilePath = _tempPath }),
        secretWriter);

    [Fact]
    public void Constructor_ExistingFileWithLockedLiteral_MigratesItToTheStore()
    {
        if (!IsWindows)
        {
            return;
        }

        File.WriteAllText(_tempPath, JsonSerializer.Serialize(OptionsWithLockedLiteral(), new JsonSerializerOptions { WriteIndented = true }));
        var secretStore = new ProtectedSecretStore(_secretStorePath);

        var store = CreateStore(secretStore);

        var headers = store.Snapshot.Options.Providers["anthropic"].Headers;
        var apiKeyHeader = headers.Single(h => h.Name == "x-api-key");
        Assert.Null(apiKeyHeader.Value);
        Assert.NotNull(apiKeyHeader.ValueSecretRef);
        Assert.True(secretStore.TryRead(apiKeyHeader.ValueSecretRef!, out var migratedValue));
        Assert.Equal("sk-ant-secret", migratedValue);

        // The unlocked public header must be left exactly as it was.
        var versionHeader = headers.Single(h => h.Name == "anthropic-version");
        Assert.Equal("2023-06-01", versionHeader.Value);
        Assert.Null(versionHeader.ValueSecretRef);

        // The migration must have persisted: model-routing.json itself no longer holds the plaintext key.
        var onDisk = File.ReadAllText(_tempPath);
        Assert.DoesNotContain("sk-ant-secret", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_CalledTwice_IsIdempotent()
    {
        if (!IsWindows)
        {
            return;
        }

        File.WriteAllText(_tempPath, JsonSerializer.Serialize(OptionsWithLockedLiteral(), new JsonSerializerOptions { WriteIndented = true }));
        var secretStore = new ProtectedSecretStore(_secretStorePath);

        var first = CreateStore(secretStore);
        var refAfterFirst = first.Snapshot.Options.Providers["anthropic"].Headers.Single(h => h.Name == "x-api-key").ValueSecretRef;

        // A second store instance loads the now-migrated file - Value is already null, so nothing to migrate.
        var second = CreateStore(secretStore);
        var refAfterSecond = second.Snapshot.Options.Providers["anthropic"].Headers.Single(h => h.Name == "x-api-key").ValueSecretRef;

        Assert.Equal(refAfterFirst, refAfterSecond);
        Assert.True(secretStore.TryRead(refAfterSecond!, out var value));
        Assert.Equal("sk-ant-secret", value);
    }

    [Fact]
    public void Constructor_NoSecretWriter_LeavesTheLiteralInPlace()
    {
        File.WriteAllText(_tempPath, JsonSerializer.Serialize(OptionsWithLockedLiteral(), new JsonSerializerOptions { WriteIndented = true }));

        var store = CreateStore(secretWriter: null);

        var apiKeyHeader = store.Snapshot.Options.Providers["anthropic"].Headers.Single(h => h.Name == "x-api-key");
        Assert.Equal("sk-ant-secret", apiKeyHeader.Value);
        Assert.Null(apiKeyHeader.ValueSecretRef);
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }

        var secretStoreDirectory = Path.GetDirectoryName(_secretStorePath);
        if (secretStoreDirectory is not null && Directory.Exists(secretStoreDirectory))
        {
            Directory.Delete(secretStoreDirectory, recursive: true);
        }
    }
}
