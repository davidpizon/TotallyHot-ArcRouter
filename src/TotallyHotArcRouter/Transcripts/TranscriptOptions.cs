using System.ComponentModel.DataAnnotations;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Configuration for the opt-in transcript store (docs/router/self-organizing-classification-plan.md
/// Phase T1). Bound from the <c>Transcript</c> configuration section.
/// </summary>
public sealed class TranscriptOptions
{
    /// <summary>Gets the configuration section name used for transcript settings.</summary>
    public const string SectionName = "Transcript";

    /// <summary>
    /// Gets whether transcript capture is enabled. Defaults to <see langword="true"/>, matching the
    /// System Settings window's Transcription Capture toggle defaulting to On - no <c>transcripts.db</c>
    /// table is created until the first row is actually written, so a fresh install that never routes a
    /// request still creates nothing. Overridable at runtime through the <c>router_settings</c> SQLite
    /// table (<see cref="Router.RouterSettingsStore"/>) via <see cref="TranscriptSettingsConfigureOptions"/>,
    /// which - when a stored value is present - beats whatever <c>appsettings.json</c> bound here, which in
    /// turn beats this property's own coded default. A stored override takes effect immediately through
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>, without a process restart - see
    /// <see cref="Router.RouterSettingsAdminGrpcService"/>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets whether the embedding backfill background service is enabled
    /// (docs/router/self-organizing-classification-plan.md Phase T1d). Defaults to <see langword="false"/>.
    /// When enabled, a background job recovers training samples whose embeddings were skipped when the
    /// embedding budget expired or the model was not warm. Independent of <see cref="Enabled"/>, gated
    /// by <c>&amp; <see cref="Enabled"/></c> so backfill never runs when transcripts are disabled.
    /// </summary>
    public bool EnableEmbeddingBackfill { get; init; }

    /// <summary>
    /// Gets whether the background quality rescan is enabled. Defaults to <see langword="false"/>. When
    /// enabled, <see cref="QualityRescanService"/> grades saved transcript rows that the live path never
    /// graded, and re-scores rows whose <c>scorer_version</c> is stale so the corpus can be re-measured
    /// under a changed scorer. Independent of <see cref="Enabled"/>, and gated by it so the rescan never
    /// runs when transcripts are disabled - with capture off there is no saved task data to grade.
    /// </summary>
    public bool EnableQualityRescan { get; init; }

    /// <summary>
    /// Gets how often <see cref="QualityRescanService"/> sweeps for rows needing a grade, in minutes.
    /// Defaults to 5, matching <see cref="EmbeddingBackfillService"/>'s interval.
    /// </summary>
    [Range(1, 1_440)]
    public int QualityRescanIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// Gets how many rows <see cref="QualityRescanService"/> grades per sweep. Defaults to 100, matching
    /// <see cref="EmbeddingBackfillService"/>'s batch size. Bounded because each row may cost a judge call
    /// once the LLM grader portfolio lands, so a sweep's upper cost has to stay predictable.
    /// </summary>
    [Range(1, 10_000)]
    public int QualityRescanBatchSize { get; init; } = 100;

    /// <summary>
    /// Gets the maximum age in days for rows in <c>request_transcripts</c> before they become eligible
    /// for deletion by the retention purge (docs/router/self-organizing-classification-plan.md Phase T1e).
    /// Rows older than this are deleted by <see cref="TranscriptRetentionService"/>; defaults to 30 days.
    /// </summary>
    [Range(1, 365)]
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// Gets the maximum number of rows <c>request_transcripts</c> is allowed to hold before the oldest
    /// rows are deleted to bring it back under this limit (docs/router/self-organizing-classification-plan.md
    /// Phase T1e). Defaults to 50,000 - a conservative bound that enforces bounded retention without
    /// frequent purges under ordinary traffic.
    /// </summary>
    [Range(100, 1_000_000)]
    public int MaxRows { get; init; } = 50_000;
}
