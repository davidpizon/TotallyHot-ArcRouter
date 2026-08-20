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
}
