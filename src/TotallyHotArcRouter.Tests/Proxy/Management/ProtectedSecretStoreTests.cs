using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProtectedSecretStore"/>: round-tripping, deletion, prefix cascade, the
/// pluggable-protector contract (web GUI migration plan Phase P3 - DPAPI on Windows, ASP.NET Core Data
/// Protection everywhere else), and ADR-0015's machine-shared protection - the ACL the store is written
/// with, the legacy per-user blob it still reads, and the quarantine path that keeps an undecryptable
/// store from failing host startup.
/// </summary>
/// <remarks>
/// The non-Windows behavior (key-ring creation/permission-refusal, the versioned on-disk format, restart
/// survival) was additionally verified for real on a Linux container (not merely reasoned about) during
/// Phase P3's implementation - see the plan doc's P3 status section for that run's output. The
/// <c>[UnsupportedOSPlatform("windows")]</c>-guarded portions of <see cref="ProtectedSecretStore"/> itself
/// cannot be meaningfully unit-tested from a Windows test run (there is no way to make
/// <c>OperatingSystem.IsWindows()</c> lie), so this file's own coverage is necessarily Windows-only where
/// it touches the store's internals, and platform-agnostic where it only touches the public contract.
/// <para>
/// One part of ADR-0015 is deliberately not asserted here: that the blob really carries DPAPI's
/// <c>LocalMachine</c> scope rather than the per-user one. Windows exposes no managed way to read a blob's
/// scope back - <c>Unprotect</c> takes the scope as an argument but reads the real one out of the blob and
/// largely ignores what it was passed - and the only behavioral difference is whether a <em>different</em>
/// OS account can decrypt it, which a single-account test process cannot exercise. What is asserted here is
/// the ACL that admits that other account, plus the data-preserving migration; the scope itself was
/// verified end to end by the installed service, running as <c>LocalSystem</c>, reading a store written by
/// an interactive user (see ADR-0015's verification note).
/// </para>
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
    /// Web GUI migration plan Phase P3's headline exit criterion, and since ADR-0015 also the read half of
    /// the machine-scope migration: a <c>secrets.dat</c> sealed with the per-user scope this store used to
    /// write must still read correctly for the user who wrote it. Writes that legacy shape directly,
    /// bypassing <see cref="ProtectedSecretStore"/> entirely, so it fails if <c>UnprotectWindows</c> ever
    /// loses its fallback to <see cref="DataProtectionScope.CurrentUser"/> and starts quarantining stores
    /// it could in fact have read.
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

    /// <summary>
    /// ADR-0015's regression test, and the one that would have caught the installer failure it fixes: the
    /// store must be readable by <c>LocalSystem</c>, because that is the account the installed service runs
    /// as. Before ADR-0015 the store was written through <c>SecureFile.WriteRestricted</c>, whose protected
    /// DACL held exactly one rule granting the creating user - so a store created by a developer running
    /// the router directly locked the service out, the host failed to start with
    /// <see cref="UnauthorizedAccessException"/>, and the MSI reported only "Service ... failed to start.
    /// Verify that you have sufficient privileges".
    /// </summary>
    /// <remarks>
    /// Asserts through the public <see cref="ProtectedSecretStore.Write"/> API rather than calling
    /// <c>SecureFile</c> directly, so it also covers the part that made the ACL easy to get wrong: the store
    /// writes a temp file and renames it over the real path, and the assertion only holds because a move
    /// within one volume carries the explicit DACL with it.
    /// </remarks>
    [Fact]
    public void Write_ProtectsTheStoreForLocalSystemAndAdministratorsOnly()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            new ProtectedSecretStore(path).Write(name: "management:token", value: "token-value");

            var rules = new FileInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            // Inheritance is broken, so these rules are the file's complete access list - had any inherited
            // rule survived, "no Users access" below would be vacuous rather than a real boundary.
            Assert.All(rules, rule => Assert.False(rule.IsInherited));

            var localSystem = new SecurityIdentifier(sidType: WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(sidType: WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(sidType: WellKnownSidType.BuiltinUsersSid, null);

            Assert.Contains(rules, rule => rule.IdentityReference.Equals(localSystem)
                                           && rule.AccessControlType == AccessControlType.Allow
                                           && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            Assert.Contains(rules, rule => rule.IdentityReference.Equals(administrators)
                                           && rule.AccessControlType == AccessControlType.Allow
                                           && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));

            // The tightening half of ADR-0015: WriteMachineShared used to grant Users read so the
            // interactive-user GUI could read the shared management token, which ADR-0012's loopback session
            // cookie made unnecessary. Any local account is admin-adjacent for this file otherwise, since
            // LocalMachine-scoped DPAPI lets whoever can read the bytes decrypt them.
            Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(users));
        }
        finally
        {
            CleanUp(path);
        }
    }

    /// <summary>
    /// The write half of ADR-0015's migration: a legacy per-user store is re-sealed in place on the next
    /// write, preserving every secret already in it rather than starting over. Together with
    /// <see cref="ExistingPreP3DpapiFile_StillReads"/> this is what makes the scope change require no
    /// migration step of its own - the store converts itself the first time anything writes to it.
    /// </summary>
    [Fact]
    public void LegacyPerUserStore_IsRewrittenInPlace_PreservingExistingSecrets()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var legacy = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["router-ca:cert-password"] = "ca-password"
            };
            var entropy = "TotallyHotArcRouter.ProtectedSecretStore.v1"u8.ToArray();
            File.WriteAllBytes(path: path, bytes: ProtectedData.Protect(
                userData: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy),
                optionalEntropy: entropy,
                scope: DataProtectionScope.CurrentUser));

            var store = new ProtectedSecretStore(path);
            store.Write(name: "management:token", value: "token-value");

            // The pre-existing secret survived the re-seal - losing it would orphan the .pfx it decrypts.
            Assert.True(store.TryRead(name: "router-ca:cert-password", value: out var caPassword));
            Assert.Equal(expected: "ca-password", actual: caPassword);
            Assert.True(store.TryRead(name: "management:token", value: out var token));
            Assert.Equal(expected: "token-value", actual: token);

            // Re-sealed through the store's own write path, so it now carries the machine-shared ACL and is
            // no longer a file only its original author can open.
            Assert.DoesNotContain(
                new FileInfo(path).GetAccessControl()
                    .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(),
                rule => rule.IdentityReference.Equals(new SecurityIdentifier(sidType: WellKnownSidType.BuiltinUsersSid, null)));

            // And no quarantine file was produced: this store was readable, so setting it aside would have
            // been a silent loss of the very secrets asserted above.
            Assert.Empty(Directory.GetFiles(path: Path.GetDirectoryName(path)!, searchPattern: "*.unreadable-*"));
        }
        finally
        {
            CleanUp(path);
        }
    }

    /// <summary>
    /// A store this process cannot decrypt reads as empty and is moved aside, rather than throwing out of
    /// <see cref="ProtectedSecretStore.TryRead"/>. This is the defect that turned a recoverable
    /// configuration problem into a failed install: the exception escaped into host startup, the service
    /// process aborted, and the SCM surfaced only a privileges message.
    /// </summary>
    /// <remarks>
    /// Uses bytes that are not a DPAPI blob at all, which is the same failure mode
    /// (<see cref="CryptographicException"/> from <c>Unprotect</c>) as a real store sealed under another
    /// account's key, and the only one a single-account test process can actually produce.
    /// </remarks>
    [Fact]
    public void UndecryptableStore_ReadsAsEmpty_AndIsMovedAside()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path: path, bytes: "not a DPAPI blob"u8.ToArray());

            var store = new ProtectedSecretStore(path);

            Assert.False(store.TryRead(name: "management:token", value: out _));
            Assert.False(File.Exists(path));

            var quarantined = Assert.Single(Directory.GetFiles(path: directory, searchPattern: "*.unreadable-*"));
            Assert.Equal(expected: "not a DPAPI blob"u8.ToArray(), actual: File.ReadAllBytes(quarantined));

            // Still usable afterwards: the host regenerates what it needs instead of failing to start.
            store.Write(name: "management:token", value: "regenerated");
            Assert.True(store.TryRead(name: "management:token", value: out var token));
            Assert.Equal(expected: "regenerated", actual: token);
        }
        finally
        {
            CleanUp(path);
        }
    }

    /// <summary>
    /// A second quarantine must not overwrite the first. The quarantined file is the only remaining copy of
    /// secrets this account cannot read but their author may still be able to, so two decrypt failures in
    /// quick succession - which a crash-looping service produces by definition, and which a
    /// seconds-resolution name plus <c>overwrite: true</c> would collapse onto one path - have to leave two
    /// files behind, not one.
    /// </summary>
    [Fact]
    public void TwoUndecryptableStoresInSuccession_AreBothPreserved()
    {
        if (!IsWindows) return;

        var path = TempStorePath();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var store = new ProtectedSecretStore(path);

            File.WriteAllBytes(path: path, bytes: "first unreadable store"u8.ToArray());
            Assert.False(store.TryRead(name: "management:token", value: out _));

            File.WriteAllBytes(path: path, bytes: "second unreadable store"u8.ToArray());
            Assert.False(store.TryRead(name: "management:token", value: out _));

            var quarantined = Directory.GetFiles(path: directory, searchPattern: "*.unreadable-*");

            Assert.Equal(expected: 2, actual: quarantined.Length);
            Assert.Equal(
                expected: new[] { "first unreadable store", "second unreadable store" },
                actual: quarantined.Select(File.ReadAllText).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            CleanUp(path);
        }
    }
}