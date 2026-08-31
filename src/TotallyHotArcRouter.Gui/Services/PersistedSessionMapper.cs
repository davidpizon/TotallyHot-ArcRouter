using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Maps persisted-history aggregation output (<see cref="PersistedConversation"/> /
/// <see cref="PersistedConversationTurn"/>, produced by <c>TotallyHot.ArcRouter.Gui.Telemetry</c>) onto the
/// dashboard's existing view-model shape (<see cref="Conversation"/> / <see cref="ConversationTurn"/>) - the
/// persisted-history counterpart of <see cref="LiveConversationMapper"/>
/// (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
/// <remarks>
/// <c>request_transcripts</c> carries a narrower slice of a turn's data than the live telemetry stream
/// does - no ROI baseline, tool-execution-step count, cache-hit rate, time-to-first-token, context-buffer
/// percentage, fallback flag, or distinct "agent" name (only <see cref="PersistedConversationTurn.RoutedModel"/>
/// is captured). Every field with no persisted source is set to the same honest, explicit default
/// <see cref="LiveConversationMapper"/> uses for its own no-live-source fields, rather than a fabricated
/// value - see that class's remarks for the shared rationale.
/// </remarks>
public static class PersistedSessionMapper
{
    /// <summary>Maps one <see cref="PersistedConversation"/> (and its turns) onto the dashboard's <see cref="Conversation"/> view model.</summary>
    public static Conversation ToModel(PersistedConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return new Conversation(
            Id: conversation.SessionId,
            Title: BuildTitle(conversation),
            FirstTimestamp: FormatTimestamp(conversation.FirstTimestampUtc),
            LastTimestamp: FormatTimestamp(conversation.LastTimestampUtc),
            TotalCost: conversation.TotalCostUsd,
            TotalPromptTokens: conversation.TotalInputTokens,
            TotalCompletionTokens: conversation.TotalOutputTokens,
            HasFallbackTurns: false,
            Turns: conversation.Turns.Select(ToModel).ToList(),
            IsUsedForTraining: conversation.IsUsedForTraining);
    }

    /// <summary>Maps one <see cref="PersistedConversationTurn"/> onto the dashboard's <see cref="ConversationTurn"/> view model.</summary>
    private static ConversationTurn ToModel(PersistedConversationTurn turn)
    {
        return new ConversationTurn(
            Id: $"{turn.CorrelationId}",
            Agent: turn.RoutedModel,
            Model: turn.RoutedModel,
            TurnNumber: turn.TurnNumber,
            PromptTokens: turn.InputTokens ?? 0,
            CompletionTokens: turn.OutputTokens ?? 0,
            RoutingRoi: 0m,
            TotalCost: turn.CostUsd ?? 0m,
            ToolExecutionSteps: 0,
            CacheHitRate: 0m,
            TimeToFirstTokenMs: 0,
            ContextBufferPercent: 0m,
            Timestamp: FormatTimestamp(turn.TimestampUtc),
            RoutingSteps: [new RoutingStep(StepStatus.Info, $"Route Confirmed: {turn.RoutedModel}")],
            RequestSummary: turn.PromptText,
            ResponseSummary: turn.ResponseText,
            IsFallback: false,
            TimestampUtc: turn.TimestampUtc,
            RequestedModel: turn.RequestedModel,
            RoutedModel: turn.RoutedModel);
    }

    /// <summary>Builds the conversation card's display title for a persisted session.</summary>
    private static string BuildTitle(PersistedConversation conversation) =>
        $"Session {ShortId(conversation.SessionId)}";

    /// <summary>Truncates a session id to its first 8 characters for compact display, leaving shorter ids untouched.</summary>
    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    /// <summary>Formats a UTC timestamp as a local-time "HH:mm:ss" string for display.</summary>
    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
