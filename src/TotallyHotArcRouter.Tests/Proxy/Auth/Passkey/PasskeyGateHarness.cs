using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Wires a real <see cref="ContentGate"/> over in-memory collaborators for tests of the gRPC services that
/// consume it. The ACL probe points at a path that does not exist, which the probe accepts, so the gate is
/// "store protected" without touching the real secret store.
/// </summary>
internal sealed class PasskeyGateHarness
{
    private PasskeyGateHarness(bool enrolled)
    {
        Credentials = new InMemoryPasskeyCredentialStore();
        if (enrolled)
        {
            Credentials.Add(new PasskeyCredentialRecord(
                Id: [1, 2, 3],
                PublicKey: [4, 5, 6],
                SignCount: 0,
                Name: "test-key",
                CreatedAtUtc: DateTimeOffset.UtcNow,
                BackupEligible: false,
                BackupState: false));
        }

        Options = new PasskeyOptions();
        Grants = new ContentGrantTable(Options);
        OneOperations = new OneOperationAuthorizationTable();
        Gate = new ContentGate(new SecretStoreAclProbe(), Credentials, Grants, OneOperations);
        Gate.RefreshStoreProtection(secretsPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing"));
    }

    /// <summary>Gets the in-memory credential store behind <see cref="Gate"/>.</summary>
    public InMemoryPasskeyCredentialStore Credentials { get; }

    /// <summary>Gets the gate options.</summary>
    public PasskeyOptions Options { get; }

    /// <summary>Gets the content grant table behind <see cref="Gate"/>.</summary>
    public ContentGrantTable Grants { get; }

    /// <summary>Gets the one-operation authorization table behind <see cref="Gate"/>.</summary>
    public OneOperationAuthorizationTable OneOperations { get; }

    /// <summary>Gets the gate under test.</summary>
    public ContentGate Gate { get; }

    /// <summary>Creates a harness, optionally with one passkey already enrolled.</summary>
    public static PasskeyGateHarness Create(bool enrolled = true) => new(enrolled);

    /// <summary>Builds a server call context carrying <paramref name="grantToken"/> as the content-grant header.</summary>
    public static ServerCallContext Context(string? grantToken = null, CancellationToken cancellationToken = default)
    {
        var headers = new Metadata();
        if (grantToken is not null) headers.Add(ContentGate.ContentGrantHeaderName, grantToken);

        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: cancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }
}
