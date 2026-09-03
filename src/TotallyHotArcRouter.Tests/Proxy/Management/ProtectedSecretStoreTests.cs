using System.Runtime.InteropServices;
using System.Text;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProtectedSecretStore"/>: round-tripping, deletion, prefix cascade, and the
/// non-Windows refuse-rather-than-degrade contract (<c>docs/router/secrets-at-rest-plan.md</c> §3).
/// </summary>
public sealed class ProtectedSecretStoreTests
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

    [Fact]
    public void TryRead_RoundTripsAWrittenValue()
    {
        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            if (!IsWindows)
            {
                Assert.Throws<PlatformNotSupportedException>(() => store.Write(name: "name", value: "value"));
                return;
            }

            store.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-secret");

            Assert.True(store.TryRead(name: "provider:anthropic:header:x-api-key", value: out var value));
            Assert.Equal(expected: "sk-ant-secret", actual: value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Write_Overwrite_ReplacesTheValue()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            store.Write(name: "name", value: "first");
            store.Write(name: "name", value: "second");

            Assert.True(store.TryRead(name: "name", value: out var value));
            Assert.Equal(expected: "second", actual: value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Delete_RemovesTheEntry_AndReportsWhetherOneExisted()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write(name: "name", value: "value");

            Assert.True(store.Delete("name"));
            Assert.False(store.TryRead(name: "name", value: out _));
            Assert.False(store.Delete("name"));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void DeleteByPrefix_RemovesOnlyMatchingEntries()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write(name: "provider:anthropic:header:x-api-key", value: "a");
            store.Write(name: "provider:anthropic:header:anthropic-version", value: "b");
            store.Write(name: "provider:openai:header:authorization", value: "c");

            var removed = store.DeleteByPrefix("provider:anthropic:");

            Assert.Equal(2, actual: removed);
            Assert.False(store.Exists("provider:anthropic:header:x-api-key"));
            Assert.False(store.Exists("provider:anthropic:header:anthropic-version"));
            Assert.True(store.Exists("provider:openai:header:authorization"));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void TryRead_MissingName_ReturnsFalse()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write(name: "other", value: "value");

            Assert.False(store.TryRead(name: "missing", value: out var value));
            Assert.Equal(expected: string.Empty, actual: value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void OnDiskBytes_ContainNeitherThePlaintextValueNorTheName()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write(name: "provider:anthropic:header:x-api-key", value: "sk-ant-super-secret-value");

            var bytes = File.ReadAllBytes(path);
            var asText = Encoding.UTF8.GetString(bytes);

            Assert.DoesNotContain(expectedSubstring: "sk-ant-super-secret-value", actualString: asText,
                comparisonType: StringComparison.Ordinal);
            Assert.DoesNotContain(expectedSubstring: "x-api-key", actualString: asText,
                comparisonType: StringComparison.Ordinal);
            Assert.DoesNotContain(expectedSubstring: "anthropic", actualString: asText,
                comparisonType: StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public async Task ConcurrentWrites_FromTwoStoreInstances_LoseNoEntries()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var tasks = Enumerable.Range(0, 16)
                .Select(i => Task.Run(() =>
                {
                    var store = new ProtectedSecretStore(path);
                    store.Write(name: $"name-{i}", value: $"value-{i}");
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            var reader = new ProtectedSecretStore(path);
            for (var i = 0; i < 16; i++)
            {
                Assert.True(condition: reader.TryRead(name: $"name-{i}", value: out var value),
                    userMessage: $"Expected name-{i} to survive concurrent writes.");
                Assert.Equal(expected: $"value-{i}", actual: value);
            }
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void OnNonWindows_WriteThrows_AndReadsReportAbsent()
    {
        if (IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            Assert.Throws<PlatformNotSupportedException>(() => store.Write(name: "name", value: "value"));
            Assert.False(store.TryRead(name: "name", value: out _));
            Assert.False(store.Exists("name"));
            Assert.False(store.Delete("name"));
            Assert.Equal(0, actual: store.DeleteByPrefix("name"));
        }
        finally
        {
            CleanUp(path);
        }
    }
}