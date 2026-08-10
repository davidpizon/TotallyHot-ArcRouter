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

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void TryRead_RoundTripsAWrittenValue()
    {
        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            if (!IsWindows)
            {
                Assert.Throws<PlatformNotSupportedException>(() => store.Write("name", "value"));
                return;
            }

            store.Write("provider:anthropic:header:x-api-key", "sk-ant-secret");

            Assert.True(store.TryRead("provider:anthropic:header:x-api-key", out var value));
            Assert.Equal("sk-ant-secret", value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Write_Overwrite_ReplacesTheValue()
    {
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            store.Write("name", "first");
            store.Write("name", "second");

            Assert.True(store.TryRead("name", out var value));
            Assert.Equal("second", value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Delete_RemovesTheEntry_AndReportsWhetherOneExisted()
    {
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write("name", "value");

            Assert.True(store.Delete("name"));
            Assert.False(store.TryRead("name", out _));
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
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write("provider:anthropic:header:x-api-key", "a");
            store.Write("provider:anthropic:header:anthropic-version", "b");
            store.Write("provider:openai:header:authorization", "c");

            var removed = store.DeleteByPrefix("provider:anthropic:");

            Assert.Equal(2, removed);
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
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write("other", "value");

            Assert.False(store.TryRead("missing", out var value));
            Assert.Equal(string.Empty, value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void OnDiskBytes_ContainNeitherThePlaintextValueNorTheName()
    {
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write("provider:anthropic:header:x-api-key", "sk-ant-super-secret-value");

            var bytes = File.ReadAllBytes(path);
            var asText = Encoding.UTF8.GetString(bytes);

            Assert.DoesNotContain("sk-ant-super-secret-value", asText, StringComparison.Ordinal);
            Assert.DoesNotContain("x-api-key", asText, StringComparison.Ordinal);
            Assert.DoesNotContain("anthropic", asText, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public async Task ConcurrentWrites_FromTwoStoreInstances_LoseNoEntries()
    {
        if (!IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var tasks = Enumerable.Range(0, 16)
                .Select(i => Task.Run(() =>
                {
                    var store = new ProtectedSecretStore(path);
                    store.Write($"name-{i}", $"value-{i}");
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            var reader = new ProtectedSecretStore(path);
            for (var i = 0; i < 16; i++)
            {
                Assert.True(reader.TryRead($"name-{i}", out var value), $"Expected name-{i} to survive concurrent writes.");
                Assert.Equal($"value-{i}", value);
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
        if (IsWindows)
        {
            return;
        }

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            Assert.Throws<PlatformNotSupportedException>(() => store.Write("name", "value"));
            Assert.False(store.TryRead("name", out _));
            Assert.False(store.Exists("name"));
            Assert.False(store.Delete("name"));
            Assert.Equal(0, store.DeleteByPrefix("name"));
        }
        finally
        {
            CleanUp(path);
        }
    }
}
