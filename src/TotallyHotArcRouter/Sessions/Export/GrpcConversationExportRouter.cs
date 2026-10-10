using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>
/// The production <see cref="IConversationExportRouter"/>: calls the running router's loopback gRPC endpoint with the
/// shared management token as <c>x-admin-token</c> (ADR-0020, Amendment 1). The token comes from the elevated
/// secret-store path (<c>ManagementAccessToken.GetOrCreate</c>, ADR-0012/0015), never from the gated
/// <c>GetManagementToken</c> RPC, and is sent only to the loopback endpoint this class connects to.
/// </summary>
/// <remarks>
/// The router's web port also serves the dashboard, so this reaches the same listener the dashboard's own gRPC
/// clients use. The certificate is trusted by the same rule the dashboard clients apply: the subject must be
/// exactly <c>CN=localhost</c> and the request must target a loopback host, which a process elsewhere cannot meet.
/// </remarks>
internal sealed class GrpcConversationExportRouter : IConversationExportRouter, IDisposable
{
    /// <summary>The metadata entry the router's admin interceptor reads the management token from.</summary>
    internal const string AdminTokenHeaderName = "x-admin-token";

    /// <summary>How long filing a request may take before the router counts as unreachable.</summary>
    private static readonly TimeSpan CreateDeadline = TimeSpan.FromSeconds(15);

    private readonly GrpcChannel? _channel;
    private readonly Contract.PasskeyAdminService.PasskeyAdminServiceClient _passkey;
    private readonly Contract.ConversationAdminService.ConversationAdminServiceClient _conversations;
    private readonly Metadata _headers;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcConversationExportRouter"/> class over a channel to the
    /// router's loopback endpoint. Use <see cref="Connect"/> in production.
    /// </summary>
    /// <param name="callInvoker">The invoker for the router's loopback endpoint.</param>
    /// <param name="adminToken">The management token to present on every call.</param>
    /// <param name="channel">The channel to dispose with this instance, or <see langword="null"/> when the caller owns it.</param>
    internal GrpcConversationExportRouter(CallInvoker callInvoker, string adminToken, GrpcChannel? channel = null)
    {
        ArgumentNullException.ThrowIfNull(callInvoker);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken);
        _passkey = new Contract.PasskeyAdminService.PasskeyAdminServiceClient(callInvoker);
        _conversations = new Contract.ConversationAdminService.ConversationAdminServiceClient(callInvoker);
        _headers = new Metadata { { AdminTokenHeaderName, adminToken } };
        _channel = channel;
    }

    /// <summary>
    /// Resolves where the running router listens: the port its discovery file records (the router writes it once the
    /// listener binds, so it holds whatever the operator configured), or the default web port when there is none,
    /// always on <c>localhost</c> so the certificate's name matches.
    /// </summary>
    /// <param name="discoveryFilePath">The discovery file to read; <see langword="null"/> uses the router's default location.</param>
    /// <returns>The router's loopback gRPC base address.</returns>
    internal static Uri ResolveAddress(string? discoveryFilePath = null)
    {
        var recorded = WebInterfaceDiscoveryFile.TryRead(discoveryFilePath)?.WebUrl;
        var port = Uri.TryCreate(recorded, UriKind.Absolute, out var url) && url.Port > 0
            ? url.Port
            : new WebInterfaceOptions().Port;
        return new Uri($"https://localhost:{port}");
    }

    /// <summary>Opens a channel to the router at <paramref name="address"/> that presents <paramref name="adminToken"/>.</summary>
    /// <param name="address">The router's loopback gRPC base address, from <see cref="ResolveAddress"/>.</param>
    /// <param name="adminToken">The management token to present on every call.</param>
    /// <returns>A router client that disposes its channel.</returns>
    internal static GrpcConversationExportRouter Connect(Uri address, string adminToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = ValidateLoopbackCertificate };
        var channel = GrpcChannel.ForAddress(
            address, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        return new GrpcConversationExportRouter(channel.CreateCallInvoker(), adminToken, channel);
    }

    /// <inheritdoc/>
    public async Task<ExportApprovalTicket> CreateApprovalRequestAsync(
        ExportConversationsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = new Contract.CreateApprovalRequestRequest
        {
            Export = ConversationExportWire.ToApprovalDetails(options.Filter, options.DestinationPath),
        };
        var response = await _passkey.CreateApprovalRequestAsync(
            request, _headers, deadline: DateTime.UtcNow + CreateDeadline, cancellationToken).ConfigureAwait(false);
        return new ExportApprovalTicket(
            response.ApprovalId, response.DashboardUrl, response.ExpiresAtUtc.ToDateTimeOffset());
    }

    /// <inheritdoc/>
    public async Task<ExportApprovalResult> WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);

        var response = await _passkey.WaitForApprovalAsync(
            new Contract.WaitForApprovalRequest { ApprovalId = approvalId },
            _headers,
            deadline: null,
            cancellationToken).ConfigureAwait(false);
        return response.Outcome switch
        {
            Contract.ApprovalOutcome.Approved => new ExportApprovalResult(
                PendingApprovalOutcome.Approved, response.AuthorizationToken),
            Contract.ApprovalOutcome.Denied => new ExportApprovalResult(PendingApprovalOutcome.Denied, null),
            Contract.ApprovalOutcome.Expired => new ExportApprovalResult(PendingApprovalOutcome.Expired, null),
            _ => throw new RpcException(new Status(
                StatusCode.Internal, "The router returned an approval outcome this command does not understand.")),
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ExportCommandEvent> ExportAsync(
        ExportConversationsOptions options,
        string authorizationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationToken);

        var details = ConversationExportWire.ToApprovalDetails(options.Filter, options.DestinationPath);
        var request = new Contract.ExportConversationsRequest
        {
            DestinationPath = details.DestinationPath,
            AuthorizationToken = authorizationToken,
        };
        if (details.FromUtc is not null) request.FromUtc = details.FromUtc;
        if (details.ToUtc is not null) request.ToUtc = details.ToUtc;
        if (details.HasSessionId) request.SessionId = details.SessionId;
        if (details.HasHarness) request.Harness = details.Harness;
        if (details.HasProvider) request.Provider = details.Provider;
        if (details.HasModel) request.Model = details.Model;

        using var call = _conversations.ExportConversations(request, _headers, deadline: null, cancellationToken);
        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (message.EventCase)
            {
                case Contract.ExportConversationsEvent.EventOneofCase.Progress:
                    yield return new ExportCommandEvent(
                        new ConversationExportProgress(message.Progress.TurnsWritten, message.Progress.BytesWritten),
                        null);
                    break;
                case Contract.ExportConversationsEvent.EventOneofCase.Result:
                    yield return new ExportCommandEvent(
                        null,
                        new ConversationExportResult(
                            message.Result.Path,
                            message.Result.Conversations,
                            message.Result.Turns,
                            message.Result.MissingBodies,
                            message.Result.CorruptTurns,
                            message.Result.IncompleteSessions,
                            message.Result.BodyBytes));
                    break;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _channel?.Dispose();

    /// <summary>
    /// Trusts a certificate only if its subject is exactly <c>CN=localhost</c> and the request targets a loopback host.
    /// Chain errors are ignored, as for the dashboard's own clients: the router's certificate is issued by its local
    /// CA, which this process need not have installed. The trust boundary is "same machine"; the management token
    /// this command sends is never presented to any other host.
    /// </summary>
    /// <param name="request">The request being sent.</param>
    /// <param name="certificate">The server's certificate.</param>
    /// <param name="chain">The certificate chain, which is not consulted.</param>
    /// <param name="errors">The chain errors, which are not consulted.</param>
    /// <returns><see langword="true"/> when the certificate is <c>CN=localhost</c> and the host is loopback.</returns>
    internal static bool ValidateLoopbackCertificate(
        HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || certificate.Subject != "CN=localhost") return false;

        return request.RequestUri?.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }
}
