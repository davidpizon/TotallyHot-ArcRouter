namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>One page of persisted turn metadata, and the first page's retained-history aggregate.</summary>
/// <param name="TranscriptCaptureEnabled">False when capture is off and the page was not queried.</param>
/// <param name="Turns">Newest first.</param>
/// <param name="NextBeforeTranscriptId">The cursor for the next older page, or null when this page is the end.</param>
/// <param name="Aggregate">Present on the first page only.</param>
public sealed record SessionTurnDetailsResult(
    bool TranscriptCaptureEnabled,
    IReadOnlyList<SessionTurnDetailDto> Turns,
    long? NextBeforeTranscriptId,
    SessionAggregateDto? Aggregate);

/// <summary>Metadata for one persisted turn. No conversation text.</summary>
/// <param name="TranscriptId">The transcript row.</param>
/// <param name="TurnNumber">The turn inside the session.</param>
/// <param name="CreatedAtUtc">When the row was written.</param>
/// <param name="RequestedModel">The model the client named.</param>
/// <param name="RoutedModel">The model that served.</param>
/// <param name="Dimension">The classification dimension, or null.</param>
/// <param name="Difficulty">The classification difficulty, or null.</param>
/// <param name="Language">The classification language, or null.</param>
/// <param name="IsUtility">Whether the turn was utility.</param>
/// <param name="Score">The stored score, or null.</param>
/// <param name="CostUsd">The stored cost, or null.</param>
/// <param name="IsExploratory">Whether routing explored.</param>
/// <param name="Propensity">The probability routing assigned.</param>
/// <param name="InputTokens">Prompt tokens, or null.</param>
/// <param name="OutputTokens">Completion tokens, or null.</param>
/// <param name="MemoryEntryId">The memory row, when this turn trained.</param>
/// <param name="DimBestModel">The dimension-best model, or null.</param>
/// <param name="ScorerVersion">The scorer version, or null.</param>
/// <param name="IsJudgeScored">Whether a judge grade was stored.</param>
/// <param name="PromptTextLength">The stored prompt length, or null.</param>
/// <param name="ResponseTextLength">The stored response length, or null.</param>
/// <param name="HasToolCalls">Whether the reply carried tool calls. Null means unknown.</param>
/// <param name="Comparison">The baseline comparison, or null.</param>
public sealed record SessionTurnDetailDto(
    long TranscriptId,
    int TurnNumber,
    DateTimeOffset CreatedAtUtc,
    string RequestedModel,
    string RoutedModel,
    string? Dimension,
    string? Difficulty,
    string? Language,
    bool IsUtility,
    double? Score,
    decimal? CostUsd,
    bool IsExploratory,
    double Propensity,
    int? InputTokens,
    int? OutputTokens,
    long? MemoryEntryId,
    string? DimBestModel,
    string? ScorerVersion,
    bool IsJudgeScored,
    int? PromptTextLength,
    int? ResponseTextLength,
    bool? HasToolCalls,
    BaselineComparisonDto? Comparison);

/// <summary>The baseline comparison shown on the Routing tab.</summary>
/// <param name="BaselineModel">The baseline model, or null.</param>
/// <param name="ActualCostUsd">The turn's cost, or null.</param>
/// <param name="BaselineEstimatedCostUsd">The baseline cost, or null.</param>
/// <param name="EstimatedNetSavingsUsd">The estimated savings, or null.</param>
/// <param name="BaselinePredictedScore">The baseline's predicted score, or null.</param>
/// <param name="EstimatedRegret">The estimated regret, or null.</param>
/// <param name="BaselineTokenizerRatio">The tokenizer ratio, or null.</param>
/// <param name="BaselineInputPricePerMillion">The frozen input price, or null.</param>
/// <param name="BaselineOutputPricePerMillion">The frozen output price, or null.</param>
public sealed record BaselineComparisonDto(
    string? BaselineModel,
    decimal? ActualCostUsd,
    decimal? BaselineEstimatedCostUsd,
    decimal? EstimatedNetSavingsUsd,
    double? BaselinePredictedScore,
    double? EstimatedRegret,
    double? BaselineTokenizerRatio,
    decimal? BaselineInputPricePerMillion,
    decimal? BaselineOutputPricePerMillion);

/// <summary>Retained-history figures for the Session tab.</summary>
/// <param name="PersistedTurns">How many rows the session still has.</param>
/// <param name="TrainedTurns">Rows with a memory entry.</param>
/// <param name="ComparedTurns">Rows that have both savings and a positive baseline cost.</param>
/// <param name="TotalEstimatedNetSavingsUsd">Sum of savings over compared turns, or null.</param>
/// <param name="TotalBaselineEstimatedCostUsd">Sum of baseline cost over those turns, or null.</param>
/// <param name="TotalCostUsd">Sum of known costs, or null.</param>
/// <param name="UnpricedTurns">Rows with no cost.</param>
/// <param name="TotalInputTokens">Sum of reported input tokens.</param>
/// <param name="TotalOutputTokens">Sum of reported output tokens.</param>
/// <param name="TokenUnknownTurns">Rows missing either token count.</param>
/// <param name="Models">The most recently used routed models, at most 20.</param>
/// <param name="OtherModels">Distinct models beyond those 20.</param>
/// <param name="OtherModelTurns">Turns those omitted models served.</param>
/// <param name="PersistedTurnNumbersComplete">False when the floor list was cut at 2,000.</param>
public sealed record SessionAggregateDto(
    int PersistedTurns,
    int TrainedTurns,
    int ComparedTurns,
    decimal? TotalEstimatedNetSavingsUsd,
    decimal? TotalBaselineEstimatedCostUsd,
    decimal? TotalCostUsd,
    int UnpricedTurns,
    long TotalInputTokens,
    long TotalOutputTokens,
    int TokenUnknownTurns,
    IReadOnlyList<SessionModelUsageDto> Models,
    int OtherModels,
    int OtherModelTurns,
    bool PersistedTurnNumbersComplete);

/// <summary>One model's share of a session's retained turns.</summary>
/// <param name="RoutedModel">The model name.</param>
/// <param name="Turns">How many retained turns it served.</param>
/// <param name="CostUsd">Sum of its priced turns, or null.</param>
/// <param name="UnpricedTurns">Its turns with no cost.</param>
public sealed record SessionModelUsageDto(string RoutedModel, int Turns, decimal? CostUsd, int UnpricedTurns);
