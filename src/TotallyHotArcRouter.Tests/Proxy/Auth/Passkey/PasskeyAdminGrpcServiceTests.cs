using Grpc.Core;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Covers <see cref="PasskeyAdminGrpcService"/> against <see cref="FakeWebAuthnCeremonyService"/>: enrollment
/// behind a single-use code, content unlock issuing a grant, one-operation authorizations bound to their
/// operation, locking, and the approval audit list.
/// </summary>
public sealed class PasskeyAdminGrpcServiceTests
{
    private sealed class Fixture
    {
        public PasskeyGateHarness Harness { get; }

        public EnrollmentCodeService Codes { get; }

        public PasskeyApprovalLog Log { get; } = new();

        public PasskeyAdminGrpcService Service { get; }

        public Fixture(bool enrolled)
        {
            Harness = PasskeyGateHarness.Create(enrolled);
            Codes = new EnrollmentCodeService(
                new ProtectedSecretStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat")));
            var ceremonies = new FakeWebAuthnCeremonyService(
                new ChallengeStore(Harness.Options), Harness.Credentials);
            Service = new PasskeyAdminGrpcService(
                Harness.Gate, Codes, ceremonies, Harness.Grants, Harness.OneOperations, Log,
                Harness.Credentials, Harness.Options, new PendingApprovalTable(new Uri("https://localhost:47104")));
        }
    }

    private static ServerCallContext Ctx(string? grant = null) =>
        PasskeyGateHarness.Context(grant, TestContext.Current.CancellationToken);

    private static string Challenge(string optionsJson)
    {
        using var doc = JsonDocument.Parse(optionsJson);
        return doc.RootElement.GetProperty("challenge").GetString()!;
    }

    private static string Response(string challenge) => JsonSerializer.Serialize(new { challenge });

    [Fact]
    public async Task GetPasskeyGateStatus_ReportsEnrollmentAndGrantState()
    {
        var f = new Fixture(enrolled: true);
        var grant = f.Harness.Grants.IssueGrant();

        var withGrant = await f.Service.GetPasskeyGateStatus(new Contract.GetPasskeyGateStatusRequest(), Ctx(grant));
        var without = await f.Service.GetPasskeyGateStatus(new Contract.GetPasskeyGateStatusRequest(), Ctx());

        Assert.True(withGrant.StoreProtected);
        Assert.True(withGrant.Enrolled);
        Assert.True(withGrant.GrantActive);
        Assert.False(without.GrantActive);
        Assert.Equal(EnrollmentCliCommand.FlagName, without.EnrollmentCommandHint);
    }

    [Fact]
    public async Task Enrollment_WithValidCode_EnrollsOnceAndSpendsTheCode()
    {
        var f = new Fixture(enrolled: false);
        var code = f.Codes.Mint();

        var options = await f.Service.BeginEnrollment(
            new Contract.BeginEnrollmentRequest { EnrollmentCode = code }, Ctx());
        var finished = await f.Service.FinishEnrollment(
            new Contract.FinishEnrollmentRequest { AttestationJson = Response(Challenge(options.OptionsJson)), Name = "laptop" },
            Ctx());

        Assert.Equal("laptop", finished.Credential.Name);
        var listed = await f.Service.ListPasskeys(new Contract.ListPasskeysRequest(), Ctx());
        Assert.Single(listed.Credentials);

        Task<Contract.WebAuthnOptionsResponse> Reuse() =>
            f.Service.BeginEnrollment(new Contract.BeginEnrollmentRequest { EnrollmentCode = code }, Ctx());
        var ex = await Assert.ThrowsAsync<RpcException>(Reuse);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task BeginEnrollment_WithWrongCode_IsPermissionDeniedAndLogged()
    {
        var f = new Fixture(enrolled: false);
        f.Codes.Mint();

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.Service.BeginEnrollment(
            new Contract.BeginEnrollmentRequest { EnrollmentCode = "WRONG-CODE" }, Ctx()));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(f.Log.Recent(), e => e.Operation == "enroll" && e.Outcome == "failed");
    }

    [Fact]
    public async Task BeginEnrollment_WithBlankCode_IsInvalidArgument()
    {
        var f = new Fixture(enrolled: false);

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.Service.BeginEnrollment(
            new Contract.BeginEnrollmentRequest { EnrollmentCode = " " }, Ctx()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ContentUnlock_IssuesAGrantThatTheGateAccepts()
    {
        var f = new Fixture(enrolled: true);

        var options = await f.Service.BeginContentUnlock(new Contract.BeginContentUnlockRequest(), Ctx());
        var grant = await f.Service.FinishContentUnlock(
            new Contract.FinishContentUnlockRequest { AssertionJson = Response(Challenge(options.OptionsJson)) }, Ctx());

        Assert.False(string.IsNullOrEmpty(grant.GrantToken));
        Assert.True(f.Harness.Gate.TryGetContentGrant(Ctx(grant.GrantToken)));
        Assert.Contains(f.Log.Recent(), e => e.Operation == GatedOperation.ContentUnlock && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task ContentUnlock_WithoutEnrollment_IsFailedPrecondition()
    {
        var f = new Fixture(enrolled: false);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            f.Service.BeginContentUnlock(new Contract.BeginContentUnlockRequest(), Ctx()));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task FinishContentUnlock_WithUnknownChallenge_IsPermissionDeniedAndIssuesNoGrant()
    {
        var f = new Fixture(enrolled: true);

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.Service.FinishContentUnlock(
            new Contract.FinishContentUnlockRequest { AssertionJson = Response("AAAA") }, Ctx()));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains(f.Log.Recent(), e => e.Outcome == "failed");
    }

    [Fact]
    public async Task OneOperation_IssuesATokenBoundToItsOperation()
    {
        var f = new Fixture(enrolled: true);
        var op = GatedOperation.GetManagementToken;
        var parameters = GatedOperation.GetManagementTokenParameters();

        var options = await f.Service.BeginOneOperation(
            new Contract.BeginOneOperationRequest { Operation = op, Parameters = parameters }, Ctx());
        var authorization = await f.Service.FinishOneOperation(
            new Contract.FinishOneOperationRequest
            {
                AssertionJson = Response(Challenge(options.OptionsJson)),
                Operation = op,
                Parameters = parameters
            },
            Ctx());

        Assert.False(f.Harness.OneOperations.TryConsume(authorization.AuthorizationToken,
            GatedOperation.RegenerateManagementToken, parameters));
    }

    [Fact]
    public async Task OneOperation_TokenConsumesForItsOwnOperation()
    {
        var f = new Fixture(enrolled: true);
        var op = GatedOperation.RegenerateManagementToken;
        var parameters = GatedOperation.RegenerateManagementTokenParameters();

        var options = await f.Service.BeginOneOperation(
            new Contract.BeginOneOperationRequest { Operation = op, Parameters = parameters }, Ctx());
        var authorization = await f.Service.FinishOneOperation(
            new Contract.FinishOneOperationRequest
            {
                AssertionJson = Response(Challenge(options.OptionsJson)),
                Operation = op,
                Parameters = parameters
            },
            Ctx());

        Assert.True(f.Harness.OneOperations.TryConsume(authorization.AuthorizationToken, op, parameters));
    }

    [Theory]
    [InlineData(GatedOperation.ContentUnlock)]
    [InlineData("not_an_operation")]
    public async Task BeginOneOperation_WithUnsupportedOperation_IsInvalidArgument(string operation)
    {
        var f = new Fixture(enrolled: true);

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.Service.BeginOneOperation(
            new Contract.BeginOneOperationRequest { Operation = operation }, Ctx()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task LockContent_RevokesEveryGrant()
    {
        var f = new Fixture(enrolled: true);
        var grant = f.Harness.Grants.IssueGrant();

        await f.Service.LockContent(new Contract.LockContentRequest(), Ctx(grant));

        Assert.False(f.Harness.Gate.TryGetContentGrant(Ctx(grant)));
        var approvals = await f.Service.ListRecentApprovals(new Contract.ListRecentApprovalsRequest(), Ctx());
        Assert.Contains(approvals.Approvals, a => a.Operation == "lock");
    }
}
