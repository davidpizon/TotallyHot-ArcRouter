using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>
/// The production <see cref="IConversationImportRouter"/>: calls the running router's loopback gRPC endpoint with the
/// shared management token as <c>x-admin-token</c>, exactly as <see cref="GrpcConversationExportRouter"/> does and
/// by the same rules (ADR-0020, Amendment 1): the token comes from the elevated secret-store path, never from the
/// gated token RPC, and is sent only to the loopback endpoint this class connects to, whose certificate is trusted
/// by the same subject-and-loopback check.
/// </summary>
internal sealed class GrpcConversationImportRouter : IConversationImportRouter, IDisposable
{
    /// <summary>How long a short call may take before the router counts as unreachable.</summary>
    private static readonly TimeSpan ShortCallDeadline = TimeSpan.FromSeconds(15);

    private readonly GrpcChannel? _channel;
    private readonly Contract.PasskeyAdminService.PasskeyAdminServiceClient _passkey;
    private readonly Contract.ConversationAdminService.ConversationAdminServiceClient _conversations;
    private readonly Metadata _headers;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcConversationImportRouter"/> class over a channel to the
    /// router's loopback endpoint. Use <see cref="Connect"/> in production.
    /// </summary>
    /// <param name="callInvoker">The invoker for the router's loopback endpoint.</param>
    /// <param name="adminToken">The management token to present on every call.</param>
    /// <param name="channel">The channel to dispose with this instance, or <see langword="null"/> when the caller owns it.</param>
    private GrpcConversationImportRouter(CallInvoker callInvoker, string adminToken, GrpcChannel? channel = null)
    {
        ArgumentNullException.ThrowIfNull(callInvoker);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken);
        _passkey = new Contract.PasskeyAdminService.PasskeyAdminServiceClient(callInvoker);
        _conversations = new Contract.ConversationAdminService.ConversationAdminServiceClient(callInvoker);
        _headers = new Metadata { { GrpcConversationExportRouter.AdminTokenHeaderName, adminToken } };
        _channel = channel;
    }

    /// <summary>Opens a channel to the router at <paramref name="address"/> that presents <paramref name="adminToken"/>.</summary>
    /// <param name="address">The router's loopback gRPC base address, from <see cref="GrpcConversationExportRouter.ResolveAddress"/>.</param>
    /// <param name="adminToken">The management token to present on every call.</param>
    /// <returns>A router client that disposes its channel.</returns>
    internal static GrpcConversationImportRouter Connect(Uri address, string adminToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = GrpcConversationExportRouter.ValidateLoopbackCertificate,
        };
        var channel = GrpcChannel.ForAddress(
            address, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        return new GrpcConversationImportRouter(channel.CreateCallInvoker(), adminToken, channel);
    }

    /// <inheritdoc/>
    public async Task<ImportStagedCopy> StageAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        // Copying a large zip can take a while, so unlike filing a request this has no short deadline.
        var response = await _conversations.StageImportAsync(
            new Contract.StageImportRequest { SourcePath = sourcePath },
            _headers,
            deadline: null,
            cancellationToken).ConfigureAwait(false);
        return new ImportStagedCopy(response.StagedId, response.Sha256, response.SizeBytes);
    }

    /// <inheritdoc/>
    public async Task<ExportApprovalTicket> CreateApprovalRequestAsync(
        string stagedId, ConversationImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedId);
        ArgumentNullException.ThrowIfNull(options);

        var request = new Contract.CreateApprovalRequestRequest
        {
            Import = new Contract.ImportApprovalDetails
            {
                StagedId = stagedId,
                Options = ConversationImportWire.ToWire(options),
            },
        };
        var response = await _passkey.CreateApprovalRequestAsync(
            request, _headers, deadline: DateTime.UtcNow + ShortCallDeadline, cancellationToken).ConfigureAwait(false);
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
    public async IAsyncEnumerable<ImportCommandEvent> ImportAsync(
        string stagedId,
        ConversationImportOptions options,
        string authorizationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationToken);

        var request = new Contract.ImportConversationsRequest
        {
            StagedId = stagedId,
            Options = ConversationImportWire.ToWire(options),
            AuthorizationToken = authorizationToken,
        };

        using var call = _conversations.ImportConversations(request, _headers, deadline: null, cancellationToken);
        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (message.EventCase)
            {
                case Contract.ImportConversationsEvent.EventOneofCase.Progress:
                    yield return new ImportCommandEvent(
                        new ConversationImportProgress(message.Progress.TurnsProcessed, message.Progress.TurnsStored),
                        null);
                    break;
                case Contract.ImportConversationsEvent.EventOneofCase.Result:
                    var result = message.Result;
                    yield return new ImportCommandEvent(
                        null,
                        new ConversationImportResult(
                            Guid.TryParse(result.ImportId, out var importId) ? importId : Guid.Empty,
                            result.SessionsAdded,
                            result.SessionsSkipped,
                            result.TurnsAdded,
                            result.TurnsAlreadyPresent,
                            result.TurnsConflictKept,
                            result.TurnsRejected,
                            result.TurnsToGrade,
                            result.BodyBytes,
                            result.TurnsDueForRetention));
                    break;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _channel?.Dispose();
}
