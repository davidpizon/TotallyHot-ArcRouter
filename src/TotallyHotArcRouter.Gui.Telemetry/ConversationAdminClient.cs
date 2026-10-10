using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Selects the turns a conversation export includes. Every field is optional and all that are set must match.
/// The same values travel in the request and in the passkey approval's binding
/// (<see cref="PasskeyOperations.ExportParameters"/>), so the dialog builds one of these, signs it, and sends
/// exactly it.
/// </summary>
/// <param name="From">The earliest turn time to include (inclusive), or <see langword="null"/> for no lower bound.</param>
/// <param name="To">The latest turn time to include (inclusive), or <see langword="null"/> for no upper bound.</param>
/// <param name="SessionId">An archive session id or a client session id.</param>
/// <param name="Harness">The harness a turn must have.</param>
/// <param name="Provider">The provider a turn must have been routed to.</param>
/// <param name="Model">A model a turn must have requested, been routed to, or resolved to.</param>
public sealed record ConversationExportFilterInfo(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? SessionId = null,
    string? Harness = null,
    string? Provider = null,
    string? Model = null);

/// <summary>What the router's session store holds, for the Export dialog's header.</summary>
/// <param name="SessionCount">Sessions in the store.</param>
/// <param name="TurnCount">Committed turns across those sessions.</param>
/// <param name="BytesOnDisk">Committed bytes of the session files (encrypted and compressed, so not the export's size).</param>
public sealed record ConversationExportSummaryInfo(int SessionCount, long TurnCount, long BytesOnDisk);

/// <summary>How far a running export has got.</summary>
/// <param name="TurnsWritten">Turns in the zip so far.</param>
/// <param name="BytesWritten">Stored plaintext bytes of the bodies written so far, before the zip compresses them.</param>
public sealed record ConversationExportProgressInfo(int TurnsWritten, long BytesWritten);

/// <summary>What a finished export wrote.</summary>
/// <param name="Path">The zip's path on the router machine.</param>
/// <param name="Conversations">Sessions that contributed at least one turn.</param>
/// <param name="Turns">Turns in the zip.</param>
/// <param name="MissingBodies">Bodies recorded as missing rather than written.</param>
/// <param name="CorruptTurns">Turns that failed verification and were left out.</param>
/// <param name="IncompleteSessions">Sessions deleted before all their turns were written.</param>
/// <param name="BodyBytes">Stored plaintext bytes of every body written, before the zip compresses them.</param>
public sealed record ConversationExportResultInfo(
    string Path,
    int Conversations,
    int Turns,
    int MissingBodies,
    int CorruptTurns,
    int IncompleteSessions,
    long BodyBytes);

/// <summary>
/// One message on the export stream: zero or more progress reports, then one result as the final message.
/// </summary>
/// <param name="Progress">A progress report, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The finished export, set only on the final message.</param>
public sealed record ConversationExportEvent(
    ConversationExportProgressInfo? Progress,
    ConversationExportResultInfo? Result);

/// <summary>
/// The Sessions tab's Export dialog operations. An interface so the dialog can be unit-tested against a fake
/// without a live proxy or a gRPC channel, mirroring <see cref="IManagementTokenAdminClient"/>.
/// </summary>
public interface IConversationAdminClient
{
    /// <summary>Reads how many sessions and turns the router's session store holds and the bytes they occupy.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<ConversationExportSummaryInfo> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the matching conversations to a zip at <paramref name="destinationPath"/> on the router machine,
    /// yielding one <see cref="ConversationExportEvent"/> per progress report and a final one carrying the
    /// result. The router refuses the call unless <paramref name="authorizationToken"/> is a one-operation passkey
    /// authorization bound to exactly this filter and destination
    /// (<see cref="PasskeyOperations.ExportParameters"/>, ADR-0020).
    /// </summary>
    /// <param name="filter">Which turns to include.</param>
    /// <param name="destinationPath">The zip's absolute path on the router machine.</param>
    /// <param name="authorizationToken">The single-use authorization from <see cref="IPasskeyAdminClient.FinishOneOperationAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the export, which then leaves no file behind.</param>
    /// <exception cref="GrpcAdminException">The call failed, was not authorized, was refused, or the router is unreachable.</exception>
    IAsyncEnumerable<ConversationExportEvent> ExportAsync(
        ConversationExportFilterInfo filter,
        string destinationPath,
        string authorizationToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>ConversationAdminService</c> - the Sessions tab's Export dialog (#165 phase 3).
/// Lives in this plain <c>net10.0</c> library rather than a UI-framework project so CI can unit-test it, exactly
/// like <see cref="ManagementTokenAdminClient"/>.
/// </summary>
public sealed class ConversationAdminClient
    : GrpcAdminClientBase<Contract.ConversationAdminService.ConversationAdminServiceClient>,
        IConversationAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationAdminClient"/> class over a shared,
    /// already-authenticated call invoker - see <see cref="IRouterChannelProvider"/>'s remarks.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public ConversationAdminClient(CallInvoker callInvoker)
        : base(new Contract.ConversationAdminService.ConversationAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    /// <param name="client">The generated client to wrap.</param>
    public ConversationAdminClient(Contract.ConversationAdminService.ConversationAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<ConversationExportSummaryInfo> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
                call: (client, ct) => client.GetConversationExportSummaryAsync(
                    new Contract.GetConversationExportSummaryRequest(), cancellationToken: ct),
                action: "Could not read the conversation store",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ConversationExportSummaryInfo(
            SessionCount: response.SessionCount,
            TurnCount: response.TurnCount,
            BytesOnDisk: response.BytesOnDisk);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ConversationExportEvent> ExportAsync(
        ConversationExportFilterInfo filter,
        string destinationPath,
        string authorizationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        var request = new Contract.ExportConversationsRequest
        {
            DestinationPath = destinationPath,
            AuthorizationToken = authorizationToken,
        };
        if (filter.From is { } from) request.FromUtc = Timestamp.FromDateTimeOffset(from);
        if (filter.To is { } to) request.ToUtc = Timestamp.FromDateTimeOffset(to);
        if (filter.SessionId is not null) request.SessionId = filter.SessionId;
        if (filter.Harness is not null) request.Harness = filter.Harness;
        if (filter.Provider is not null) request.Provider = filter.Provider;
        if (filter.Model is not null) request.Model = filter.Model;

        using var call = Client.ExportConversations(request, cancellationToken: cancellationToken);
        var stream = call.ResponseStream;

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await stream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                // Not caught inside a try that also yields: an iterator cannot yield from within a catch
                // block, so MoveNext's outcome is captured here and acted on outside the try.
                throw Wrap(ex: ex, action: "The conversation export failed");
            }

            if (!hasNext) yield break;

            yield return MapEvent(stream.Current);
        }
    }

    /// <summary>
    /// Converts a gRPC-contract stream message into the client's <see cref="ConversationExportEvent"/>. Switches
    /// explicitly on every defined <see cref="Contract.ExportConversationsEvent.EventOneofCase"/>, including
    /// <c>None</c>, so a message kind added to the contract later is a visible gap rather than a silent default.
    /// </summary>
    private static ConversationExportEvent MapEvent(Contract.ExportConversationsEvent wire)
    {
        return wire.EventCase switch
        {
            Contract.ExportConversationsEvent.EventOneofCase.Progress =>
                new ConversationExportEvent(
                    Progress: new ConversationExportProgressInfo(
                        TurnsWritten: wire.Progress.TurnsWritten, BytesWritten: wire.Progress.BytesWritten),
                    Result: null),
            Contract.ExportConversationsEvent.EventOneofCase.Result =>
                new ConversationExportEvent(
                    Progress: null,
                    Result: new ConversationExportResultInfo(
                        Path: wire.Result.Path,
                        Conversations: wire.Result.Conversations,
                        Turns: wire.Result.Turns,
                        MissingBodies: wire.Result.MissingBodies,
                        CorruptTurns: wire.Result.CorruptTurns,
                        IncompleteSessions: wire.Result.IncompleteSessions,
                        BodyBytes: wire.Result.BodyBytes)),
            _ => new ConversationExportEvent(Progress: null, Result: null),
        };
    }
}
