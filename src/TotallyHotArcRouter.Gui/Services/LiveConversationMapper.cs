using TotallyHot.ArcRouter.Gui.Charts;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Maps the live-telemetry aggregation output (<see cref="LiveConversation"/> /
/// <see cref="LiveConversationTurn"/>, produced by <c>TotallyHot.ArcRouter.Gui.Telemetry</c>) onto the
/// dashboard's existing view-model shape (<see cref="Conversation"/> / <see cref="ConversationTurn"/>)
/// so <c>LiveStream</c>, <c>ConversationCard</c>, <c>ConversationSummary</c>, and <c>TurnCard</c> can
/// render live data without knowing it isn't <see cref="MockData"/>.
/// </summary>
/// <remarks>
/// Several <see cref="ConversationTurn"/> fields have no live-data source given the proxy's current
/// telemetry scope and are set to explicit, honest defaults here rather than fabricated:
/// <list type="bullet">
/// <item><see cref="ConversationTurn.RoutingRoi"/> - no "worst case" baseline cost is computed for live
/// requests (defaults to 0).</item>
/// <item><see cref="ConversationTurn.ToolExecutionSteps"/> - the proxy does not introspect tool calls
/// within a turn (defaults to 0).</item>
/// <item><see cref="ConversationTurn.ContextBufferPercent"/> - no per-model context-window-size
/// configuration exists (defaults to 0).</item>
/// </list>
/// <see cref="ConversationTurn.RequestSummary"/> / <see cref="ConversationTurn.ResponseSummary"/> ARE
/// real: they pass straight through from <see cref="LiveConversationTurn"/> (the proxy's newest-user-message
/// / assistant-reply-text extraction, truncated server-side - see docs/router/telemetry.md). Still
/// null when the provider is unsupported or no text was extractable (e.g. a tool-only turn).
/// <see cref="ConversationTurn.TimeToFirstTokenMs"/> IS real: it is
/// <see cref="LiveConversationTurn.LatencyToHeadersMs"/>, the time from sending the upstream request to
/// receiving response headers. <see cref="ConversationTurn.TimestampUtc"/> IS real too: it is
/// <see cref="LiveConversationTurn.TimestampUtc"/> passed straight through (the display-only
/// <see cref="ConversationTurn.Timestamp"/> string is derived from it), and is what the Cost Analytics
/// tab buckets turns by on its time axis. <see cref="ConversationTurn.CacheHitRate"/> IS real too: it is
/// derived from <see cref="LiveConversationTurn.PromptTokens"/>,
/// <see cref="LiveConversationTurn.CacheCreationTokens"/>, and
/// <see cref="LiveConversationTurn.CacheReadTokens"/> via <see cref="CostChartBuilder.CacheHitRate"/>.
/// See docs/gui/dashboard.md for the full real-vs-defaulted breakdown.
/// </remarks>
public static class LiveConversationMapper
{
    /// <summary>Maps one <see cref="LiveConversation"/> (and its turns) onto the dashboard's <see cref="Conversation"/> view model.</summary>
    public static Conversation ToModel(LiveConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return new Conversation(
            Id: conversation.SessionId,
            Title: BuildTitle(conversation),
            FirstTimestamp: FormatTimestamp(conversation.FirstTimestampUtc),
            LastTimestamp: FormatTimestamp(conversation.LastTimestampUtc),
            TotalCost: conversation.TotalCost,
            TotalPromptTokens: conversation.TotalPromptTokens,
            TotalCompletionTokens: conversation.TotalCompletionTokens,
            HasFallbackTurns: conversation.HasFallbackTurns,
            Turns: conversation.Turns.Select(ToModel).ToList());
    }

    /// <summary>Maps one <see cref="LiveConversationTurn"/> onto the dashboard's <see cref="ConversationTurn"/> view model.</summary>
    private static ConversationTurn ToModel(LiveConversationTurn turn)
    {
        return new ConversationTurn(
            Id: $"{turn.SessionId}-t{turn.TurnNumber}",
            Agent: turn.Agent,
            Model: turn.Model,
            TurnNumber: turn.TurnNumber,
            PromptTokens: turn.PromptTokens,
            CompletionTokens: turn.CompletionTokens,
            RoutingRoi: 0m,
            TotalCost: turn.EstimatedCostUsd,
            ToolExecutionSteps: 0,
            CacheHitRate: CostChartBuilder.CacheHitRate(turn.PromptTokens, turn.CacheCreationTokens, turn.CacheReadTokens),
            TimeToFirstTokenMs: (int)Math.Clamp(turn.LatencyToHeadersMs, 0, int.MaxValue),
            ContextBufferPercent: 0m,
            Timestamp: FormatTimestamp(turn.TimestampUtc),
            RoutingSteps: BuildRoutingSteps(turn),
            RequestSummary: turn.RequestSummary,
            ResponseSummary: turn.ResponseSummary,
            IsFallback: turn.IsFallback,
            TimestampUtc: turn.TimestampUtc);
    }

    /// <summary>Builds the display routing steps for a turn, flagging fallback routing and naming the confirmed model.</summary>
    private static IReadOnlyList<RoutingStep> BuildRoutingSteps(LiveConversationTurn turn)
    {
        List<RoutingStep> steps = [];
        if (turn.IsFallback)
        {
            steps.Add(new RoutingStep(StepStatus.Warn, "Fallback routing was used for this turn."));
        }

        steps.Add(new RoutingStep(StepStatus.Info, $"Route Confirmed: {turn.Model}"));
        return steps;
    }

    /// <summary>Builds the conversation card's display title, marking untracked (synthesized) sessions distinctly.</summary>
    private static string BuildTitle(LiveConversation conversation) =>
        conversation.IsSessionSynthesized
            ? $"Untracked session ({ShortId(conversation.SessionId)})"
            : $"Session {ShortId(conversation.SessionId)}";

    /// <summary>Truncates a session id to its first 8 characters for compact display, leaving shorter ids untouched.</summary>
    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    /// <summary>Formats a UTC timestamp as a local-time "HH:mm:ss" string for display.</summary>
    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

