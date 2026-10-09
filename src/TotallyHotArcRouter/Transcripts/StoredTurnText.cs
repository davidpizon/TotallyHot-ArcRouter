namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// One turn's conversation text as read for the Sessions tab's on-open load (#165 phase 2). The list
/// RPC does not carry this text.
/// </summary>
/// <param name="TranscriptId">The <c>request_transcripts.id</c> the caller asked for.</param>
/// <param name="Found">Whether a row with that id exists.</param>
/// <param name="PromptText">The newest user message from the Extracts frame, or <see langword="null"/>.</param>
/// <param name="ResponseText">The assistant reply from the Extracts frame, or <see langword="null"/>.</param>
/// <param name="PromptTextLength">Character length stored at insert, or <see langword="null"/> when there was no prompt.</param>
/// <param name="ResponseTextLength">Character length stored at insert, or <see langword="null"/> when there was no reply.</param>
public sealed record StoredTurnText(
    long TranscriptId,
    bool Found,
    string? PromptText,
    string? ResponseText,
    int? PromptTextLength,
    int? ResponseTextLength);
