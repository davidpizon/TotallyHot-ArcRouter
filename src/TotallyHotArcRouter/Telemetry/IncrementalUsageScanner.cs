namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fallback usage recovery for a streamed response too large to fit <c>ProxyMiddleware</c>'s
/// head-capped capture buffer (<c>MaxCapturedResponseBytes</c>). A streaming provider's usage block
/// arrives in the <b>final</b> SSE event (OpenAI's trailing <c>usage</c> chunk; Anthropic's last
/// <c>message_delta</c>) - for a response whose total size exceeds the cap, that final event never
/// makes it into the head-capped capture, so <see cref="IUsageExtractor.TryExtractUsage"/> against it
/// fails outright even though the upstream response carried perfectly good usage data. This class is
/// fed every raw chunk as it streams through <c>ProxyMiddleware</c>'s copy loop and retains only a
/// bounded <em>tail</em> window (independent of the head cap), so the trailing usage event
/// survives regardless of how large the response grew before it. <c>UsageExtractor</c> (the
/// single-shot parser over the head-capped buffer) remains the primary path; this is consulted only
/// when that primary parse fails (<c>docs/router/token-tracking-improvements.md</c> §5.11).
/// </summary>
public sealed class IncrementalUsageScanner
{
    /// <summary>Default tail window size: generous relative to a single trailing SSE usage event.</summary>
    public const int DefaultMaxTailBytes = 65_536;

    private readonly int _maxTailBytes;
    private byte[] _tail = [];

    /// <summary>Initializes a new instance of the <see cref="IncrementalUsageScanner"/> class.</summary>
    /// <param name="maxTailBytes">The maximum number of trailing bytes retained. Defaults to <see cref="DefaultMaxTailBytes"/>.</param>
    public IncrementalUsageScanner(int maxTailBytes = DefaultMaxTailBytes)
    {
        if (maxTailBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTailBytes), maxTailBytes, "Must be positive.");
        }

        _maxTailBytes = maxTailBytes;
    }

    /// <summary>
    /// Appends a newly-arrived chunk of the raw response stream, discarding whatever earlier bytes no
    /// longer fit within the tail window. Cheap relative to the cap: at most one array allocation of
    /// size at most <see cref="DefaultMaxTailBytes"/> per call.
    /// </summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        if (chunk.Length >= _maxTailBytes)
        {
            _tail = chunk[^_maxTailBytes..].ToArray();
            return;
        }

        var combinedLength = Math.Min(_tail.Length + chunk.Length, _maxTailBytes);
        var keepFromExisting = combinedLength - chunk.Length;
        var combined = new byte[combinedLength];
        _tail.AsSpan(_tail.Length - keepFromExisting).CopyTo(combined);
        chunk.CopyTo(combined.AsSpan(keepFromExisting));
        _tail = combined;
    }

    /// <summary>
    /// Attempts to extract usage from the retained tail window via <paramref name="extractor"/>, using
    /// the same provider-specific parsers <c>UsageExtractor</c> uses for the primary (head-capped) path.
    /// </summary>
    /// <param name="provider">The provider key whose parser should read the tail bytes.</param>
    /// <param name="isStreaming">Whether the response was streamed.</param>
    /// <param name="extractor">The extractor to parse the tail bytes with.</param>
    /// <param name="usage">The extracted usage, when this method returns <see langword="true"/>.</param>
    public bool TryExtractUsage(string provider, bool isStreaming, IUsageExtractor extractor, out UsageInfo usage)
    {
        ArgumentNullException.ThrowIfNull(extractor);

        usage = default;
        return _tail.Length > 0 && extractor.TryExtractUsage(provider, isStreaming, _tail, out usage);
    }
}
