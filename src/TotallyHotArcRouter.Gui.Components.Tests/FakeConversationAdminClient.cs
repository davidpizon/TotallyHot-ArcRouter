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

    // ---- Import (#165 phase 4) ----

    /// <summary>Gets or sets the staged copy <see cref="StageImportAsync"/> returns.</summary>
    public StagedImportInfo Staged { get; set; } = new(
        StagedId: "staged-1", Sha256: new string('a', 64), SizeBytes: 4_096, ExpiresAtUtc: new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Gets the import progress reports streamed before the result.</summary>
    private List<ConversationImportProgressInfo> ImportProgress { get; } = [new(TurnsProcessed: 2, TurnsStored: 2)];

    /// <summary>Gets or sets the result the import streams last.</summary>
    public ConversationImportResultInfo ImportResult { get; set; } = new(
        ImportId: "import-1", SessionsAdded: 2, SessionsSkipped: 1, TurnsAdded: 5, TurnsAlreadyPresent: 3, TurnsConflictKept: 0,
        TurnsRejected: 0, TurnsToGrade: 5, BodyBytes: 8_192, TurnsDueForRetention: 0);

    /// <summary>When set, the import waits for it after the progress reports and before the result.</summary>
    public Task? HoldBeforeImportResult { get; set; }

    /// <summary>Gets or sets the past imports <see cref="ListImportsAsync"/> returns.</summary>
    public List<ConversationImportRowInfo> PastImports { get; set; } = [];

    /// <summary>When set, <see cref="StageImportAsync"/> fails with it.</summary>
    public GrpcAdminException? StageFailure { get; set; }

    /// <summary>When set, <see cref="ImportAsync"/> fails with it as soon as it is enumerated.</summary>
    public GrpcAdminException? ImportFailure { get; set; }

    /// <summary>When set, <see cref="DeleteImportAsync"/> fails with it.</summary>
    public GrpcAdminException? DeleteFailure { get; set; }

    /// <summary>Gets the source paths passed to <see cref="StageImportAsync"/>, in order.</summary>
    public List<string> StagedSources { get; } = [];

    /// <summary>Gets the staged ids passed to <see cref="ImportAsync"/>, in order.</summary>
    public List<string> ImportedStagedIds { get; } = [];

    /// <summary>Gets the options passed to <see cref="ImportAsync"/>, in order.</summary>
    public List<ConversationImportOptionsInfo> ImportOptions { get; } = [];

    /// <summary>Gets the authorization tokens passed to <see cref="ImportAsync"/>, in order.</summary>
    public List<string> ImportAuthorizations { get; } = [];

    /// <summary>Gets the ids passed to <see cref="DeleteImportAsync"/>, in order.</summary>
    public List<string> DeletedImports { get; } = [];

    /// <inheritdoc/>
    public Task<StagedImportInfo> StageImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        StagedSources.Add(sourcePath);
        return StageFailure is null ? Task.FromResult(Staged) : Task.FromException<StagedImportInfo>(StageFailure);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ConversationImportEvent> ImportAsync(
        string stagedId,
        ConversationImportOptionsInfo options,
        string authorizationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ImportedStagedIds.Add(stagedId);
        ImportOptions.Add(options);
        ImportAuthorizations.Add(authorizationToken);
        if (ImportFailure is not null) throw ImportFailure;

        foreach (var progress in ImportProgress)
        {
            yield return new ConversationImportEvent(Progress: progress, Result: null);
        }

        if (HoldBeforeImportResult is not null) await HoldBeforeImportResult.WaitAsync(cancellationToken);

        yield return new ConversationImportEvent(Progress: null, Result: ImportResult);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ConversationImportRowInfo>> ListImportsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationImportRowInfo>>([.. PastImports]);

    /// <inheritdoc/>
    public Task<ConversationImportDeleteInfo> DeleteImportAsync(string importId, CancellationToken cancellationToken = default)
    {
        DeletedImports.Add(importId);
        if (DeleteFailure is not null) return Task.FromException<ConversationImportDeleteInfo>(DeleteFailure);

        PastImports.RemoveAll(row => row.ImportId == importId);
        return Task.FromResult(new ConversationImportDeleteInfo(SessionsDeleted: 1, TurnsRetired: 0));
    }
}
