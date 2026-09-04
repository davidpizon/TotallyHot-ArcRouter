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

    private readonly byte[] _buffer;

    private readonly int _maxTailBytes;
    private int _count;
    private int _writePos;

    /// <summary>Initializes a new instance of the <see cref="IncrementalUsageScanner"/> class.</summary>
    /// <param name="maxTailBytes">
    /// The maximum number of trailing bytes retained. Defaults to <see cref="DefaultMaxTailBytes"/>
    /// .
    /// </param>
    public IncrementalUsageScanner(int maxTailBytes = DefaultMaxTailBytes)
    {
        if (maxTailBytes <= 0)
            throw new ArgumentOutOfRangeException(paramName: nameof(maxTailBytes), actualValue: maxTailBytes,
                message: "Must be positive.");

        _maxTailBytes = maxTailBytes;
        _buffer = new byte[maxTailBytes];
    }

    /// <summary>
    /// Appends a newly-arrived chunk of the raw response stream, discarding whatever earlier bytes no
    /// longer fit within the tail window. Writes straight into a fixed-size circular buffer allocated
    /// once in the constructor - unlike a naive "concat and re-slice" tail, this call never allocates,
    /// which matters since <c>ProxyMiddleware</c>'s copy loop invokes it once per streamed chunk and a
    /// large response can stream thousands of them.
    /// </summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        if (chunk.Length >= _maxTailBytes)
        {
            // Only the trailing maxTailBytes of this chunk can possibly matter; landing them at index 0
            // and resetting the write cursor there keeps the buffer's "oldest byte" invariant intact
            // without needing to touch bytes this chunk is about to overwrite anyway.
            chunk[^_maxTailBytes..].CopyTo(_buffer);
            _writePos = 0;
            _count = _maxTailBytes;
            return;
        }

        var firstLen = Math.Min(val1: chunk.Length, val2: _maxTailBytes - _writePos);
        chunk[..firstLen].CopyTo(_buffer.AsSpan(_writePos));
        var remaining = chunk.Length - firstLen;
        if (remaining > 0) chunk[firstLen..].CopyTo(_buffer.AsSpan(0, length: remaining));

        _writePos = (_writePos + chunk.Length) % _maxTailBytes;
        _count = Math.Min(val1: _count + chunk.Length, val2: _maxTailBytes);
    }

    /// <summary>
    /// Attempts to extract usage from the retained tail window via <paramref name="extractor"/>, using
    /// the same provider-specific parsers <c>UsageExtractor</c> uses for the primary (head-capped) path.
    /// This is the one point where the circular buffer's contents are copied into a contiguous
    /// <see cref="byte"/>[] - a single allocation made only when a caller actually needs to parse the
    /// tail, not on every <see cref="Append"/> call.
    /// </summary>
    /// <param name="provider">The provider key whose parser should read the tail bytes.</param>
    /// <param name="isStreaming">Whether the response was streamed.</param>
    /// <param name="extractor">The extractor to parse the tail bytes with.</param>
    /// <param name="usage">The extracted usage, when this method returns <see langword="true"/>.</param>
    public bool TryExtractUsage(string provider, bool isStreaming, IUsageExtractor extractor, out UsageInfo usage)
    {
        ArgumentNullException.ThrowIfNull(extractor);

        usage = default;
        if (_count == 0) return false;

        return extractor.TryExtractUsage(provider: provider, isStreaming: isStreaming,
            bufferedResponseBody: MaterializeTail(), usage: out usage);
    }

    /// <summary>Copies the circular buffer's valid range, oldest byte first, into a contiguous array.</summary>
    private byte[] MaterializeTail()
    {
        var result = new byte[_count];
        var oldestIndex = (_writePos - _count + _maxTailBytes) % _maxTailBytes;
        var firstLen = Math.Min(val1: _count, val2: _maxTailBytes - oldestIndex);
        _buffer.AsSpan(start: oldestIndex, length: firstLen).CopyTo(result);
        var remaining = _count - firstLen;
        if (remaining > 0) _buffer.AsSpan(0, length: remaining).CopyTo(result.AsSpan(firstLen));

        return result;
    }
}