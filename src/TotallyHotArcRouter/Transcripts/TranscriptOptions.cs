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
    /// Gets whether transcript capture is enabled. Defaults to <see langword="false"/> - the plan's
    /// "Privacy-first transcripts" ground rule: capture defaults off, and no <c>transcripts.db</c> table
    /// is ever created while this stays <see langword="false"/>. An operator opts in explicitly, aware
    /// that enabling this persists raw prompt/response text, unlike every other learned-memory table in
    /// this codebase.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets whether the embedding backfill background service is enabled
    /// (docs/router/self-organizing-classification-plan.md Phase T1d). Defaults to <see langword="false"/>.
    /// When enabled, a background job recovers training samples whose embeddings were skipped when the
    /// embedding budget expired or the model was not warm. Independent of <see cref="Enabled"/>, gated
    /// by <c>&amp; <see cref="Enabled"/></c> so backfill never runs when transcripts are disabled.
    /// </summary>
    public bool EnableEmbeddingBackfill { get; init; }

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
