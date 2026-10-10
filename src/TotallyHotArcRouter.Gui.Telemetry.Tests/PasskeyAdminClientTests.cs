using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="PasskeyAdminClient"/> - the wire-to-view mapping, the request fields each RPC sends, and
/// error translation behind ADR-0020's dashboard passkey flows. Driven through a subclassed generated stub
/// rather than a live server, mirroring <c>CostReconciliationAdminClientTests</c>.
/// </summary>
public class PasskeyAdminClientTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 7, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetGateStatusAsync_maps_every_field()
    {
        var expires = Created.AddMinutes(15);
        var stub = new StubClient
        {
            Status = new Contract.PasskeyGateStatusResponse
            {
                StoreProtected = true,
                Enrolled = true,
                GrantActive = true,
                GrantExpiresAtUtc = Timestamp.FromDateTimeOffset(expires),
                EnrollmentCommandHint = "--mint-passkey-enrollment-code"
            }
        };

        var status = await new PasskeyAdminClient(stub).GetGateStatusAsync(TestContext.Current.CancellationToken);

        status.StoreProtected.Should().BeTrue();
        status.Enrolled.Should().BeTrue();
        status.GrantActive.Should().BeTrue();
        status.GrantExpiresAtUtc.Should().Be(expires);
        status.EnrollmentCommandHint.Should().Be("--mint-passkey-enrollment-code");
    }

    [Fact]
    public async Task GetGateStatusAsync_absent_expiry_and_blank_hint_degrade_to_null_and_the_default_flag()
    {
        var stub = new StubClient { Status = new Contract.PasskeyGateStatusResponse { Enrolled = false } };

        var status = await new PasskeyAdminClient(stub).GetGateStatusAsync(TestContext.Current.CancellationToken);

        status.GrantExpiresAtUtc.Should().BeNull();
        status.EnrollmentCommandHint.Should().Be(PasskeyOperations.EnrollmentCommandFlag);
    }

    [Fact]
    public async Task ListPasskeysAsync_maps_the_sync_flags()
    {
        var stub = new StubClient
        {
            Passkeys = new Contract.ListPasskeysResponse
            {
                Credentials =
                {
                    new Contract.PasskeyCredentialInfo
                    {
                        Id = "abc_-", Name = "Phone", CreatedAtUtc = Timestamp.FromDateTimeOffset(Created),
                        BackupEligible = true, BackupState = true
                    },
                    new Contract.PasskeyCredentialInfo
                    {
                        Id = "def", Name = "Key", CreatedAtUtc = Timestamp.FromDateTimeOffset(Created),
                        BackupEligible = false, BackupState = false
                    }
                }
            }
        };

        var passkeys = await new PasskeyAdminClient(stub).ListPasskeysAsync(TestContext.Current.CancellationToken);

        passkeys.Should().HaveCount(2);
        passkeys[0].Should().Be(new PasskeyInfo("abc_-", "Phone", Created, BackupEligible: true, BackupState: true));
        passkeys[1].BackupEligible.Should().BeFalse();
        passkeys[1].BackupState.Should().BeFalse();
    }

    [Fact]
    public async Task BeginEnrollmentAsync_sends_the_code_and_returns_the_options_json()
    {
        var stub = new StubClient { Options = new Contract.WebAuthnOptionsResponse { OptionsJson = "{\"rp\":1}" } };

        var options = await new PasskeyAdminClient(stub)
            .BeginEnrollmentAsync("ABCD-EFGH", TestContext.Current.CancellationToken);

        options.Should().Be("{\"rp\":1}");
        stub.LastEnrollmentCode.Should().Be("ABCD-EFGH");
    }

    [Fact]
    public async Task FinishEnrollmentAsync_sends_the_attestation_and_name_and_maps_the_credential()
    {
        var stub = new StubClient
        {
            Enrolled = new Contract.PasskeyCredentialResponse
            {
                Credential = new Contract.PasskeyCredentialInfo
                {
                    Id = "new", Name = "Laptop", CreatedAtUtc = Timestamp.FromDateTimeOffset(Created)
                }
            }
        };

        var credential = await new PasskeyAdminClient(stub)
            .FinishEnrollmentAsync("{\"att\":1}", "Laptop", TestContext.Current.CancellationToken);

        stub.LastAttestation.Should().Be("{\"att\":1}");
        stub.LastName.Should().Be("Laptop");
        credential.Id.Should().Be("new");
        credential.Name.Should().Be("Laptop");
    }

    [Fact]
    public async Task BeginContentUnlockAsync_returns_the_options_json()
    {
        var stub = new StubClient { Options = new Contract.WebAuthnOptionsResponse { OptionsJson = "{\"challenge\":\"c\"}" } };

        var options = await new PasskeyAdminClient(stub).BeginContentUnlockAsync(TestContext.Current.CancellationToken);

        options.Should().Be("{\"challenge\":\"c\"}");
    }

    [Fact]
    public async Task FinishContentUnlockAsync_sends_the_assertion_and_maps_the_grant()
    {
        var expires = Created.AddMinutes(15);
        var stub = new StubClient
        {
            GrantResponse = new Contract.ContentGrantResponse
            {
                GrantToken = "grant-123", ExpiresAtUtc = Timestamp.FromDateTimeOffset(expires)
            }
        };

        var grant = await new PasskeyAdminClient(stub)
            .FinishContentUnlockAsync("{\"sig\":1}", TestContext.Current.CancellationToken);

        stub.LastAssertion.Should().Be("{\"sig\":1}");
        grant.Should().Be(new ContentGrantInfo("grant-123", expires));
    }

    [Fact]
    public async Task BeginOneOperationAsync_sends_the_operation_and_parameters()
    {
        var stub = new StubClient { Options = new Contract.WebAuthnOptionsResponse { OptionsJson = "{}" } };

        await new PasskeyAdminClient(stub).BeginOneOperationAsync(PasskeyOperations.GetManagementToken,
            PasskeyOperations.NoParameters, TestContext.Current.CancellationToken);

        stub.LastOperation.Should().Be("get_management_token");
        stub.LastParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishOneOperationAsync_sends_assertion_operation_and_parameters_and_maps_the_authorization()
    {
        var expires = Created.AddMinutes(2);
        var stub = new StubClient
        {
            Authorization = new Contract.OneOperationAuthorizationResponse
            {
                AuthorizationToken = "authz-9", ExpiresAtUtc = Timestamp.FromDateTimeOffset(expires)
            }
        };

        var authorization = await new PasskeyAdminClient(stub).FinishOneOperationAsync("{\"sig\":2}",
            PasskeyOperations.RegenerateManagementToken, PasskeyOperations.NoParameters,
            TestContext.Current.CancellationToken);

        stub.LastAssertion.Should().Be("{\"sig\":2}");
        stub.LastOperation.Should().Be("regenerate_management_token");
        stub.LastParameters.Should().BeEmpty();
        authorization.Should().Be(new OneOperationAuthorizationInfo("authz-9", expires));
    }

    [Fact]
    public async Task LockContentAsync_calls_the_lock_rpc()
    {
        var stub = new StubClient();

        await new PasskeyAdminClient(stub).LockContentAsync(TestContext.Current.CancellationToken);

        stub.LockCalls.Should().Be(1);
    }

    [Fact]
    public async Task ListRecentApprovalsAsync_maps_each_entry()
    {
        var stub = new StubClient
        {
            ApprovalList = new Contract.ListRecentApprovalsResponse
            {
                Approvals =
                {
                    new Contract.PasskeyApproval
                    {
                        Operation = "content_unlock", CredentialName = "Phone",
                        AtUtc = Timestamp.FromDateTimeOffset(Created), Outcome = "succeeded"
                    }
                }
            }
        };

        var approvals = await new PasskeyAdminClient(stub)
            .ListRecentApprovalsAsync(TestContext.Current.CancellationToken);

        approvals.Should().ContainSingle().Which.Should()
            .Be(new PasskeyApprovalInfo("content_unlock", "Phone", Created, "succeeded"));
    }

    [Fact]
    public async Task A_refusal_keeps_the_routers_own_detail_and_is_not_flagged_unavailable()
    {
        const string detail = "Conversation content is locked until a passkey is enrolled. Run --mint-passkey-enrollment-code";
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.FailedPrecondition, detail)) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            new PasskeyAdminClient(stub).BeginContentUnlockAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be($"Could not start the content unlock: {detail}");
        ex.IsUnavailable.Should().BeFalse();
        ex.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task An_unreachable_router_becomes_a_plain_language_unavailable_error()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            new PasskeyAdminClient(stub).GetGateStatusAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the passkey gate status: the router is not reachable.");
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PasskeyAdminClient((Contract.PasskeyAdminService.PasskeyAdminServiceClient)null!));
    }

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.PasskeyAdminService.PasskeyAdminServiceClient
    {
        public Contract.PasskeyGateStatusResponse Status { get; init; } = new();
        public Contract.ListPasskeysResponse Passkeys { get; init; } = new();
        public Contract.WebAuthnOptionsResponse Options { get; init; } = new();
        public Contract.PasskeyCredentialResponse Enrolled { get; init; } = new()
        {
            Credential = new Contract.PasskeyCredentialInfo { CreatedAtUtc = new Timestamp() }
        };
        public Contract.ContentGrantResponse GrantResponse { get; init; } = new() { ExpiresAtUtc = new Timestamp() };
        public Contract.OneOperationAuthorizationResponse Authorization { get; init; } = new() { ExpiresAtUtc = new Timestamp() };
        public Contract.ListRecentApprovalsResponse ApprovalList { get; init; } = new();
        public RpcException? Failure { get; init; }

        public string? LastEnrollmentCode { get; private set; }
        public string? LastAttestation { get; private set; }
        public string? LastName { get; private set; }
        public string? LastAssertion { get; private set; }
        public string? LastOperation { get; private set; }
        public string? LastParameters { get; private set; }
        public int LockCalls { get; private set; }

        private AsyncUnaryCall<T> Reply<T>(T response)
        {
            return new AsyncUnaryCall<T>(
                responseAsync: Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Grpc.Core.Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncUnaryCall<Contract.PasskeyGateStatusResponse> GetPasskeyGateStatusAsync(
            Contract.GetPasskeyGateStatusRequest request, CallOptions options) => Reply(Status);

        public override AsyncUnaryCall<Contract.ListPasskeysResponse> ListPasskeysAsync(
            Contract.ListPasskeysRequest request, CallOptions options) => Reply(Passkeys);

        public override AsyncUnaryCall<Contract.WebAuthnOptionsResponse> BeginEnrollmentAsync(
            Contract.BeginEnrollmentRequest request, CallOptions options)
        {
            LastEnrollmentCode = request.EnrollmentCode;
            return Reply(Options);
        }

        public override AsyncUnaryCall<Contract.PasskeyCredentialResponse> FinishEnrollmentAsync(
            Contract.FinishEnrollmentRequest request, CallOptions options)
        {
            LastAttestation = request.AttestationJson;
            LastName = request.Name;
            return Reply(Enrolled);
        }

        public override AsyncUnaryCall<Contract.WebAuthnOptionsResponse> BeginContentUnlockAsync(
            Contract.BeginContentUnlockRequest request, CallOptions options) => Reply(Options);

        public override AsyncUnaryCall<Contract.ContentGrantResponse> FinishContentUnlockAsync(
            Contract.FinishContentUnlockRequest request, CallOptions options)
        {
            LastAssertion = request.AssertionJson;
            return Reply(GrantResponse);
        }

        public override AsyncUnaryCall<Contract.WebAuthnOptionsResponse> BeginOneOperationAsync(
            Contract.BeginOneOperationRequest request, CallOptions options)
        {
            LastOperation = request.Operation;
            LastParameters = request.Parameters;
            return Reply(Options);
        }

        public override AsyncUnaryCall<Contract.OneOperationAuthorizationResponse> FinishOneOperationAsync(
            Contract.FinishOneOperationRequest request, CallOptions options)
        {
            LastAssertion = request.AssertionJson;
            LastOperation = request.Operation;
            LastParameters = request.Parameters;
            return Reply(Authorization);
        }

        public override AsyncUnaryCall<Contract.LockContentResponse> LockContentAsync(
            Contract.LockContentRequest request, CallOptions options)
        {
            LockCalls++;
            return Reply(new Contract.LockContentResponse());
        }

        public override AsyncUnaryCall<Contract.ListRecentApprovalsResponse> ListRecentApprovalsAsync(
            Contract.ListRecentApprovalsRequest request, CallOptions options) => Reply(ApprovalList);

        public Contract.ListPendingApprovalsResponse PendingList { get; init; } = new();
        public string? LastApprovalId { get; private set; }
        public int DenyCalls { get; private set; }

        public override AsyncUnaryCall<Contract.ListPendingApprovalsResponse> ListPendingApprovalsAsync(
            Contract.ListPendingApprovalsRequest request, CallOptions options) => Reply(PendingList);

        public override AsyncUnaryCall<Contract.WebAuthnOptionsResponse> BeginApprovalAsync(
            Contract.BeginApprovalRequest request, CallOptions options)
        {
            LastApprovalId = request.ApprovalId;
            return Reply(Options);
        }

        public override AsyncUnaryCall<Contract.FinishApprovalResponse> FinishApprovalAsync(
            Contract.FinishApprovalRequest request, CallOptions options)
        {
            LastApprovalId = request.ApprovalId;
            LastAssertion = request.AssertionJson;
            return Reply(new Contract.FinishApprovalResponse());
        }

        public override AsyncUnaryCall<Contract.DenyApprovalResponse> DenyApprovalAsync(
            Contract.DenyApprovalRequest request, CallOptions options)
        {
            LastApprovalId = request.ApprovalId;
            DenyCalls++;
            return Reply(new Contract.DenyApprovalResponse());
        }
    }

    [Fact]
    public async Task ListPendingApprovalsAsync_maps_the_filter_and_destination_and_leaves_absent_options_null()
    {
        var stub = new StubClient
        {
            PendingList = new Contract.ListPendingApprovalsResponse
            {
                Approvals =
                {
                    new Contract.PendingApproval
                    {
                        ApprovalId = "a1",
                        Export = new Contract.ExportApprovalDetails
                        {
                            FromUtc = Timestamp.FromDateTimeOffset(Created),
                            Model = "opus",
                            DestinationPath = "/exports/out.zip"
                        },
                        CreatedAtUtc = Timestamp.FromDateTimeOffset(Created),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(Created.AddMinutes(5))
                    }
                }
            }
        };

        var pending = await new PasskeyAdminClient(stub).ListPendingApprovalsAsync(TestContext.Current.CancellationToken);

        pending.Should().ContainSingle().Which.Should().Be(new PendingApprovalInfo(
            ApprovalId: "a1",
            Filter: new ConversationExportFilterInfo(From: Created, Model: "opus"),
            DestinationPath: "/exports/out.zip",
            CreatedAtUtc: Created,
            ExpiresAtUtc: Created.AddMinutes(5)));
    }

    [Fact]
    public async Task BeginApprovalAsync_sends_only_the_request_id_and_returns_the_options_json()
    {
        var stub = new StubClient { Options = new Contract.WebAuthnOptionsResponse { OptionsJson = "{\"c\":1}" } };

        var options = await new PasskeyAdminClient(stub).BeginApprovalAsync("a1", TestContext.Current.CancellationToken);

        options.Should().Be("{\"c\":1}");
        stub.LastApprovalId.Should().Be("a1");
    }

    [Fact]
    public async Task FinishApprovalAsync_sends_the_request_id_and_the_assertion()
    {
        var stub = new StubClient();

        await new PasskeyAdminClient(stub).FinishApprovalAsync("a1", "{\"assertion\":1}", TestContext.Current.CancellationToken);

        stub.LastApprovalId.Should().Be("a1");
        stub.LastAssertion.Should().Be("{\"assertion\":1}");
    }

    [Fact]
    public async Task DenyApprovalAsync_sends_the_request_id()
    {
        var stub = new StubClient();

        await new PasskeyAdminClient(stub).DenyApprovalAsync("a1", TestContext.Current.CancellationToken);

        stub.LastApprovalId.Should().Be("a1");
        stub.DenyCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_request_that_is_no_longer_waiting_keeps_the_routers_detail()
    {
        const string detail = "This approval request is no longer waiting";
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.NotFound, detail)) };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            new PasskeyAdminClient(stub).DenyApprovalAsync("a1", TestContext.Current.CancellationToken));

        ex.Message.Should().Contain(detail);
        ex.IsUnavailable.Should().BeFalse();
    }
}
