using System.Threading.Channels;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Transcripts;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>
/// gRPC service behind the Sessions tab's Export dialog (#165 phase 3, PR 3b): reports what the session store
/// holds and writes the conversations that match a filter to a zip on the router machine through
/// <see cref="ConversationExportWriter"/>. It is a thin transport over that library, and a dedicated service
/// rather than a <c>ManagementFacade</c> method or an <c>ExportUsageRollup</c> change, because exporting captured
/// conversation text is a passkey-gated operation (ADR-0020) that has nothing to do with usage reporting or
/// provider management.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate before I/O.</b> <see cref="ExportConversations"/> consumes the request's one-operation authorization
/// through <see cref="ContentGateHooks.RequireExport"/> before it opens the session store or the destination,
/// so an unapproved call touches no file. The authorization is bound to the SHA-256 of the filter and
/// destination the request carries, so an approval for one export cannot run another. The only work before it
/// is turning the wire fields into a filter, which does no I/O. A refused export still spends its approval, so
/// the operator approves again after fixing the destination.
/// </para>
/// <para>
/// <b>Status mapping.</b> A refusal by the writer maps to <see cref="StatusCode.InvalidArgument"/> for a
/// destination that breaks the path rules and to <see cref="StatusCode.FailedPrecondition"/> for an export
/// already running or a volume too full; a missing, spent or mismatched authorization is
/// <see cref="StatusCode.Unauthenticated"/> and an un-enrolled router is
/// <see cref="StatusCode.FailedPrecondition"/>, both from the gate.
/// </para>
/// <para>
/// Mapped by the <c>ConversationAdminDependencies</c> admin module beside the other loopback admin services.
/// </para>
/// </remarks>
public sealed class ConversationAdminGrpcService : Contract.ConversationAdminService.ConversationAdminServiceBase
{
    private readonly Lazy<SessionStore> _store;
    private readonly ConversationExportWriter _writer;
    private readonly ContentGate _gate;
    private readonly TranscriptDatabase _database;
    private readonly ILogger<ConversationAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConversationAdminGrpcService"/> class.</summary>
    /// <param name="store">The lazily opened session store; not opened until an export runs or the store already exists.</param>
    /// <param name="writer">The export writer. Share one instance so its single-flight rule holds across calls.</param>
    /// <param name="gate">Verifies and consumes the one-operation passkey authorization an export requires.</param>
    /// <param name="database">Names where the session folder lives, so a summary can tell whether anything was ever captured.</param>
    /// <param name="logger">Receives export outcomes; optional so a hand-built service in a test needs no logging.</param>
    public ConversationAdminGrpcService(
        Lazy<SessionStore> store,
        ConversationExportWriter writer,
        ContentGate gate,
        TranscriptDatabase database,
        ILogger<ConversationAdminGrpcService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(database);
        _store = store;
        _writer = writer;
        _gate = gate;
        _database = database;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationAdminGrpcService>.Instance;
    }

    /// <inheritdoc/>
    public override Task<Contract.ConversationExportSummaryResponse> GetConversationExportSummary(
        Contract.GetConversationExportSummaryRequest request, ServerCallContext context)
    {
        // The folder exists only once a capture has started; opening the store for a router that never captured
        // would create the database and folder just to report zeros.
        if (!Directory.Exists(SessionStore.FolderBeside(_database.DatabasePath)))
        {
            return Task.FromResult(new Contract.ConversationExportSummaryResponse());
        }

        var sessions = _store.Value.ListSessions();
        return Task.FromResult(new Contract.ConversationExportSummaryResponse
        {
            SessionCount = sessions.Count,
            TurnCount = sessions.Sum(session => (long)session.TurnCount),
            BytesOnDisk = sessions.Sum(session => session.CommittedLength),
        });
    }

    /// <inheritdoc/>
    public override async Task ExportConversations(
        Contract.ExportConversationsRequest request,
        IServerStreamWriter<Contract.ExportConversationsEvent> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);

        var filter = ToFilter(request);
        ContentGateHooks.RequireExport(_gate, request.AuthorizationToken, filter, request.DestinationPath);

        var cancellationToken = context.CancellationToken;
        var progress = Channel.CreateBounded<ConversationExportProgress>(new BoundedChannelOptions(1)
        {
            // A slow client sees the latest count rather than slowing the export down.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        ConversationExportResult result;
        try
        {
            var export = _writer.WriteAsync(
                _store.Value, filter, request.DestinationPath, new ChannelProgress(progress.Writer), cancellationToken);

            // Ends the progress loop below when the export finishes, fails or is cancelled.
            _ = export.ContinueWith(
                static (_, writer) => ((ChannelWriter<ConversationExportProgress>)writer!).TryComplete(),
                progress.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (var update in progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new Contract.ExportConversationsEvent
                {
                    Progress = new Contract.ExportProgress
                    {
                        TurnsWritten = update.TurnsWritten,
                        BytesWritten = update.BytesWritten,
                    },
                }).ConfigureAwait(false);
            }

            result = await export.ConfigureAwait(false);
        }
        catch (ConversationExportException ex)
        {
            _logger.LogWarning("Conversation export refused: {Reason}.", ex.Reason);
            throw new RpcException(new Status(ToStatusCode(ex.Reason), ex.Message));
        }

        await responseStream.WriteAsync(new Contract.ExportConversationsEvent
        {
            Result = new Contract.ExportResult
            {
                Path = result.Path,
                Conversations = result.Conversations,
                Turns = result.Turns,
                MissingBodies = result.MissingBodies,
                CorruptTurns = result.CorruptTurns,
                IncompleteSessions = result.IncompleteSessions,
                BodyBytes = result.BodyBytes,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the export filter from the request's wire fields, rejecting an out-of-range timestamp. Does no I/O,
    /// so it can run before the gate.
    /// </summary>
    /// <param name="request">The export request.</param>
    /// <returns>The filter the request asks for.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.InvalidArgument"/> when a timestamp is out of range.</exception>
    private static ConversationExportFilter ToFilter(Contract.ExportConversationsRequest request)
    {
        return ConversationExportWire.ToFilter(
            request.FromUtc,
            request.ToUtc,
            request.HasSessionId ? request.SessionId : null,
            request.HasHarness ? request.Harness : null,
            request.HasProvider ? request.Provider : null,
            request.HasModel ? request.Model : null);
    }

    /// <summary>Maps a writer refusal to the gRPC status the client sees.</summary>
    /// <param name="reason">Why the writer refused.</param>
    /// <returns>The status code.</returns>
    private static StatusCode ToStatusCode(ConversationExportFailure reason) => reason switch
    {
        ConversationExportFailure.InvalidDestination => StatusCode.InvalidArgument,
        ConversationExportFailure.InProgress => StatusCode.FailedPrecondition,
        ConversationExportFailure.InsufficientDiskSpace => StatusCode.FailedPrecondition,
        _ => StatusCode.Internal,
    };

    /// <summary>
    /// Hands the writer's per-turn reports to a one-slot channel the service drains onto the response stream, so
    /// the writer's thread never touches the stream and a slow client never stalls the export.
    /// </summary>
    /// <param name="writer">The channel to publish to.</param>
    private sealed class ChannelProgress(ChannelWriter<ConversationExportProgress> writer)
        : IProgress<ConversationExportProgress>
    {
        /// <inheritdoc/>
        public void Report(ConversationExportProgress value) => writer.TryWrite(value);
    }
}
