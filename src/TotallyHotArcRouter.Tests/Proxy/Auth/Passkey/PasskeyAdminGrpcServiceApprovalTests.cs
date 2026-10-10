using System.Text.Json;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Sessions.Export;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Covers the browser hand-off RPCs of <see cref="PasskeyAdminGrpcService"/> (#165 phase 3, PR 3c; ADR-0020 Amendment
/// 1): a command files a request, the dashboard approves it through a ceremony bound to the request's own digest, and
/// the command collects a one-operation authorization that runs that export and no other.
/// </summary>
public sealed class PasskeyAdminGrpcServiceApprovalTests
{
    private const string Destination = @"C:\exports\conversations.zip";

    private sealed class Fixture
    {
        public Fixture(bool enrolled = true)
        {
            Harness = PasskeyGateHarness.Create(enrolled);
            Pending = new PendingApprovalTable(new Uri("https://localhost:47104"));
            Log = new PasskeyApprovalLog();
            var ceremonies = new FakeWebAuthnCeremonyService(new ChallengeStore(Harness.Options), Harness.Credentials);
            Service = new PasskeyAdminGrpcService(
                Harness.Gate,
                new EnrollmentCodeService(
                    new ProtectedSecretStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat"))),
                ceremonies,
                Harness.Grants,
                Harness.OneOperations,
                Log,
                Harness.Credentials,
                Harness.Options,
                Pending);
        }

        public PasskeyGateHarness Harness { get; }

        public PendingApprovalTable Pending { get; }

        public PasskeyApprovalLog Log { get; }

        public PasskeyAdminGrpcService Service { get; }

        public async Task<string> CreateAsync(string destination = Destination, string? model = "gpt-5")
        {
            var details = new Contract.ExportApprovalDetails { DestinationPath = destination };
            if (model is not null) details.Model = model;
            var response = await Service.CreateApprovalRequest(
                new Contract.CreateApprovalRequestRequest { Export = details }, Ctx());
            return response.ApprovalId;
        }

        public async Task ApproveAsync(string approvalId)
        {
            var options = await Service.BeginApproval(new Contract.BeginApprovalRequest { ApprovalId = approvalId }, Ctx());
            await Service.FinishApproval(
                new Contract.FinishApprovalRequest
                {
                    ApprovalId = approvalId,
                    AssertionJson = JsonSerializer.Serialize(new { challenge = Challenge(options.OptionsJson) }),
                },
                Ctx());
        }
    }

    private static ServerCallContext Ctx(CancellationToken cancellationToken = default) =>
        PasskeyGateHarness.Context(null, cancellationToken);

    private static string Challenge(string optionsJson)
    {
        using var doc = JsonDocument.Parse(optionsJson);
        return doc.RootElement.GetProperty("challenge").GetString()!;
    }

    private static ConversationExportFilter Filter(string? model = "gpt-5") => new(Model: model);

    [Fact]
    public async Task CreateApprovalRequest_ReturnsTheIdAndADashboardLinkToThatRequest()
    {
        var f = new Fixture();

        var response = await f.Service.CreateApprovalRequest(
            new Contract.CreateApprovalRequestRequest
            {
                Export = new Contract.ExportApprovalDetails { DestinationPath = Destination, Model = "gpt-5" },
            },
            Ctx());

        response.ApprovalId.Should().NotBeNullOrEmpty();
        response.DashboardUrl.Should().Be($"https://localhost:47104/?approval={response.ApprovalId}");
        response.ExpiresAtUtc.ToDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(4));
        f.Pending.TryGetPending(response.ApprovalId).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateApprovalRequest_WithoutAnEnrolledPasskey_FailsFastWithTheEnrollmentMessage()
    {
        var f = new Fixture(enrolled: false);

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.CreateAsync());

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Status.Detail.Should().Contain(EnrollmentCliCommand.FlagName);
        f.Pending.ListPending().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateApprovalRequest_RejectsARequestWithNoOperationOrNoDestination()
    {
        var f = new Fixture();

        var none = await Assert.ThrowsAsync<RpcException>(() => f.Service.CreateApprovalRequest(
            new Contract.CreateApprovalRequestRequest(), Ctx()));
        var blank = await Assert.ThrowsAsync<RpcException>(() => f.Service.CreateApprovalRequest(
            new Contract.CreateApprovalRequestRequest { Export = new Contract.ExportApprovalDetails { DestinationPath = " " } },
            Ctx()));

        none.StatusCode.Should().Be(StatusCode.InvalidArgument);
        blank.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListPendingApprovals_ShowsTheDestinationAndFilterExactlyAsFiled()
    {
        var f = new Fixture();
        await f.CreateAsync();

        var listed = await f.Service.ListPendingApprovals(new Contract.ListPendingApprovalsRequest(), Ctx());

        var approval = listed.Approvals.Should().ContainSingle().Subject;
        approval.Export.DestinationPath.Should().Be(Destination);
        approval.Export.Model.Should().Be("gpt-5");
        approval.Export.HasHarness.Should().BeFalse();
        approval.ExpiresAtUtc.Should().BeGreaterThan(approval.CreatedAtUtc);
    }

    [Fact]
    public async Task TheHandOff_IssuesAnAuthorizationBoundToThatExportAndNoOtherAndOnlyOnce()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        var waiting = f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx());
        waiting.IsCompleted.Should().BeFalse();

        await f.ApproveAsync(id);
        var response = await waiting;

        response.Outcome.Should().Be(Contract.ApprovalOutcome.Approved);
        response.AuthorizationToken.Should().NotBeNullOrEmpty();
        f.Log.Recent().Should().Contain(e => e.Operation == GatedOperation.Export && e.Outcome == "succeeded");

        // The authorization runs the approved export...
        var otherDestination = () => ContentGateHooks.RequireExport(
            f.Harness.Gate, response.AuthorizationToken, Filter(), @"C:\exports\other.zip");
        otherDestination.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        // ...and a mismatch spent it, so even the approved export cannot reuse it.
        var spent = () => ContentGateHooks.RequireExport(f.Harness.Gate, response.AuthorizationToken, Filter(), Destination);
        spent.Should().Throw<RpcException>();
    }

    [Fact]
    public async Task TheHandOff_AuthorizationRunsTheApprovedExport()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        await f.ApproveAsync(id);

        var response = await f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx());

        var run = () => ContentGateHooks.RequireExport(f.Harness.Gate, response.AuthorizationToken, Filter(), Destination);
        run.Should().NotThrow();
    }

    [Fact]
    public async Task WaitForApproval_ReportsTheOutcomeToOneCallerOnly()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        await f.ApproveAsync(id);
        await f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx());

        var second = await Assert.ThrowsAsync<RpcException>(() =>
            f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx()));

        second.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task WaitForApproval_AfterADenial_ReportsDeniedWithNoAuthorization()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();

        await f.Service.DenyApproval(new Contract.DenyApprovalRequest { ApprovalId = id }, Ctx());
        var response = await f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx());

        response.Outcome.Should().Be(Contract.ApprovalOutcome.Denied);
        response.AuthorizationToken.Should().BeEmpty();
        f.Log.Recent().Should().Contain(e => e.Operation == GatedOperation.Export && e.Outcome == "denied");
    }

    [Fact]
    public async Task WaitForApproval_ForAnUnknownId_IsNotFound()
    {
        var f = new Fixture();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = "nope" }, Ctx()));

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task BeginApproval_BindsTheChallengeToTheRequestNotToAnythingTheDashboardSends()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        var other = await f.CreateAsync(destination: @"C:\exports\other.zip");

        var options = await f.Service.BeginApproval(new Contract.BeginApprovalRequest { ApprovalId = id }, Ctx());
        // A ceremony begun for one request cannot finish another: the router re-derives the binding from the id.
        var wrong = await Assert.ThrowsAsync<RpcException>(() => f.Service.FinishApproval(
            new Contract.FinishApprovalRequest
            {
                ApprovalId = other,
                AssertionJson = JsonSerializer.Serialize(new { challenge = Challenge(options.OptionsJson) }),
            },
            Ctx()));

        wrong.StatusCode.Should().Be(StatusCode.PermissionDenied);
        f.Pending.TryGetPending(other).Should().NotBeNull();
        f.Pending.TryGetPending(id).Should().NotBeNull();
    }

    [Fact]
    public async Task FinishApproval_WithAnAssertionThatFailsVerification_LeavesTheRequestOpenToRetry()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        await f.Service.BeginApproval(new Contract.BeginApprovalRequest { ApprovalId = id }, Ctx());

        var ex = await Assert.ThrowsAsync<RpcException>(() => f.Service.FinishApproval(
            new Contract.FinishApprovalRequest { ApprovalId = id, AssertionJson = "{\"challenge\":\"bogus\"}" }, Ctx()));

        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        f.Pending.TryGetPending(id).Should().NotBeNull();
        f.Log.Recent().Should().Contain(e => e.Outcome == "failed");
    }

    [Fact]
    public async Task ApproveOrDeny_AfterTheRequestWasDecided_IsRefused()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();
        await f.Service.DenyApproval(new Contract.DenyApprovalRequest { ApprovalId = id }, Ctx());

        var begin = await Assert.ThrowsAsync<RpcException>(() =>
            f.Service.BeginApproval(new Contract.BeginApprovalRequest { ApprovalId = id }, Ctx()));
        var deny = await Assert.ThrowsAsync<RpcException>(() =>
            f.Service.DenyApproval(new Contract.DenyApprovalRequest { ApprovalId = id }, Ctx()));

        begin.StatusCode.Should().Be(StatusCode.NotFound);
        deny.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task TheAuthorizationNeverReachesTheDashboard()
    {
        var f = new Fixture();
        var id = await f.CreateAsync();

        await f.ApproveAsync(id);

        // Approving marks the request; the authorization exists only once the command collects it.
        var tokensIssuedBeforeCollection = () => ContentGateHooks.RequireExport(
            f.Harness.Gate, "anything", Filter(), Destination);
        tokensIssuedBeforeCollection.Should().Throw<RpcException>();
        var collected = await f.Service.WaitForApproval(new Contract.WaitForApprovalRequest { ApprovalId = id }, Ctx());
        collected.AuthorizationToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FilingARequestIsNotRecordedAsAnApprovalUntilTheOperatorApproves()
    {
        var f = new Fixture();

        await f.CreateAsync();

        f.Log.Recent().Should().BeEmpty();
    }

    [Fact]
    public void ExportApprovalDetails_RoundTripsThroughTheSharedWireMapping()
    {
        var filter = new ConversationExportFilter(
            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            SessionId: "s-1",
            Harness: "claude-code",
            Provider: "anthropic",
            Model: "opus");

        var wire = ConversationExportWire.ToApprovalDetails(filter, Destination);

        ConversationExportWire.ToFilter(wire).Should().Be(filter);
        wire.DestinationPath.Should().Be(Destination);
        GatedOperation.ExportParameters(ConversationExportWire.ToFilter(wire), wire.DestinationPath)
            .Should().Be(GatedOperation.ExportParameters(filter, Destination));
    }

    [Fact]
    public void ExportApprovalDetails_RejectsAnOutOfRangeTimestamp()
    {
        var details = new Contract.ExportApprovalDetails
        {
            DestinationPath = Destination,
            FromUtc = new Timestamp { Seconds = long.MaxValue },
        };

        var map = () => ConversationExportWire.ToFilter(details);

        map.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
