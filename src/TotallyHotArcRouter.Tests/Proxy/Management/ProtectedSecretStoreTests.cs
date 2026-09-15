using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProtectedSecretStore"/>: round-tripping, deletion, prefix cascade, and (web GUI
/// migration plan Phase P3) the pluggable-protector contract - DPAPI on Windows with its on-disk format
/// exactly unchanged from before this phase, and ASP.NET Core Data Protection everywhere else, now that
/// every platform actually works instead of the store refusing to write off Windows.
/// </summary>
/// <remarks>
/// The non-Windows behavior (key-ring creation/permission-refusal, the versioned on-disk format, restart
/// survival) was additionally verified for real on a Linux container (not merely reasoned about) during
/// Phase P3's implementation - see the plan doc's P3 status section for that run's output. The
/// <c>[UnsupportedOSPlatform("windows")]</c>-guarded portions of <see cref="ProtectedSecretStore"/> itself
/// cannot be meaningfully unit-tested from a Windows test run (there is no way to make
/// <c>OperatingSystem.IsWindows()</c> lie), so this file's own coverage is necessarily Windows-only where
/// it touches the store's internals, and platform-agnostic where it only touches the public contract.
/// </remarks>
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

    /// <summary>
    /// Web GUI migration plan Phase P3's headline exit criterion: a <c>secrets.dat</c> written by the
    /// pre-Phase-P3 code (a raw <see cref="ProtectedData"/> blob, no version header - exactly what
    /// <c>SaveMapWindows</c> still produces) must still read correctly. Writes that legacy shape directly,
    /// bypassing <see cref="ProtectedSecretStore"/> entirely, so this test would fail if the Windows format
    /// ever drifted from what it was before this phase.
    /// </summary>
    [Fact]
    public void ExistingPreP3DpapiFile_StillReads()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provider:anthropic:header:x-api-key"] = "sk-ant-legacy-secret"
            };
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(map);
            var entropy = "TotallyHotArcRouter.ProtectedSecretStore.v1"u8.ToArray();
            var legacyBytes = ProtectedData.Protect(userData: json, optionalEntropy: entropy,
                scope: DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path: path, bytes: legacyBytes);

            var store = new ProtectedSecretStore(path);

            Assert.True(store.TryRead(name: "provider:anthropic:header:x-api-key", value: out var value));
            Assert.Equal(expected: "sk-ant-legacy-secret", actual: value);
        }
        finally
        {
            CleanUp(path);
        }
    }

    /// <summary>
    /// Web GUI migration plan Phase P3: off Windows, <see cref="ProtectedSecretStore.Write"/> now succeeds
    /// instead of throwing <see cref="PlatformNotSupportedException"/> - see this class's remarks for why
    /// the full non-Windows contract can only be exercised on a real non-Windows host, and the plan doc's
    /// P3 status section for the real Linux run that did exercise it.
    /// </summary>
    [Fact]
    public void OnNonWindows_WriteSucceeds_AndRoundTrips()
    {
        if (IsWindows) return;

        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            store.Write(name: "name", value: "value");

            Assert.True(store.TryRead(name: "name", value: out var value));
            Assert.Equal(expected: "value", actual: value);
            Assert.True(store.Exists("name"));
            Assert.True(store.Delete("name"));
            Assert.False(store.Exists("name"));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void GetOrAdd_NoExistingValue_InvokesFactoryAndPersistsIt()
    {
        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);

            var value = store.GetOrAdd(name: "name", valueFactory: () => "generated");

            Assert.Equal(expected: "generated", actual: value);
            Assert.True(store.TryRead(name: "name", value: out var stored));
            Assert.Equal(expected: "generated", actual: stored);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void GetOrAdd_ExistingValue_ReturnsItWithoutInvokingTheFactory()
    {
        var path = TempStorePath();
        try
        {
            var store = new ProtectedSecretStore(path);
            store.Write(name: "name", value: "existing");
            var factoryCalled = false;

            var value = store.GetOrAdd(name: "name", valueFactory: () =>
            {
                factoryCalled = true;
                return "generated";
            });

            Assert.Equal(expected: "existing", actual: value);
            Assert.False(factoryCalled);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public async Task GetOrAdd_ConcurrentFirstCalls_AllObserveTheSameValue()
    {
        // The whole reason GetOrAdd exists rather than a caller composing TryRead+Write itself: the
        // check-then-write must be atomic, or two racing callers can both observe "nothing stored yet"
        // and each persist a competing value.
        var path = TempStorePath();
        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => new ProtectedSecretStore(path).GetOrAdd(name: "name", valueFactory: () => $"generated-{i}")))
            .ToArray();
        try
        {
            var values = await Task.WhenAll(tasks);

            Assert.Single(values.Distinct());
        }
        finally
        {
            CleanUp(path);
        }
    }
}