using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// A controllable <see cref="IConversationAdminClient"/> double for the Export dialog and its store. It reports a
/// fixed summary, streams the progress it is given and then a result, can be held mid-export with
/// <see cref="HoldBeforeResult"/>, and records exactly what each export call was given so a test can compare it
/// with what the passkey approval was bound to.
/// </summary>
internal sealed class FakeConversationAdminClient : IConversationAdminClient
{
    /// <summary>Gets or sets the summary <see cref="GetSummaryAsync"/> returns.</summary>
    public ConversationExportSummaryInfo Summary { get; set; } = new(SessionCount: 3, TurnCount: 1_250, BytesOnDisk: 5_242_880);

    /// <summary>Gets or sets the progress reports streamed before the result.</summary>
    public List<ConversationExportProgressInfo> Progress { get; set; } = [new(TurnsWritten: 2, BytesWritten: 2_048)];

    /// <summary>Gets or sets the result streamed last; its path is replaced by the requested destination.</summary>
    public ConversationExportResultInfo Result { get; set; } = new(
        Path: string.Empty, Conversations: 3, Turns: 7, MissingBodies: 0, CorruptTurns: 0, IncompleteSessions: 0, BodyBytes: 4_096);

    /// <summary>When set, <see cref="GetSummaryAsync"/> fails with it.</summary>
    public GrpcAdminException? SummaryFailure { get; set; }

    /// <summary>When set, <see cref="ExportAsync"/> fails with it as soon as it is enumerated.</summary>
    public GrpcAdminException? ExportFailure { get; set; }

    /// <summary>When set, the export waits for it after the progress reports and before the result.</summary>
    public Task? HoldBeforeResult { get; set; }

    /// <summary>Gets the filters passed to <see cref="ExportAsync"/>, in order.</summary>
    public List<ConversationExportFilterInfo> Filters { get; } = [];

    /// <summary>Gets the destinations passed to <see cref="ExportAsync"/>, in order.</summary>
    public List<string> Destinations { get; } = [];

    /// <summary>Gets the authorization tokens passed to <see cref="ExportAsync"/>, in order.</summary>
    public List<string> Authorizations { get; } = [];

    /// <inheritdoc/>
    public Task<ConversationExportSummaryInfo> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        SummaryFailure is null ? Task.FromResult(Summary) : Task.FromException<ConversationExportSummaryInfo>(SummaryFailure);

    /// <inheritdoc/>
    public async IAsyncEnumerable<ConversationExportEvent> ExportAsync(
        ConversationExportFilterInfo filter,
        string destinationPath,
        string authorizationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Filters.Add(filter);
        Destinations.Add(destinationPath);
        Authorizations.Add(authorizationToken);
        if (ExportFailure is not null) throw ExportFailure;

        foreach (var progress in Progress)
        {
            yield return new ConversationExportEvent(Progress: progress, Result: null);
        }

        if (HoldBeforeResult is not null) await HoldBeforeResult.WaitAsync(cancellationToken);

        yield return new ConversationExportEvent(Progress: null, Result: Result with { Path = destinationPath });
    }
}
