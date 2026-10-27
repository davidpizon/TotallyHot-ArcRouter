namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// One <c>request_transcripts</c> row's time, tokens, and cost for the token-efficiency line
/// (issue #304 phase 9). A null token count and a null cost stay null. This row has no question text
/// and no response text.
/// </summary>
/// <param name="Id">The transcript row id.</param>
/// <param name="CreatedAtUtc">When the request was recorded, in UTC.</param>
/// <param name="InputTokens">Prompt tokens, or null when the row did not record them.</param>
/// <param name="OutputTokens">Completion tokens, or null when the row did not record them.</param>
/// <param name="Cost">The request's own cost, or null when it was not known. A stored 0 stays 0.</param>
public sealed record TranscriptTokenCostPoint(
    long Id,
    DateTimeOffset CreatedAtUtc,
    int? InputTokens,
    int? OutputTokens,
    decimal? Cost);
