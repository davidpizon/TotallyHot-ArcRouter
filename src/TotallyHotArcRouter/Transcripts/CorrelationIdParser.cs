namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Parses the session id out of a transcript correlation id. Shared by every reader/writer of
/// <c>request_transcripts</c> that needs the session id rather than the per-request correlation id -
/// extracted from <see cref="TaxonomyComparisonService"/>'s original private method so
/// <see cref="SqliteTranscriptStore"/> can use the same parsing when it writes the <c>session_id</c>
/// column (docs/router/sessions-tab-training-data-plan.md Phase 1).
/// </summary>
public static class CorrelationIdParser
{
    /// <summary>
    /// Recovers the session id from <paramref name="correlationId"/>, which <c>ProxyMiddleware</c>
    /// composes as <c>"{sessionId}:{turnNumber}"</c>.
    /// </summary>
    /// <param name="correlationId">A transcript row's correlation id.</param>
    /// <returns>The session portion, or the whole id when it carries no turn suffix.</returns>
    public static string SessionIdOf(string correlationId)
    {
        var lastSeparator = correlationId.LastIndexOf(':');
        return lastSeparator > 0 ? correlationId[..lastSeparator] : correlationId;
    }
}