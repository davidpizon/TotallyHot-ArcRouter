using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

public sealed class ContentGateTests
{
    [Fact]
    public void EnsureEnrolled_WithoutPasskeys_ThrowsFailedPrecondition()
    {
        var gate = CreateGate(storeProtected: true, enrolled: false);

        var ex = Assert.Throws<RpcException>(gate.EnsureEnrolled);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains(EnrollmentCliCommand.FlagName, ex.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireContentGrant_WithValidHeader_DoesNotThrow()
    {
        var grants = new ContentGrantTable(new PasskeyOptions());
        var gate = CreateGate(storeProtected: true, enrolled: true, grants);
        var token = grants.IssueGrant();
        var context = BuildContext(ContentGate.ContentGrantHeaderName, token);

        gate.RequireContentGrant(context);
    }

    [Fact]
    public void RequireAndConsumeOneOperation_ConsumesMatchingToken()
    {
        var oneOp = new OneOperationAuthorizationTable();
        var gate = CreateGate(storeProtected: true, enrolled: true, oneOp: oneOp);
        var token = oneOp.Issue(GatedOperation.GetManagementToken, GatedOperation.GetManagementTokenParameters());

        gate.RequireAndConsumeOneOperation(token, GatedOperation.GetManagementToken,
            GatedOperation.GetManagementTokenParameters());
    }

    private static ContentGate CreateGate(
        bool storeProtected,
        bool enrolled,
        ContentGrantTable? contentGrants = null,
        OneOperationAuthorizationTable? oneOp = null)
    {
        var credentials = new InMemoryPasskeyCredentialStore();
        if (enrolled)
        {
            credentials.Add(new PasskeyCredentialRecord(
                Id: [1],
                PublicKey: [2],
                SignCount: 0,
                Name: "test",
                CreatedAtUtc: DateTimeOffset.UtcNow,
                BackupEligible: false,
                BackupState: false));
        }

        var gate = new ContentGate(
            aclProbe: new SecretStoreAclProbe(),
            credentialStore: credentials,
            contentGrants: contentGrants ?? new ContentGrantTable(new PasskeyOptions()),
            oneOperationAuthorizations: oneOp ?? new OneOperationAuthorizationTable());
        if (storeProtected) gate.RefreshStoreProtection(secretsPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing"));
        return gate;
    }

    private static ServerCallContext BuildContext(string headerName, string value)
    {
        var headers = new Metadata { { headerName, value } };
        return new FakeServerCallContext(headers);
    }

    private sealed class FakeServerCallContext(Metadata headers) : ServerCallContext
    {
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override string MethodCore => "test";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "127.0.0.1";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => headers;

        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        protected override Metadata ResponseTrailersCore => [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore =>
            new(string.Empty, []);
    }
}
