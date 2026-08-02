namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// What one request's response should be scanned for, decided before the request is forwarded
/// (<c>docs/router/tool-call-normalization.md</c> §3.4). Built by <see cref="ToolCallNormalizerFactory"/>
/// and carried by the per-request translator; immutable for the life of the response.
/// </summary>
/// <param name="ProviderKey">The provider this model is served by - half the capability-store key.</param>
/// <param name="ModelName">The client-facing model name - the other half.</param>
/// <param name="Candidates">
/// The dialects to scan for, in tie-break order (see <c>DialectMatcher.MatchAny</c>). Exactly one entry
/// when the model's dialect is settled; every scannable dialect when it is not, which is the union scan
/// that makes the first live request its own probe.
/// </param>
/// <param name="IsObserving">
/// Whether what this response reveals should be written back to the capability store. False once a model
/// has been classified at <see cref="DetectionConfidence.Observed"/> or pinned by an operator - at which
/// point re-recording the same answer on every request would be pure database traffic.
/// </param>
/// <param name="IsEmulating">
/// Whether the request was rewritten to *teach* this dialect (Phase 5) rather than merely to recognize it.
/// When true, a matched region is TotallyHot.ArcRouter's own instructions coming back and must not be recorded as
/// a dialect observation - see <see cref="ToolCallObservationRecorder"/>. A native <c>tool_calls</c>
/// response is still recorded, because that is the one thing an emulated request cannot have taught.
/// </param>
internal sealed record ToolCallNormalizationPlan(
    string ProviderKey,
    string ModelName,
    IReadOnlyList<ToolCallDialect> Candidates,
    bool IsObserving,
    bool IsEmulating = false)
{
    /// <summary>
    /// The distinct first characters of every armed opening delimiter. The streaming scanner tests
    /// incoming text against this with a single <see cref="string.IndexOfAny(char[])"/> before attempting
    /// any delimiter match, so ordinary prose - which is nearly all output - never pays for full matching
    /// (§3.4 performance rule 3). Computed once per request rather than per chunk.
    /// </summary>
    public char[] OpenerFirstChars { get; } =
        Candidates
            .SelectMany(dialect => dialect.Delimiters)
            .Select(delimiter => delimiter.Open[0])
            .Distinct()
            .ToArray();
}

