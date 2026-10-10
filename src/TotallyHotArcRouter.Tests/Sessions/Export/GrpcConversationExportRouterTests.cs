using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Sessions.Export;

/// <summary>
/// Covers <see cref="GrpcConversationExportRouter"/> (#165 phase 3, PR 3c): the real gRPC adapter, driven end to end
/// through <see cref="ExportConversationsCommand"/> against a fake router transport, makes the three calls in order,
/// presents the management token as <c>x-admin-token</c> on every one, and sends the same filter and destination in
/// the approval request and in the export - the two the router's gate binds together.
/// </summary>
public sealed class GrpcConversationExportRouterTests
{
    private const string AdminToken = "management-token-value";
    private const string OneOperationToken = "one-op-token";

    private sealed class RecordingInvoker : CallInvoker
    {
        public List<string> Calls { get; } = [];

        public List<string?> AdminTokens { get; } = [];

        public Contract.CreateApprovalRequestRequest? Created { get; private set; }

        public Contract.ExportConversationsRequest? Exported { get; private set; }

        public Contract.ApprovalOutcome Outcome { get; set; } = Contract.ApprovalOutcome.Approved;

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Record(method.Name, options);
            object response = request switch
            {
                Contract.CreateApprovalRequestRequest create => Capture(create),
                Contract.WaitForApprovalRequest => new Contract.WaitForApprovalResponse
                {
                    Outcome = Outcome,
                    AuthorizationToken = Outcome == Contract.ApprovalOutcome.Approved ? OneOperationToken : string.Empty,
                },
                _ => throw new NotSupportedException(method.Name),
            };

            return TestCalls.AsyncUnaryCall(
                Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Record(method.Name, options);
            Exported = request as Contract.ExportConversationsRequest;
            object[] messages =
            [
                new Contract.ExportConversationsEvent { Progress = new Contract.ExportProgress { TurnsWritten = 3, BytesWritten = 99 } },
                new Contract.ExportConversationsEvent
                {
                    Result = new Contract.ExportResult { Path = Exported!.DestinationPath, Conversations = 1, Turns = 3, BodyBytes = 99 },
                },
            ];
            return TestCalls.AsyncServerStreamingCall(
                new ListStreamReader<TResponse>([.. messages.Cast<TResponse>()]),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        private Contract.CreateApprovalRequestResponse Capture(Contract.CreateApprovalRequestRequest request)
        {
            Created = request;
            return new Contract.CreateApprovalRequestResponse
            {
                ApprovalId = "approval-1",
                DashboardUrl = "https://localhost:47104/?approval=approval-1",
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 10, 12, 5, 0, TimeSpan.Zero)),
            };
        }

        private void Record(string method, CallOptions options)
        {
            Calls.Add(method);
            AdminTokens.Add(options.Headers?.GetValue(GrpcConversationExportRouter.AdminTokenHeaderName));
        }
    }

    private sealed class ListStreamReader<T>(List<T> items) : IAsyncStreamReader<T>
    {
        private int _next;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_next >= items.Count) return Task.FromResult(false);

            Current = items[_next++];
            return Task.FromResult(true);
        }
    }

    private static ExportConversationsOptions Options() => new(
        new ConversationExportFilter(
            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            SessionId: "s-1",
            Model: "opus"),
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapter-export.zip")));

    [Fact]
    public async Task EndToEnd_MakesTheThreeCallsInOrder_WithTheAdminTokenOnEach()
    {
        var invoker = new RecordingInvoker();
        var router = new GrpcConversationExportRouter(invoker, AdminToken);
        var stdout = new StringWriter();

        var exit = await ExportConversationsCommand.RunAsync(
            Options(), router, stdout, new StringWriter(), progressInterval: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        exit.Should().Be(ExportConversationsCommand.ExitSuccess);
        invoker.Calls.Should().Equal("CreateApprovalRequest", "WaitForApproval", "ExportConversations");
        invoker.AdminTokens.Should().AllBe(AdminToken);
        stdout.ToString().Should().Contain("https://localhost:47104/?approval=approval-1");
    }

    [Fact]
    public async Task EndToEnd_ExportsWithTheAuthorizationTheRouterIssuedAndTheSameFilterAndDestinationItWasAskedToApprove()
    {
        var invoker = new RecordingInvoker();
        var options = Options();

        await ExportConversationsCommand.RunAsync(
            options, new GrpcConversationExportRouter(invoker, AdminToken), new StringWriter(), new StringWriter(),
            progressInterval: TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);

        invoker.Exported!.AuthorizationToken.Should().Be(OneOperationToken);
        invoker.Exported.DestinationPath.Should().Be(options.DestinationPath);
        invoker.Created!.Export.DestinationPath.Should().Be(options.DestinationPath);

        // The approval and the export describe one export, so the router's digest binding accepts the pair.
        var approvedFilter = ConversationExportWire.ToFilter(invoker.Created.Export);
        var exportedFilter = ConversationExportWire.ToFilter(
            invoker.Exported.FromUtc,
            invoker.Exported.ToUtc,
            invoker.Exported.HasSessionId ? invoker.Exported.SessionId : null,
            invoker.Exported.HasHarness ? invoker.Exported.Harness : null,
            invoker.Exported.HasProvider ? invoker.Exported.Provider : null,
            invoker.Exported.HasModel ? invoker.Exported.Model : null);
        exportedFilter.Should().Be(approvedFilter).And.Be(options.Filter);
        GatedOperation.ExportParameters(exportedFilter, invoker.Exported.DestinationPath)
            .Should().Be(GatedOperation.ExportParameters(approvedFilter, invoker.Created.Export.DestinationPath));
    }

    [Fact]
    public async Task EndToEnd_WhenTheOperatorDenies_NeverCallsExport()
    {
        var invoker = new RecordingInvoker { Outcome = Contract.ApprovalOutcome.Denied };

        var exit = await ExportConversationsCommand.RunAsync(
            Options(), new GrpcConversationExportRouter(invoker, AdminToken), new StringWriter(), new StringWriter(),
            cancellationToken: TestContext.Current.CancellationToken);

        exit.Should().Be(ExportConversationsCommand.ExitDenied);
        invoker.Calls.Should().Equal("CreateApprovalRequest", "WaitForApproval");
    }

    [Fact]
    public async Task EndToEnd_WhenTheApprovalExpires_NeverCallsExport()
    {
        var invoker = new RecordingInvoker { Outcome = Contract.ApprovalOutcome.Expired };

        var exit = await ExportConversationsCommand.RunAsync(
            Options(), new GrpcConversationExportRouter(invoker, AdminToken), new StringWriter(), new StringWriter(),
            cancellationToken: TestContext.Current.CancellationToken);

        exit.Should().Be(ExportConversationsCommand.ExitTimedOut);
        invoker.Calls.Should().Equal("CreateApprovalRequest", "WaitForApproval");
    }

    [Fact]
    public void Constructor_RefusesABlankAdminToken()
    {
        var create = () => new GrpcConversationExportRouter(new RecordingInvoker(), " ");

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolveAddress_UsesThePortTheRouterRecordedAndAlwaysTargetsLocalhost()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-interface-{Guid.NewGuid():N}.json");
        try
        {
            WebInterfaceDiscoveryFile.Write(new WebInterfaceDiscoveryInfo(WebUrl: "https://127.0.0.1:50123", CaThumbprint: null), path);

            GrpcConversationExportRouter.ResolveAddress(path).Should().Be(new Uri("https://localhost:50123"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveAddress_WithoutADiscoveryFile_FallsBackToTheDefaultWebPort()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        GrpcConversationExportRouter.ResolveAddress(missing).Should().Be(new Uri("https://localhost:47104"));
    }

    [Fact]
    public void ValidateLoopbackCertificate_TrustsOnlyCnLocalhostOnALoopbackHost()
    {
        using var localhost = SelfSigned("CN=localhost");
        using var other = SelfSigned("CN=localhost.evil");

        Validate("https://localhost:47104/", localhost).Should().BeTrue();
        Validate("https://127.0.0.1:47104/", localhost).Should().BeTrue();
        Validate("https://[::1]:47104/", localhost).Should().BeTrue();
        Validate("https://example.com:47104/", localhost).Should().BeFalse("the token must never go to another host");
        Validate("https://localhost:47104/", other).Should().BeFalse();
        GrpcConversationExportRouter.ValidateLoopbackCertificate(
            new HttpRequestMessage(HttpMethod.Get, "https://localhost/"), null, null, default).Should().BeFalse();

        static bool Validate(string url, X509Certificate2 certificate) =>
            GrpcConversationExportRouter.ValidateLoopbackCertificate(
                new HttpRequestMessage(HttpMethod.Get, url), certificate, null, default);
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
