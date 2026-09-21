using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Charts;

namespace TotallyHot.ArcRouter.Gui.Models;

/// <summary>Severity of a single routing-decision log step.</summary>
public enum StepStatus
{
    /// <summary>The step succeeded normally.</summary>
    Ok,

    /// <summary>The step completed but flags something worth the user's attention.</summary>
    Warn,

    /// <summary>Purely informational step; no success/failure connotation.</summary>
    Info
}

/// <summary>One step in the routing decision log shown in the Live Stream inspector.</summary>
public sealed record RoutingStep(StepStatus Status, string Message);

/// <summary>Prompt/completion token volume for one time slot.</summary>
public sealed record TokenBucket(string Slot, decimal Prompt, decimal Completion);

/// <summary>Market-share percentage (and display color) for one model.</summary>
public sealed record ModelShare(string Model, decimal Value, string Color);

/// <summary>A single turn (one multi-step agentic workflow) within a conversation.</summary>
public sealed record ConversationTurn(
    string Id,
    string Agent,
    string Model,
    int TurnNumber,
    int PromptTokens,
    int CompletionTokens,
    decimal RoutingRoi,
    decimal TotalCost,
    int ToolExecutionSteps,
    decimal CacheHitRate,
    int TimeToFirstTokenMs,
    decimal ContextBufferPercent,
    string Timestamp,
    IReadOnlyList<RoutingStep> RoutingSteps,
    string? RequestSummary = null,
    string? ResponseSummary = null,
    bool IsFallback = false,
    // Real UTC instant of the turn (the display `Timestamp` above is a lossy "HH:mm:ss" string).
    // Used by the Cost Analytics tab to bucket turns onto a time axis. Optional/defaulted so test
    // fixtures and older call sites keep compiling; live turns get the real value from
    // LiveConversationMapper.
    DateTimeOffset TimestampUtc = default,
    // How TotalCost was arrived at (a Telemetry.CostConfidence name, e.g. "Catalog", "Unknown"), or null
    // when the source has no confidence concept. Backs the turn card's cost-stat confidence indicator
    // (docs/router/token-tracking-implementation-plan.md Phase 3, §5.6).
    string? CostConfidence = null,
    // The client's literal requested model, the model that actually served, and why they differ (a
    // Telemetry.RoutingSubstitutionReason name), or null when the source has no live-routing concept.
    // Plumbed through by Phase M2 (docs/router/orchestrator-live-path-plan.md §M2.2) and rendered by
    // Phase M3.1: LiveConversationMapper.BuildRoutingSteps turns a visible reason (anything but None or
    // AutoSelect) into the Live Stream inspector's substitution warning step.
    string? RequestedModel = null,
    string? RoutedModel = null,
    string? SubstitutionReason = null);

/// <summary>A conversation (session) whose turns are shown in the Live Stream tab.</summary>
/// <param name="Id">The session id.</param>
/// <param name="Title">The card's display title (marks untracked/synthesized sessions distinctly).</param>
/// <param name="FirstTimestamp">The first turn's display timestamp.</param>
/// <param name="LastTimestamp">The most recent turn's display timestamp.</param>
/// <param name="TotalCost">The sum of every turn's known cost.</param>
/// <param name="TotalPromptTokens">The sum of every turn's prompt tokens.</param>
/// <param name="TotalCompletionTokens">The sum of every turn's completion tokens.</param>
/// <param name="HasFallbackTurns">Whether any turn was served by fallback routing.</param>
/// <param name="Turns">This conversation's turns, in chronological order.</param>
/// <param name="UnpricedTurns">
/// How many of <paramref name="Turns"/> had no cost at all, and so contributed nothing to
/// <paramref name="TotalCost"/> - a non-zero value means the total is a floor, not a complete sum (§5.6).
/// </param>
/// <param name="IsUsedForTraining">
/// Whether any turn's persisted transcript was folded into the live-learning corpus
/// (docs/router/sessions-tab-training-data-plan.md Phase 2) - the Sessions tab's "used for live training"
/// badge. Always <see langword="false"/> for a session sourced only from the live telemetry stream
/// (<see cref="TotallyHot.ArcRouter.Gui.Services.LiveConversationMapper"/>), since the linkage this flag
/// reports is only known once a transcript has been persisted and its embedding backfilled.
/// </param>
public sealed record Conversation(
    string Id,
    string Title,
    string FirstTimestamp,
    string LastTimestamp,
    decimal TotalCost,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    bool HasFallbackTurns,
    IReadOnlyList<ConversationTurn> Turns,
    int UnpricedTurns = 0,
    bool IsUsedForTraining = false);

/// <summary>
/// Offline fallback fixtures for dashboard tabs that still need a corpus when the proxy has sent no
/// live telemetry. Sessions no longer reads this class: that tab shows live and persisted conversations
/// only (empty until the proxy or history store supplies them). Cost Analytics uses
/// <see cref="BuildMetricHistory"/> when no live turns exist; Model Distribution uses
/// <see cref="TokenBuckets"/> and <see cref="ModelShares"/> the same way. Tool Steps and Context Buffer
/// keep these fallbacks until they have live sources.
/// </summary>
public static class MockData
{
    /// <summary>Mock daily prompt/completion token volumes for the Model Distribution tab.</summary>
    public static readonly IReadOnlyList<TokenBucket> TokenBuckets =
    [
        new(Slot: "Mon", 2_840_000m, 980_000m),
        new(Slot: "Tue", 3_120_000m, 1_140_000m),
        new(Slot: "Wed", 4_200_000m, 1_680_000m),
        new(Slot: "Thu", 3_890_000m, 1_520_000m),
        new(Slot: "Fri", 2_960_000m, 1_020_000m),
        new(Slot: "Sat", 1_840_000m, 620_000m),
        new(Slot: "Sun", 1_240_000m, 380_000m)
    ];

    /// <summary>Mock per-model spend for the Report Card tab's offline/no-proxy fallback.</summary>
    public static readonly IReadOnlyList<ModelSpendRowView> ReportCardSpend =
    [
        new(Model: "gpt-4o-mini", 12.40m, 84),
        new(Model: "claude-3-haiku", 8.10m, 51),
        new(Model: "gemini-1.5-flash", 3.20m, 37)
    ];

    /// <summary>Mock A–F grade mix for the Report Card tab's offline/no-proxy fallback.</summary>
    public static readonly IReadOnlyList<GradeMixRowView> ReportCardGradeMix =
    [
        new(Grade: "A", 42, 42.0m),
        new(Grade: "B", 31, 31.0m),
        new(Grade: "C", 18, 18.0m),
        new(Grade: "D", 6, 6.0m),
        new(Grade: "F", 3, 3.0m)
    ];

    /// <summary>Mock per-model score delta for the Report Card tab's offline/no-proxy fallback.</summary>
    public static readonly IReadOnlyList<ModelScoreDeltaRowView> ReportCardScoreDelta =
    [
        new(Model: "gpt-4o-mini", 0.082, 40),
        new(Model: "claude-3-haiku", 0.031, 28),
        new(Model: "gemini-1.5-flash", -0.019, 22)
    ];

    /// <summary>Mock mean score delta for the Report Card tab's offline/no-proxy fallback.</summary>
    public const double ReportCardMeanScoreDelta = 0.041;

    /// <summary>Mock scored-request count for the Report Card tab's offline/no-proxy fallback.</summary>
    public const int ReportCardScoredRequests = 100;

    /// <summary>Mock per-model token-volume market share for the Model Distribution tab.</summary>
    public static readonly IReadOnlyList<ModelShare> ModelShares =
    [
        new(Model: "gpt-4o-mini", 38m, Color: "#10b981"),
        new(Model: "claude-3-haiku", 22m, Color: "#38bdf8"),
        new(Model: "gemini-1.5-flash", 18m, Color: "#818cf8"),
        new(Model: "fallback-local", 10m, Color: "#f59e0b"),
        new(Model: "claude-3-5-sonnet", 7m, Color: "#fb7185"),
        new(Model: "text-embedding-3-small", 5m, Color: "#a78bfa")
    ];

    /// <summary>
    /// Models the mock history draws from, each with an approximate ($/M input, $/M output) price used
    /// to derive per-turn cost. Deliberately a spread of tiers (cheap routed models through a premium
    /// model and a $0 local fallback) so the Cost Analytics model-choice bars show a real mix.
    /// </summary>
    private static readonly (string Model, decimal InputPerM, decimal OutputPerM)[] HistoryModels =
    [
        ("gpt-4o-mini", 0.15m, 0.60m),
        ("claude-3-haiku", 0.25m, 1.25m),
        ("gemini-1.5-flash", 0.075m, 0.30m),
        ("claude-3-5-sonnet", 3.00m, 15.00m),
        ("fallback-cheapest-local", 0m, 0m)
    ];

    /// <summary>
    /// One mock session: how long ago its first turn was, how many turns it has, and how far apart the
    /// turns are. Chosen so every Cost Analytics time range is populated - two sessions land inside the
    /// last hour (each turn a distinct point), more inside the day/week/month, and several stretch back
    /// months for the all-time view.
    /// </summary>
    private static readonly (int StartMinutesAgo, int Turns, int SpacingMinutes)[] HistorySessions =
    [
        (45, 5, 6), // within the last hour
        (52, 3, 5), // within the last hour
        (6 * 60, 5, 9), // within the last day
        (14 * 60, 4, 12), // within the last day
        (2 * 24 * 60, 6, 15), // within the last week
        (4 * 24 * 60, 5, 20), // within the last week
        (10 * 24 * 60, 6, 18), // within the last month
        (20 * 24 * 60, 5, 22), // within the last month
        (45 * 24 * 60, 6, 16), // all-time
        (75 * 24 * 60, 5, 25), // all-time
        (110 * 24 * 60, 7, 14) // all-time
    ];

    /// <summary>
    /// Builds a deterministic, timestamped corpus of turn-level metrics anchored to <paramref name="now"/>,
    /// used by the Cost Analytics tab so every metric and time range renders even with no proxy running.
    /// Deterministic (fixed RNG seed) so the shapes are stable across renders; only the timestamps move
    /// with <paramref name="now"/> so the range windows stay populated. Live turns, when present, are
    /// merged on top of this by the component - this is the "mock fills the gaps" corpus.
    /// A handful of exemplar events are injected at fixed positions so each bespoke chart shows its
    /// special state within the default (Week) window: a token runaway, a TTFT spike, a fallback (which
    /// draws a below-zero ROI bar and a model handoff), and context sessions that breach the 90% line.
    /// </summary>
    public static IReadOnlyList<MetricTurnPoint> BuildMetricHistory(DateTimeOffset now)
    {
        var random = new Random(20260710);
        var points = new List<MetricTurnPoint>();

        for (var s = 0; s < HistorySessions.Length; s++)
        {
            var (startMinutesAgo, turns, spacing) = HistorySessions[s];
            var sessionId = $"hist-sess-{s + 1:D2}";
            // Each session mostly reuses one primary model (as agentic sessions do); a later turn may
            // fall back to the local model, so the per-bucket bars show occasional model switches.
            var primary = HistoryModels[s % HistoryModels.Length];

            var start = now.AddMinutes(-startMinutesAgo);
            // Context fills faster in some sessions than others; the heavier ones (growth ~20%/turn)
            // cross the 90% threshold on their later turns so the Context chart shows real breaches.
            var contextGrowth = 12m + s % 3 * 4m;

            for (var t = 0; t < turns; t++)
            {
                var timestamp = start.AddMinutes(t * spacing);
                if (timestamp > now) break;

                // Fixed exemplar events (see the summary) plus low-probability random texture.
                var forceFallback = s == 5 && t == 2;
                var forceRunaway = s == 2 && t == 3;
                var forceSpike = s == 3 && t == 2;

                var isFallback = forceFallback || (t > 0 && random.Next(0, 100) < 7);
                var (model, inputPerM, outputPerM) = isFallback ? HistoryModels[^1] : primary;

                // Prompt tokens compound turn-over-turn within a session (the hockey-stick shape);
                // a runaway turn injects a large recursive-loop spike (> 2.5x the prior turn).
                var basePrompt = 1500 + t * 1200 + random.Next(0, 900);
                var promptTokens = forceRunaway ? basePrompt + random.Next(90_000, 150_000) : basePrompt;
                var completionTokens = 250 + random.Next(0, 1300);

                var cost = promptTokens / 1_000_000m * inputPerM + completionTokens / 1_000_000m * outputPerM;
                // A demo counterfactual: the baseline would have spent 1.6x-4.0x this turn's cost, except
                // on a fallback turn where it would have been cheaper (a routing loss). Expressed as a
                // baseline cost rather than a saved-percentage because that is what the real feed carries.
                var baselineCost = isFallback ? cost * 0.7m : cost * (1.6m + random.Next(0, 240) / 100m);
                var toolSteps = 1 + random.Next(0, maxValue: Math.Min(6, val2: t + 2));
                var cacheHit = t == 0 ? 0m : 40m + random.Next(0, 400) / 10m; // 40.0 - 80.0 %
                var spike = forceSpike || random.Next(0, 100) < 5;
                var ttft = spike ? 1800 + random.Next(0, 3200) : 150 + random.Next(0, 450);
                // Grows per turn; heavier sessions breach the 90% safety line, capped below the edge.
                var contextPct = Math.Min(97m, val2: 12m + t * contextGrowth);

                points.Add(new MetricTurnPoint(
                    TimestampUtc: timestamp,
                    SessionId: sessionId,
                    Model: model,
                    BaselineCostUsd: baselineCost,
                    TotalCost: cost,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    ToolExecutionSteps: toolSteps,
                    CacheHitRate: cacheHit,
                    TimeToFirstTokenMs: ttft,
                    ContextBufferPercent: contextPct,
                    BaselineModel: "demo-baseline",
                    IsExploratory: isFallback));
            }
        }

        return points;
    }
}