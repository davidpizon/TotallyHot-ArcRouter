using System.Globalization;

namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>
/// One of the seven Cost Analytics metrics, ordered by TotallyHot.ArcRouter's business priority (1 = most
/// important). The order here is the order the tab's metric selector presents them in.
/// </summary>
public enum CostMetric
{
    /// <summary>1. Routing ROI - dollars saved by the routing decision versus a worst-case baseline.</summary>
    RoutingRoi,

    /// <summary>2. Total turn cost ($).</summary>
    TotalTurnCost,

    /// <summary>3. Prompt + completion tokens (the canonical "hockey stick").</summary>
    Tokens,

    /// <summary>4. Tool execution loop count (steps per turn).</summary>
    ToolExecutionSteps,

    /// <summary>5. Prompt-cache hit rate (%).</summary>
    CacheHitRate,

    /// <summary>6. Time-to-First-Token / routing latency (ms).</summary>
    TimeToFirstToken,

    /// <summary>7. Context buffer margin - % of the context window used.</summary>
    ContextBufferMargin
}

/// <summary>The time window a metric series is scoped to.</summary>
public enum MetricRange
{
    /// <summary>Past hour.</summary>
    Hour,

    /// <summary>Past 24 hours.</summary>
    Day,

    /// <summary>Past 7 days.</summary>
    Week,

    /// <summary>Past 30 days.</summary>
    Month,

    /// <summary>All history.</summary>
    AllTime
}

/// <summary>
/// One turn's raw metrics, the input to <see cref="CostChartBuilder"/>. Mirrors the fields the Gui's
/// <c>ConversationTurn</c> carries (see <c>Models/DashboardData.cs</c>) plus a real
/// <see cref="TimestampUtc"/> for time placement.
/// </summary>
/// <param name="TimestampUtc">When the turn was routed.</param>
/// <param name="SessionId">The session (conversation) the turn belongs to.</param>
/// <param name="Model">The model the router selected for the turn.</param>
/// <param name="BaselineCostUsd">
/// The estimated cost of the model the frozen nine-dimension baseline would have chosen, or
/// <see langword="null"/> when no estimate exists (the baseline abstained, the model was never priced, or
/// the comparison job has not reached this turn yet). Never defaulted to zero: a turn with no
/// counterfactual is excluded from the ROI chart rather than drawn as a break-even bar.
/// </param>
/// <param name="TotalCost">The turn's estimated cost in USD.</param>
/// <param name="PromptTokens">Prompt tokens sent for the turn.</param>
/// <param name="CompletionTokens">Completion tokens generated for the turn.</param>
/// <param name="ToolExecutionSteps">Number of tool-execution steps in the turn.</param>
/// <param name="CacheHitRate">Prompt-cache hit rate for the turn (0-100).</param>
/// <param name="TimeToFirstTokenMs">Latency to first token / response headers, in milliseconds.</param>
/// <param name="ContextBufferPercent">Percentage of the model's context window used (0-100).</param>
/// <param name="BaselineModel">
/// The model the frozen baseline would have chosen, named in the ROI tooltip.
/// <see langword="null"/> when it abstained.
/// </param>
/// <param name="IsExploratory">
/// Whether this turn was an epsilon-greedy probe rather than the ensemble's own pick. The ROI chart
/// renders these in a muted tone so a deliberate probe is not misread as a routing miss, while still
/// counting toward the all-in net figure.
/// </param>
public sealed record MetricTurnPoint(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string Model,
    decimal? BaselineCostUsd,
    decimal TotalCost,
    int PromptTokens,
    int CompletionTokens,
    int ToolExecutionSteps,
    decimal CacheHitRate,
    int TimeToFirstTokenMs,
    decimal ContextBufferPercent,
    string? BaselineModel = null,
    bool IsExploratory = false);

/// <summary>A model and its deterministic display color (the chart legend / bar color).</summary>
public sealed record CostModelColor(string Model, string Color);

/// <summary>One colored slice of a segmented tool-step bar: steps handled by a single model.</summary>
public sealed record CostChartSegment(string Model, string Color, int Steps);

/// <summary>
/// One turn's point on a Cost Analytics chart. Carries everything the ECharts renderer
/// (<c>wwwroot/js/echarts-interop.js</c>) needs for any of the bespoke per-metric formats, so the JS
/// side only picks fields by chart kind and never computes metric values itself.
/// </summary>
/// <param name="T">The turn's instant as Unix milliseconds (the x-axis position).</param>
/// <param name="Label">Tooltip header, e.g. <c>Turn #1042 (14:02:11)</c>.</param>
/// <param name="Model">The model that handled the turn.</param>
/// <param name="Color">The model's display color.</param>
/// <param name="Value">The primary y value in the metric's natural unit (signed for ROI bars).</param>
/// <param name="Secondary">A secondary value some kinds need (e.g. the cumulative running total).</param>
/// <param name="Flag">Special-state marker: runaway (tokens), spike (TTFT), breach (context), failure (ROI).</param>
/// <param name="Tip">Pre-formatted tooltip body lines.</param>
/// <param name="Segments">Per-model step segments, for the segmented tool-step bars only.</param>
public sealed record CostChartPoint(
    long T,
    string Label,
    string Model,
    string Color,
    decimal Value,
    decimal Secondary,
    bool Flag,
    IReadOnlyList<string> Tip,
    IReadOnlyList<CostChartSegment>? Segments = null);

/// <summary>
/// A fully-computed Cost Analytics chart, serialized to JSON and handed to the ECharts renderer. The
/// <see cref="Kind"/> string selects the bespoke chart format on the JS side.
/// </summary>
/// <param name="Kind">Renderer chart-kind discriminator (see <see cref="CostChartKind"/>).</param>
/// <param name="Title">Chart title / y-axis meaning.</param>
/// <param name="Unit">Value unit: <c>$</c>, <c>%</c>, <c>ms</c>, <c>tok</c>, or <c>steps</c>.</param>
/// <param name="Headline">The pre-formatted headline figure shown beside the title.</param>
/// <param name="Threshold">A horizontal threshold line (context margin's 90%), or null.</param>
/// <param name="Models">Distinct models present, with colors (drives the legend and bar colors).</param>
/// <param name="Points">The per-turn points, chronologically ordered.</param>
public sealed record CostChartModel(
    string Kind,
    string Title,
    string Unit,
    string Headline,
    decimal? Threshold,
    IReadOnlyList<CostModelColor> Models,
    IReadOnlyList<CostChartPoint> Points);

/// <summary>The bespoke chart format the ECharts renderer draws for a metric. Serialized as its name.</summary>
public static class CostChartKind
{
    /// <summary>Routing ROI: dual-directional bars (savings above 0, remediation below).</summary>
    public const string DualDirectionalBars = nameof(DualDirectionalBars);

    /// <summary>Turn cost: stepped cumulative area recolored per active model.</summary>
    public const string SteppedCumulativeArea = nameof(SteppedCumulativeArea);

    /// <summary>Tokens: cumulative stepped area with exponential-runaway highlighting.</summary>
    public const string RunawayArea = nameof(RunawayArea);

    /// <summary>Tool steps: per-turn bar split into per-model step segments.</summary>
    public const string SegmentedStepBars = nameof(SegmentedStepBars);

    /// <summary>Cache hit rate: stepped percentage line with a gradient track.</summary>
    public const string CacheGradientLine = nameof(CacheGradientLine);

    /// <summary>TTFT: stepped latency line over per-model background zones.</summary>
    public const string ZonedLatencyLine = nameof(ZonedLatencyLine);

    /// <summary>Context margin: stepped percentage line with a fixed threshold.</summary>
    public const string ThresholdLine = nameof(ThresholdLine);
}

/// <summary>
/// Builds the Cost Analytics tab's per-metric chart models from a corpus of turn points, following the
/// spec in <c>docs/gui/cost-analytics-visualization-spec.md</c>. Pure and stateless - no dependency on
/// the Gui project's Blazor/MAUI types - so it's unit-testable on any platform. Every rich tooltip
/// figure the spec calls for (worst-case baseline, cached/uncached token split, context token counts,
/// TTFT cold-start) is <b>derived</b> here from the turn's existing fields, so nothing new has to flow
/// through the telemetry pipeline for the charts to render fully.
/// </summary>
public static class CostChartBuilder
{
    // Growth factor over the previous turn that marks a token "runaway" (spec: Δtokens/Δturn > 2.5x).
    private const decimal RunawayFactor = 2.5m;

    // TTFT above this (ms) is flagged as a latency spike anomaly.
    private const int LatencySpikeMs = 1500;

    // The context-window fill percentage at which the safety threshold line sits.
    private const decimal ContextThresholdPercent = 90m;

    // ---- shared helpers ----------------------------------------------------------

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Builds the chart model for <paramref name="metric"/> over the <paramref name="range"/> window
    /// measured back from <paramref name="now"/>, optionally scoped to a single
    /// <paramref name="sessionId"/> (null/empty = all sessions). Points are chronological.
    /// </summary>
    public static CostChartModel Build(
        IReadOnlyList<MetricTurnPoint> points,
        CostMetric metric,
        MetricRange range,
        string? sessionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(points);

        var windowStart = WindowStart(range: range, now: now);
        var turns = points
            .Where(p => p.TimestampUtc >= windowStart && p.TimestampUtc <= now)
            .Where(p => string.IsNullOrEmpty(sessionId) ||
                        string.Equals(a: p.SessionId, b: sessionId, comparisonType: StringComparison.Ordinal))
            .OrderBy(p => p.TimestampUtc)
            .ToList();

        return metric switch
        {
            CostMetric.RoutingRoi => BuildRoi(turns),
            CostMetric.TotalTurnCost => BuildCost(turns),
            CostMetric.Tokens => BuildTokens(turns),
            CostMetric.ToolExecutionSteps => BuildSteps(turns),
            CostMetric.CacheHitRate => BuildCache(turns),
            CostMetric.TimeToFirstToken => BuildLatency(turns),
            CostMetric.ContextBufferMargin => BuildContext(turns),
            _ => Empty(kind: CostChartKind.SteppedCumulativeArea, title: "Cost", unit: "$")
        };
    }

    // 1. Routing ROI - dual-directional bars: savings above 0, losses below.
    /// <summary>
    /// Builds the Routing ROI chart: dual-directional bars of estimated net savings against what the
    /// frozen nine-dimension baseline would have chosen
    /// (docs/router/self-organizing-classification-plan.md Phase T4), with losses below zero and
    /// exploratory probes muted.
    /// </summary>
    /// <param name="turns">The turns in range, chronologically ordered.</param>
    /// <returns>The chart model, empty when no turn in range has a counterfactual to compare against.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every figure here is an estimate, and the tooltip says so.</b> The baseline model was never asked
    /// to serve these requests, so its cost is priced from its own observed-average token counts rather
    /// than observed directly.
    /// </para>
    /// <para>
    /// A turn whose baseline cost is unknown is <b>skipped</b>, not drawn at zero. Previous revisions of
    /// this chart reconstructed a baseline from a cost-reduction percentage and invented a per-token
    /// remediation rate for turns that had none; both produced confident-looking bars from numbers nothing
    /// measured, and both are gone now that a real counterfactual exists.
    /// </para>
    /// </remarks>
    private static CostChartModel BuildRoi(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);
        var net = 0m;

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            if (t.BaselineCostUsd is not { } baseline) continue;

            var savings = baseline - t.TotalCost;
            net += savings;

            var baselineName = string.IsNullOrEmpty(t.BaselineModel) ? "baseline" : t.BaselineModel;
            IReadOnlyList<string> tip =
            [
                $"Model: {t.Model}",
                $"Actual Turn Cost: {Money(t.TotalCost)}",
                $"Baseline ({baselineName}): {Money(baseline)} — estimate",
                savings >= 0m
                    ? $"Net saving: +{Money(savings)} — estimate"
                    : $"Net loss: -{Money(-savings)} — estimate",
                t.IsExploratory ? "Exploratory probe (deliberate)" : "Ensemble pick"
            ];

            // Exploratory turns keep their bar - they are part of the all-in net position - but render
            // muted, so a probe that deliberately cost more is visually distinct from a routing miss.
            var color = t.IsExploratory ? Mute(Color(t.Model)) : Color(t.Model);
            pts.Add(new CostChartPoint(T: Ms(t), Label: Label(index: i, t: t), Model: t.Model, Color: color,
                Value: Round(value: savings, 4), 0m, Flag: savings < 0m, Tip: tip));
        }

        return pts.Count == 0
            ? Empty(kind: CostChartKind.DualDirectionalBars, title: "Routing ROI (net savings)", unit: "$")
            : new CostChartModel(
                Kind: CostChartKind.DualDirectionalBars,
                Title: "Routing ROI (net savings, estimated)",
                Unit: "$",
                Headline: Money(net),
                null,
                Models: Models(turns),
                Points: pts);
    }

    // 2. Total Turn Cost - stepped cumulative running total, area recolored per active model.
    /// <summary>Builds the Total Turn Cost chart: a stepped cumulative running total, recolored per active model.</summary>
    private static CostChartModel BuildCost(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);
        var cumulative = 0m;

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            cumulative += t.TotalCost;
            IReadOnlyList<string> tip =
            [
                $"Model: {t.Model}",
                $"Incremental Cost: +{Money(t.TotalCost)}",
                $"Cumulative Total: {Money(cumulative)}"
            ];
            pts.Add(new CostChartPoint(T: Ms(t), Label: Label(index: i, t: t), Model: t.Model, Color: Color(t.Model),
                Value: Round(value: t.TotalCost, 6), Secondary: Round(value: cumulative, 6), false, Tip: tip));
        }

        return new CostChartModel(Kind: CostChartKind.SteppedCumulativeArea, Title: "Cumulative Turn Cost", Unit: "$",
            Headline: Money(cumulative), null, Models: Models(turns), Points: pts);
    }

    // 3. Prompt + Completion Tokens - cumulative stepped area with exponential-runaway detection.
    /// <summary>
    /// Builds the Tokens chart: cumulative prompt + completion tokens with exponential-runaway detection flagged per
    /// turn.
    /// </summary>
    private static CostChartModel BuildTokens(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);
        long cumulative = 0;
        var previousAdded = 0;

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var added = t.PromptTokens + t.CompletionTokens;
            cumulative += added;
            var runaway = i > 0 && previousAdded > 0 && added > previousAdded * RunawayFactor;

            IReadOnlyList<string> tip = runaway
                ?
                [
                    $"Tokens Added: +{Int(added)} [RUNAWAY]",
                    $"Input: {Int(t.PromptTokens)} · Output: {Int(t.CompletionTokens)}",
                    $"Cumulative: {Int(cumulative)}"
                ]
                :
                [
                    $"Tokens Added: +{Int(added)} (In: {Int(t.PromptTokens)}, Out: {Int(t.CompletionTokens)})",
                    $"Cumulative: {Int(cumulative)}"
                ];

            var label = runaway ? $"{Label(index: i, t: t)} [RUNAWAY ALERT]" : Label(index: i, t: t);
            pts.Add(new CostChartPoint(T: Ms(t), Label: label, Model: t.Model, Color: Color(t.Model), Value: cumulative,
                Secondary: added, Flag: runaway, Tip: tip));
            previousAdded = added;
        }

        return new CostChartModel(Kind: CostChartKind.RunawayArea, Title: "Cumulative Tokens", Unit: "tok",
            Headline: Tokens(cumulative), null, Models: Models(turns), Points: pts);
    }

    // 4. Tool Execution Loop Count - a bar per turn split into per-model step segments. A turn that
    //    hands off from the previous turn's model is shown as two colored segments (planning vs. repair).
    /// <summary>
    /// Builds the Tool Execution Steps chart: a bar per turn split into per-model step segments, splitting the bar
    /// when a turn hands off to a different model.
    /// </summary>
    private static CostChartModel BuildSteps(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);
        var segmentModels = new HashSet<string>(StringComparer.Ordinal);
        var lastTotal = 0;

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var total = Math.Max(0, val2: t.ToolExecutionSteps);
            lastTotal = total;
            var previousModel = i > 0 ? turns[i - 1].Model : null;

            List<CostChartSegment> segments;
            if (total >= 2 && previousModel is not null && !string.Equals(a: previousModel, b: t.Model,
                    comparisonType: StringComparison.Ordinal))
            {
                var planning = total / 2;
                var repair = total - planning;
                segments =
                [
                    new CostChartSegment(Model: previousModel, Color: Color(previousModel), Steps: planning),
                    new CostChartSegment(Model: t.Model, Color: Color(t.Model), Steps: repair)
                ];
            }
            else
            {
                segments = [new CostChartSegment(Model: t.Model, Color: Color(t.Model), Steps: total)];
            }

            foreach (var s in segments) segmentModels.Add(s.Model);

            var tip = segments.Select(s => $"{s.Model}: {s.Steps} step{(s.Steps == 1 ? "" : "s")}").ToList();
            pts.Add(new CostChartPoint(T: Ms(t), Label: $"{Label(index: i, t: t)} - {total} steps", Model: t.Model,
                Color: Color(t.Model), Value: total, 0m, false, Tip: tip, Segments: segments));
        }

        // Legend must include every model that owns a segment, not just each turn's headline model.
        var models = segmentModels
            .OrderBy(keySelector: m => m, comparer: StringComparer.Ordinal)
            .Select(m => new CostModelColor(Model: m, Color: Color(m)))
            .ToList();

        return new CostChartModel(Kind: CostChartKind.SegmentedStepBars, Title: "Tool Execution Steps", Unit: "steps",
            Headline: lastTotal.ToString(Inv), null, Models: models, Points: pts);
    }

    // 5. Cache Hit Rate - stepped percentage line with a gradient track and per-model markers.
    /// <summary>
    /// Builds the Cache Hit Rate chart: a stepped percentage line with per-model markers, plus the average rate as
    /// its headline.
    /// </summary>
    private static CostChartModel BuildCache(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var cached = (long)Math.Round(t.PromptTokens * t.CacheHitRate / 100m);
            var uncached = t.PromptTokens - cached;
            IReadOnlyList<string> tip =
            [
                $"Cache Hit Rate: {t.CacheHitRate.ToString(format: "F1", provider: Inv)}%",
                $"Cached: {Int(cached)} tok",
                $"Uncached: {Int(uncached)} tok"
            ];
            pts.Add(new CostChartPoint(T: Ms(t), Label: Label(index: i, t: t), Model: t.Model, Color: Color(t.Model),
                Value: Round(value: t.CacheHitRate, 1), 0m, false, Tip: tip));
        }

        var avg = turns.Count == 0 ? 0m : turns.Average(t => t.CacheHitRate);
        return new CostChartModel(Kind: CostChartKind.CacheGradientLine, Title: "Cache Hit Rate", Unit: "%",
            Headline: $"{avg.ToString(format: "F0", provider: Inv)}%", null, Models: Models(turns), Points: pts);
    }

    // 6. TTFT / Routing Latency - stepped line over per-model background zones, spikes pinned.
    /// <summary>
    /// Builds the Time to First Token chart: a stepped latency line that pins and annotates spikes above
    /// <see cref="LatencySpikeMs"/>.
    /// </summary>
    private static CostChartModel BuildLatency(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var spike = t.TimeToFirstTokenMs >= LatencySpikeMs;
            IReadOnlyList<string> tip;
            if (spike)
            {
                var routerOverhead = (int)Math.Round(t.TimeToFirstTokenMs * 0.08);
                tip =
                [
                    $"Total Routing Latency: {Int(t.TimeToFirstTokenMs)} ms [SPIKE]",
                    $"Model: {t.Model}",
                    $"{routerOverhead} ms router + {t.TimeToFirstTokenMs - routerOverhead} ms upstream cold-start"
                ];
            }
            else
            {
                tip = [$"TTFT: {t.TimeToFirstTokenMs} ms", $"Model: {t.Model}"];
            }

            var label = spike ? $"{Label(index: i, t: t)} - SPIKE" : Label(index: i, t: t);
            pts.Add(new CostChartPoint(T: Ms(t), Label: label, Model: t.Model, Color: Color(t.Model),
                Value: t.TimeToFirstTokenMs, 0m, Flag: spike, Tip: tip));
        }

        var avg = turns.Count == 0 ? 0 : (int)Math.Round(turns.Average(t => t.TimeToFirstTokenMs));
        return new CostChartModel(Kind: CostChartKind.ZonedLatencyLine, Title: "Time to First Token", Unit: "ms",
            Headline: $"{avg} ms", null, Models: Models(turns), Points: pts);
    }

    // 7. Context Buffer Margin - stepped % line with a fixed 90% red threshold and pulsing breaches.
    /// <summary>
    /// Builds the Context Buffer Margin chart: a stepped percentage line with a fixed threshold breach flagged per
    /// turn.
    /// </summary>
    private static CostChartModel BuildContext(IReadOnlyList<MetricTurnPoint> turns)
    {
        var pts = new List<CostChartPoint>(turns.Count);

        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var breach = t.ContextBufferPercent >= ContextThresholdPercent;
            var window = ContextWindowTokens(t.Model);
            var used = (long)Math.Round(window * t.ContextBufferPercent / 100m);
            IReadOnlyList<string> tip = breach
                ?
                [
                    $"Context Used: {t.ContextBufferPercent.ToString(format: "F1", provider: Inv)}% [WARNING]",
                    $"{Int(used)} / {Int(window)} tokens",
                    "Automated context-pruning sweep queued"
                ]
                :
                [
                    $"Context Used: {t.ContextBufferPercent.ToString(format: "F1", provider: Inv)}%",
                    $"{Int(used)} / {Int(window)} tokens"
                ];
            pts.Add(new CostChartPoint(T: Ms(t), Label: Label(index: i, t: t), Model: t.Model, Color: Color(t.Model),
                Value: Round(value: t.ContextBufferPercent, 1), 0m, Flag: breach, Tip: tip));
        }

        var last = pts.Count > 0 ? pts[^1].Value : 0m;
        return new CostChartModel(Kind: CostChartKind.ThresholdLine, Title: "Context Buffer Margin", Unit: "%",
            Headline: $"{last.ToString(format: "F1", provider: Inv)}%", Threshold: ContextThresholdPercent,
            Models: Models(turns), Points: pts);
    }

    /// <summary>Builds a placeholder chart model with no data points, used when a metric has no matching turns.</summary>
    private static CostChartModel Empty(string kind, string title, string unit)
    {
        return new CostChartModel(Kind: kind, Title: title, Unit: unit, Headline: "—", null, Models: [], Points: []);
    }

    /// <summary>Builds the legend entries for every distinct model appearing in the given turns, sorted by name.</summary>
    private static IReadOnlyList<CostModelColor> Models(IReadOnlyList<MetricTurnPoint> turns)
    {
        return turns
            .Select(t => t.Model)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(keySelector: m => m, comparer: StringComparer.Ordinal)
            .Select(m => new CostModelColor(Model: m, Color: Color(m)))
            .ToList();
    }

    /// <summary>Formats a data point's display label from its 1-based turn number and timestamp.</summary>
    private static string Label(int index, MetricTurnPoint t)
    {
        return $"Turn #{index + 1} ({t.TimestampUtc.ToUniversalTime():HH:mm:ss})";
    }

    /// <summary>Converts a turn's timestamp to Unix milliseconds for the chart's time axis.</summary>
    private static long Ms(MetricTurnPoint t)
    {
        return t.TimestampUtc.ToUnixTimeMilliseconds();
    }

    /// <summary>Looks up the display color assigned to a model.</summary>
    private static string Color(string model)
    {
        return ChartPalette.ColorFor(model);
    }

    /// <summary>
    /// Desaturates a <c>#rrggbb</c> color toward mid-grey, keeping a model identifiable while marking its
    /// bar as an exploratory probe rather than an ensemble pick.
    /// </summary>
    /// <param name="hex">The model's palette color.</param>
    /// <returns>The muted color, or <paramref name="hex"/> unchanged when it is not a 6-digit hex triple.</returns>
    private static string Mute(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#'
                            || !int.TryParse(s: hex.AsSpan(1), style: NumberStyles.HexNumber, provider: Inv,
                                result: out var rgb))
            return hex;

        // Blend 55% toward mid-grey: enough separation to read at a glance without losing the model's hue.
        static int Blend(int channel)
        {
            return (int)(channel * 0.45 + 128 * 0.55);
        }

        var muted = (Blend((rgb >> 16) & 0xFF) << 16) | (Blend((rgb >> 8) & 0xFF) << 8) | Blend(rgb & 0xFF);
        return "#" + muted.ToString(format: "x6", provider: Inv);
    }

    /// <summary>Rounds a decimal value away from zero to the given number of digits.</summary>
    private static decimal Round(decimal value, int digits)
    {
        return Math.Round(d: value, decimals: digits, mode: MidpointRounding.AwayFromZero);
    }

    /// <summary>Formats a decimal value as a dollar-prefixed, two-decimal string.</summary>
    private static string Money(decimal value)
    {
        return "$" + value.ToString(format: "F2", provider: Inv);
    }

    /// <summary>Formats an integer value with thousands separators.</summary>
    private static string Int(long value)
    {
        return value.ToString(format: "N0", provider: Inv);
    }

    /// <summary>
    /// Computes a turn's prompt-cache hit rate (0-100): the percentage of its full input that was
    /// served from the prompt cache rather than processed from scratch. The denominator is the
    /// additive total (<paramref name="promptTokens"/> + <paramref name="cacheCreationTokens"/> +
    /// <paramref name="cacheReadTokens"/> - see <c>UsageInfo.TotalInputTokens</c>), not just
    /// <paramref name="promptTokens"/> alone: the provider's own <c>input_tokens</c> excludes cached
    /// tokens, so dividing by it can exceed 100%. Returns 0 for a turn with no input tokens at all.
    /// </summary>
    public static decimal CacheHitRate(int promptTokens, int cacheCreationTokens, int cacheReadTokens)
    {
        // Widened to long before summing: three int.MaxValue-range counts could overflow a 32-bit sum and
        // silently wrap into a wrong (possibly negative) denominator.
        var totalInputTokens = (long)promptTokens + cacheCreationTokens + cacheReadTokens;
        return totalInputTokens <= 0 ? 0m : Round(value: cacheReadTokens / (decimal)totalInputTokens * 100m, 1);
    }

    /// <summary>Formats a token count using K/M abbreviations for large values.</summary>
    private static string Tokens(long value)
    {
        return value >= 1_000_000
            ? (value / 1_000_000m).ToString(format: "F1", provider: Inv) + "M"
            : value >= 1000
                ? (value / 1000m).ToString(format: "F0", provider: Inv) + "K"
                : value.ToString(Inv);
    }

    /// <summary>Approximate context-window size (tokens) for a model, keyed off its name.</summary>
    private static int ContextWindowTokens(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("gemini")) return 1_000_000;
        if (m.Contains("sonnet") || m.Contains("claude")) return 200_000;
        if (m.Contains("gpt-4o") || m.Contains("4o")) return 128_000;
        if (m.Contains("local") || m.Contains("fallback")) return 32_000;
        return 128_000;
    }

    /// <summary>The earliest instant included in a range window measured back from <paramref name="now"/>.</summary>
    private static DateTimeOffset WindowStart(MetricRange range, DateTimeOffset now)
    {
        return range switch
        {
            MetricRange.Hour => now.AddHours(-1),
            MetricRange.Day => now.AddDays(-1),
            MetricRange.Week => now.AddDays(-7),
            MetricRange.Month => now.AddDays(-30),
            MetricRange.AllTime => DateTimeOffset.MinValue,
            _ => DateTimeOffset.MinValue
        };
    }
}