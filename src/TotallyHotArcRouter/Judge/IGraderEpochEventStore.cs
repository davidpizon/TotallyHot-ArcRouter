namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Append-only history of who graded a kind (issue #304 phase 8). Rows are never updated and never
/// deleted. Phase 9 reads <see cref="ListForPlot"/> and places it on a time axis beside token cost;
/// this store does not join those series.
/// </summary>
public interface IGraderEpochEventStore
{
    /// <summary>The epoch type written when a local grader begins grading a kind on its own.</summary>
    public const string LocalTakeover = "local_takeover";

    /// <summary>The epoch type written when the teacher takes charge of a kind again.</summary>
    public const string TeacherResumed = "teacher_resumed";

    /// <summary>The epoch type written when the teacher model or prompt version changes.</summary>
    public const string TeacherChanged = "teacher_changed";

    /// <summary>The epoch type written when the BGE model id changes.</summary>
    public const string BgeChanged = "bge_changed";

    /// <summary>The epoch type written when the feature-schema version changes.</summary>
    public const string FeatureSchemaChanged = "feature_schema_changed";

    /// <summary>Inserts <paramref name="row"/> on the open transaction. There is no update and no delete.</summary>
    /// <param name="row">The event. Question text, response text, and vectors are not fields on it.</param>
    void Insert(GraderEpochEventRow row);

    /// <summary>
    /// Runs <paramref name="work"/> inside one database transaction. A throw rolls the insert back.
    /// File renames inside <paramref name="work"/> are not enlisted; the caller orders them after the insert.
    /// </summary>
    /// <param name="work">The inserts and the file rename that must commit together.</param>
    void Execute(Action work);

    /// <summary>Every row, oldest first. Phase 9 draws this list. It does not filter by kind.</summary>
    /// <returns>The append-only history.</returns>
    IReadOnlyList<GraderEpochEventRow> ListForPlot();

    /// <summary>
    /// The newest <c>local_takeover</c> or <c>teacher_resumed</c> row for <paramref name="kind"/>.
    /// An identity row is not an authority row and is not returned here.
    /// </summary>
    /// <param name="kind"><see cref="GraderEpochKinds.Code"/> or <see cref="GraderEpochKinds.NonCode"/>.</param>
    /// <returns>The latest authority row, or null when this kind has none.</returns>
    GraderEpochEventRow? LatestAuthority(string kind);

    /// <summary>The newest row of <paramref name="epochType"/> for <paramref name="kind"/>.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="epochType">One epoch type constant on this interface.</param>
    /// <returns>That row, or null when this kind has none of that type.</returns>
    GraderEpochEventRow? LatestOfType(string kind, string epochType);
}

/// <summary>
/// One <c>grader_epoch_events</c> row. Null columns stay null and are never stored as 0. No property
/// holds question text, response text, or a vector.
/// </summary>
public sealed record GraderEpochEventRow
{
    /// <summary>Gets the SQLite row id. Zero on a row that has not been read back.</summary>
    public long Id { get; init; }

    /// <summary>Gets <see cref="TimeProvider.GetUtcNow"/> formatted with the round-trip <c>O</c> format.</summary>
    public required string AtUtc { get; init; }

    /// <summary>Gets <c>code</c> or <c>non-code</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the epoch type.</summary>
    public required string EpochType { get; init; }

    /// <summary>Gets why this row was appended.</summary>
    public required string Cause { get; init; }

    /// <summary>Gets the artifact this row is about. A written row has it.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Gets the artifact that was in charge before this row, or null when there was none.</summary>
    public string? PreviousArtifactId { get; init; }

    /// <summary>Gets the spot-check window size. Set only for a window cause. Otherwise null, never 0.</summary>
    public int? WindowSize { get; init; }

    /// <summary>Gets the window's mean absolute miss. Set only for a window cause. Otherwise null, never 0.</summary>
    public double? MeanMiss { get; init; }

    /// <summary>Gets the teacher model before a <c>teacher_changed</c> row.</summary>
    public string? PreviousTeacherModel { get; init; }

    /// <summary>Gets the teacher model on a <c>teacher_changed</c> row.</summary>
    public string? TeacherModel { get; init; }

    /// <summary>Gets the prompt version before a <c>teacher_changed</c> row.</summary>
    public string? PreviousPromptVersion { get; init; }

    /// <summary>Gets the prompt version on a <c>teacher_changed</c> row.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>
    /// Gets whether <c>rollback.json</c> existed. Set only on an identity row: 1 when the file existed,
    /// 0 when it did not. Authority rows store null, never 0.
    /// </summary>
    public int? RollbackMarkPresent { get; init; }

    /// <summary>Gets the artifact's BGE id on a <c>bge_changed</c> row.</summary>
    public string? PreviousBgeModel { get; init; }

    /// <summary>Gets the current BGE id on a <c>bge_changed</c> row.</summary>
    public string? BgeModel { get; init; }

    /// <summary>Gets the artifact's feature-schema version on a <c>feature_schema_changed</c> row.</summary>
    public int? PreviousFeatureSchemaVersion { get; init; }

    /// <summary>Gets the current feature-schema version on a <c>feature_schema_changed</c> row.</summary>
    public int? FeatureSchemaVersion { get; init; }
}
