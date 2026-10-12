using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Import;
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
    private readonly ConversationImporter? _importer;
    private readonly ImportStagingArea? _staging;
    private readonly ConversationImportCatalog? _imports;
    private readonly IOptionsMonitor<RoutingOptions>? _routing;

    /// <summary>Initializes a new instance of the <see cref="ConversationAdminGrpcService"/> class.</summary>
    /// <param name="store">The lazily opened session store; not opened until an export runs or the store already exists.</param>
    /// <param name="writer">The export writer. Share one instance so its single-flight rule holds across calls.</param>
    /// <param name="gate">Verifies and consumes the one-operation passkey authorization an export requires.</param>
    /// <param name="database">Names where the session folder lives, so a summary can tell whether anything was ever captured.</param>
    /// <param name="logger">Receives export outcomes; optional so a hand-built service in a test needs no logging.</param>
    /// <param name="importer">Imports a staged zip. Share one instance so its single-flight rule holds across calls. Optional: without it the import RPCs are unavailable.</param>
    /// <param name="staging">Makes the private, hashed copies an import reads. Optional, as <paramref name="importer"/>.</param>
    /// <param name="imports">Lists and deletes past imports. Optional, as <paramref name="importer"/>.</param>
    /// <param name="routing">Supplies the Sample Size the import result's retention report is measured against; optional.</param>
    public ConversationAdminGrpcService(
        Lazy<SessionStore> store,
        ConversationExportWriter writer,
        ContentGate gate,
        TranscriptDatabase database,
        ILogger<ConversationAdminGrpcService>? logger = null,
        ConversationImporter? importer = null,
        ImportStagingArea? staging = null,
        ConversationImportCatalog? imports = null,
        IOptionsMonitor<RoutingOptions>? routing = null)
    {
        _importer = importer;
        _staging = staging;
        _imports = imports;
        _routing = routing;
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

    /// <inheritdoc/>
    public override Task<Contract.StageImportResponse> StageImport(
        Contract.StageImportRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, staging) = RequireImport();

        try
        {
            var staged = staging.Stage(_store.Value, request.SourcePath, context.CancellationToken);
            return Task.FromResult(new Contract.StageImportResponse
            {
                StagedId = staged.Id,
                Sha256 = staged.Sha256Hex,
                SizeBytes = staged.SizeBytes,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(staged.ExpiresAtUtc),
            });
        }
        catch (ConversationImportException ex)
        {
            _logger.LogWarning("Conversation import staging refused: {Reason}.", ex.Reason);
            throw new RpcException(new Status(ToStatusCode(ex.Reason), ex.Message));
        }
    }

    /// <inheritdoc/>
    public override async Task ImportConversations(
        Contract.ImportConversationsRequest request,
        IServerStreamWriter<Contract.ImportConversationsEvent> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var (importer, staging) = RequireImport();

        // The digest is the one the router computed while it staged the copy. A client cannot name a different
        // one, so an approval for one zip cannot import another.
        var staged = staging.TryGet(request.StagedId)
                     ?? throw new RpcException(new Status(
                         StatusCode.NotFound, "No staged import has that id. It was imported, discarded, or lapsed."));
        ContentGateHooks.RequireImport(_gate, request.AuthorizationToken, staged.Sha256Hex);

        var progress = Channel.CreateBounded<ConversationImportProgress>(new BoundedChannelOptions(1)
        {
            // A slow client sees the latest count rather than slowing the import down.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        ConversationImportResult result;
        Task<ConversationImportResult>? import = null;
        try
        {
            import = importer.ImportAsync(
                _store.Value,
                staged.StagedPath,
                ConversationImportWire.ToOptions(request.Options),
                new ChannelImportProgress(progress.Writer),
                context.CancellationToken,
                staged.Sha256Hex,
                _routing?.CurrentValue.EmbeddingMemoryCapacity);

            // Ends the progress loop below when the import finishes, fails or is cancelled.
            _ = import.ContinueWith(
                static (_, writer) => ((ChannelWriter<ConversationImportProgress>)writer!).TryComplete(),
                progress.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (var update in progress.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new Contract.ImportConversationsEvent
                {
                    Progress = new Contract.ImportProgress
                    {
                        TurnsProcessed = update.TurnsProcessed,
                        TurnsStored = update.TurnsStored,
                    },
                }).ConfigureAwait(false);
            }

            result = await import.ConfigureAwait(false);
        }
        catch (ConversationImportException ex)
        {
            _logger.LogWarning("Conversation import refused: {Reason}.", ex.Reason);
            throw new RpcException(new Status(ToStatusCode(ex.Reason), ex.Message));
        }
        finally
        {
            // A cancelled call stops waiting before the import has noticed; the copy is deleted only once the import
            // has let go of it (an open file cannot be deleted on Windows and would be left until restart).
            if (import is not null)
            {
                try
                {
                    await import.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ConversationImportException or OperationCanceledException or IOException)
                {
                    // Already reported, or cancelled; only the wait matters here.
                }
            }

            // The copy has served its purpose whatever happened; the next attempt stages the zip again.
            staging.Discard(staged.Id);
        }

        await responseStream.WriteAsync(new Contract.ImportConversationsEvent
        {
            Result = new Contract.ImportResult
            {
                ImportId = result.ImportId.ToString("D"),
                SessionsAdded = result.SessionsAdded,
                SessionsSkipped = result.SessionsSkipped,
                TurnsAdded = result.TurnsAdded,
                TurnsAlreadyPresent = result.TurnsAlreadyPresent,
                TurnsConflictKept = result.TurnsConflictKept,
                TurnsRejected = result.TurnsRejected,
                TurnsToGrade = result.TurnsToGrade,
                BodyBytes = result.BodyBytes,
                TurnsDueForRetention = result.TurnsDueForRetention,
            },
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override Task<Contract.ListConversationImportsResponse> ListConversationImports(
        Contract.ListConversationImportsRequest request, ServerCallContext context)
    {
        var catalog = _imports ?? throw ImportUnavailable();
        var response = new Contract.ListConversationImportsResponse();
        foreach (var row in catalog.List())
        {
            response.Imports.Add(new Contract.ConversationImportInfo
            {
                ImportId = row.ImportId.ToString("D"),
                ImportedAtUtc = Timestamp.FromDateTimeOffset(row.ImportedAtUtc),
                SourceSha256 = row.SourceSha256,
                SessionsAdded = row.SessionsAdded,
                TurnsAdded = row.TurnsAdded,
                TurnsSkipped = row.TurnsSkipped,
                TurnsRejected = row.TurnsRejected,
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.DeleteConversationImportResponse> DeleteConversationImport(
        Contract.DeleteConversationImportRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalog = _imports ?? throw ImportUnavailable();
        if (!Guid.TryParse(request.ImportId, out var importId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "'import_id' is not a valid import id."));
        }

        ConversationImportDeleteResult? removed;
        try
        {
            removed = catalog.Delete(importId);
        }
        catch (ConversationImportException ex)
        {
            throw new RpcException(new Status(ToStatusCode(ex.Reason), ex.Message));
        }

        if (removed is null) throw new RpcException(new Status(StatusCode.NotFound, "No import has that id."));
        _logger.LogInformation(
            "Conversation import deleted: {Sessions} sessions removed, {Turns} turns retired.",
            removed.SessionsDeleted, removed.TurnsRetired);
        return Task.FromResult(new Contract.DeleteConversationImportResponse
        {
            SessionsDeleted = removed.SessionsDeleted,
            TurnsRetired = removed.TurnsRetired,
        });
    }

    /// <summary>Returns the import collaborators, or refuses when this router was built without them.</summary>
    /// <returns>The importer and the staging area.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.Unimplemented"/> when import is not wired.</exception>
    private (ConversationImporter Importer, ImportStagingArea Staging) RequireImport() =>
        (_importer ?? throw ImportUnavailable(), _staging ?? throw ImportUnavailable());

    /// <summary>Builds the refusal for an import call on a router without import wired.</summary>
    /// <returns>The exception to throw.</returns>
    private static RpcException ImportUnavailable() =>
        new(new Status(StatusCode.Unimplemented, "Conversation import is not available on this router."));

    /// <summary>Maps an importer refusal to the gRPC status the client sees.</summary>
    /// <param name="reason">Why the importer refused.</param>
    /// <returns>The status code.</returns>
    private static StatusCode ToStatusCode(ConversationImportFailure reason) => reason switch
    {
        ConversationImportFailure.InProgress => StatusCode.FailedPrecondition,
        ConversationImportFailure.InsufficientDiskSpace => StatusCode.FailedPrecondition,
        ConversationImportFailure.ArchiveNotFound => StatusCode.NotFound,
        ConversationImportFailure.NotStaged => StatusCode.NotFound,
        ConversationImportFailure.InvalidArchive => StatusCode.InvalidArgument,
        ConversationImportFailure.UnsupportedVersion => StatusCode.InvalidArgument,
        ConversationImportFailure.InvalidSource => StatusCode.InvalidArgument,
        ConversationImportFailure.TooManyStaged => StatusCode.ResourceExhausted,
        _ => StatusCode.Internal,
    };

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

    /// <summary>Hands the importer's per-turn reports to a one-slot channel, as <see cref="ChannelProgress"/> does for export.</summary>
    /// <param name="writer">The channel to publish to.</param>
    private sealed class ChannelImportProgress(ChannelWriter<ConversationImportProgress> writer)
        : IProgress<ConversationImportProgress>
    {
        /// <inheritdoc/>
        public void Report(ConversationImportProgress value) => writer.TryWrite(value);
    }

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
