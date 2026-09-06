using System.ComponentModel.DataAnnotations;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Configuration for the G-Eval shadow judge (docs/router/geval-shadow-scoring-plan.md Phase G1).
/// </summary>
/// <remarks>
/// <b>Not bound from <c>appsettings.json</c>.</b> <see cref="Enabled"/> and <see cref="ModelName"/> are
/// operator-facing settings owned by the <c>router_settings</c> table and layered on by
/// <see cref="JudgeSettingsConfigureOptions"/>, exactly as
/// <see cref="Router.RouterSettingsConfigureOptions"/> does for <c>RoutingOptions</c>. The judge's backbone
/// is no longer a hardcoded local endpoint either: it is whichever free model the operator configured in
/// the Providers screen, resolved per call by <see cref="JudgeModelSelector"/>. The remaining properties
/// here are operational bounds with coded defaults that no configuration source overrides.
/// </remarks>
public sealed class JudgeOptions
{
    /// <summary>
    /// Gets whether the judge is enabled. The literal initializer here is <see langword="false"/>, but the
    /// effective default is computed by <see cref="JudgeSettingsConfigureOptions"/>: absent a stored
    /// setting, the judge turns on when an eligible free backbone can be resolved and stays off when it
    /// cannot. While this is <see langword="false"/> no response text is cached, no job is enqueued, no
    /// HTTP call is made, and no row is written to <c>judge_shadow_scores</c> - the quality verifier then
    /// scores from static analysis alone. Overridden by the <c>router_settings</c> row
    /// <see cref="Router.RouterSettingsStore.JudgeEnabledKey"/>, so the System Settings window can toggle
    /// it live and an explicit operator choice always beats the auto-detect; every consumer reads it
    /// through <c>IOptionsMonitor</c> rather than capturing it once.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the operator's chosen judge model, as a client-facing model name from the Providers screen.
    /// Empty (the default) means <b>automatic</b>: <see cref="JudgeModelSelector"/> takes the first
    /// eligible free model in configuration order. A name that is no longer eligible - its provider was
    /// switched off, the model was stopped, or it disappeared upstream - also falls back to automatic
    /// rather than failing, since a shadow-scoring path must degrade quietly. Overridden by the
    /// <c>router_settings</c> row <see cref="Router.RouterSettingsStore.JudgeModelNameKey"/>.
    /// </summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the version tag stamped on every shadow row's <c>judge_prompt_version</c> column and used as
    /// the auto-CoT cache guard. G1 scopes the auto-CoT generation-and-caching step down to a static
    /// per-dimension prompt constant (see <see cref="GEvalJudgeClient"/>'s remarks); this version still
    /// exists so a future prompt change is distinguishable in the shadow table. Defaults to
    /// <c>g-eval-v1</c>.
    /// </summary>
    public string PromptVersion { get; init; } = "g-eval-v1";

    /// <summary>
    /// Gets the time-to-live, in seconds, for an entry in <see cref="PendingResponseTextCache"/> before it
    /// is treated as evicted. Defaults to 300 (5 minutes) - long enough for the drain worker to catch up
    /// under ordinary queue depth, short enough that an unjudged response does not linger in memory.
    /// </summary>
    [Range(1, 86_400)]
    public int CacheTtlSeconds { get; init; } = 300;

    /// <summary>
    /// Gets the maximum number of entries <see cref="PendingResponseTextCache"/> holds before the oldest
    /// are evicted. Defaults to 2,000, mirroring <c>RoutingOptions.PendingEmbeddingCacheCapacity</c>'s
    /// default.
    /// </summary>
    [Range(1, 1_000_000)]
    public int CacheCapacity { get; init; } = 2_000;

    /// <summary>
    /// Gets the maximum size, in UTF-16 characters, of a single response text cached for judging. Longer
    /// text is truncated before it is cached, mirroring <c>QualityOptions.MaxCapturedOutputBytes</c>'s
    /// philosophy of bounding worst-case memory regardless of traffic. Defaults to 65,536 characters.
    /// </summary>
    [Range(256, 10_000_000)]
    public int MaxCachedTextChars { get; init; } = 65_536;

    /// <summary>
    /// Gets the capacity of the bounded background channel <see cref="JudgeShadowScoreDispatcher"/> enqueues
    /// onto. A full channel sheds the newest job (logged and dropped) rather than blocking the caller.
    /// Defaults to 500.
    /// </summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; init; } = 500;

    /// <summary>
    /// Gets the request timeout, in seconds, for a single judge HTTP call. Defaults to 30 - local
    /// inference can be slow, but a shadow-scoring call must not hang the drain worker indefinitely.
    /// </summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the maximum age in days for rows in <c>judge_shadow_scores</c> before they become eligible for
    /// deletion by the retention purge, mirroring <see cref="Transcripts.TranscriptOptions.RetentionDays"/>.
    /// Defaults to 30 days.
    /// </summary>
    [Range(1, 365)]
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// Gets the maximum number of rows <c>judge_shadow_scores</c> is allowed to hold before the oldest rows
    /// are deleted to bring it back under this limit, mirroring
    /// <see cref="Transcripts.TranscriptOptions.MaxRows"/>. Defaults to 50,000.
    /// </summary>
    [Range(100, 1_000_000)]
    public int MaxRows { get; init; } = 50_000;
}