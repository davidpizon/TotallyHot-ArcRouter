using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using Xunit;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>Covers <see cref="PrintManagementTokenGate"/> refusals for the CLI token print path.</summary>
public sealed class PrintManagementTokenGateTests
{
    /// <summary>With no enrolled passkeys, the CLI must refuse and name enrollment.</summary>
    [Fact]
    public void TryGetRefusalMessage_WhenNoPasskeys_NamesEnrollment()
    {
        var store = new InMemoryPasskeyCredentialStore();
        var message = PrintManagementTokenGate.TryGetRefusalMessage(
            credentialStore: store,
            storeProtected: true);

        Assert.NotNull(message);
        Assert.Contains(EnrollmentCliCommand.FlagName, message, StringComparison.Ordinal);
    }

    /// <summary>With a passkey enrolled, the CLI still refuses until a native ceremony exists.</summary>
    [Fact]
    public void TryGetRefusalMessage_WhenPasskeyEnrolled_SendsOperatorToDashboard()
    {
        var store = new InMemoryPasskeyCredentialStore();
        store.Add(new PasskeyCredentialRecord(
            Id: [1, 2, 3],
            PublicKey: [4, 5, 6],
            SignCount: 0,
            Name: "test",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            BackupEligible: false,
            BackupState: false));

        var message = PrintManagementTokenGate.TryGetRefusalMessage(
            credentialStore: store,
            storeProtected: true);

        Assert.NotNull(message);
        Assert.Contains("dashboard", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unprotected store fails closed before any enrollment check.</summary>
    [Fact]
    public void TryGetRefusalMessage_WhenStoreUnprotected_NamesAcl()
    {
        var store = new InMemoryPasskeyCredentialStore();
        var message = PrintManagementTokenGate.TryGetRefusalMessage(
            credentialStore: store,
            storeProtected: false);

        Assert.NotNull(message);
        Assert.Contains("ACL", message, StringComparison.OrdinalIgnoreCase);
    }
}
