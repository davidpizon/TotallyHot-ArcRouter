using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="CalibratedTokenCounter"/>: that a trusted factor is applied to a real tokenizer's
/// count and only to that, and that every way a factor can be missing or nonsensical degrades to a
/// transparent pass-through rather than a corrupted number.
/// </summary>
public class CalibratedTokenCounterTests
{
    private static readonly ModelKey Claude = new(ModelName: "claude-opus-5", Provider: "anthropic");

    [Fact]
    public void TryCountPromptTokens_TrustedFactor_ScalesAndRelabelsAsCalibrated()
    {
        var inner = new StubCounter(tokens: 100, source: TokenCountSource.LocalUncalibrated);
        var counter = new CalibratedTokenCounter(inner: inner, calibration: new StubCalibration(1.20d));

        var counted = counter.TryCountPromptTokens(text: "anything", key: Claude, tokens: out var tokens,
            source: out var source);

        Assert.True(counted);
        Assert.Equal(expected: 120, actual: tokens);
        Assert.Equal(expected: TokenCountSource.LocalCalibrated, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_HeuristicCount_IsNotCalibrated()
    {
        // The factor was learned against the tiktoken count, so applying it to a character-length guess
        // would compose two unrelated approximations and present the result as the better of the two.
        var inner = new StubCounter(tokens: 100, source: TokenCountSource.Heuristic);
        var counter = new CalibratedTokenCounter(inner: inner, calibration: new StubCalibration(1.20d));

        counter.TryCountPromptTokens(text: "anything", key: Claude, tokens: out var tokens, source: out var source);

        Assert.Equal(expected: 100, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Heuristic, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_NoCalibrationSource_PassesThroughUncalibrated()
    {
        var inner = new StubCounter(tokens: 100, source: TokenCountSource.LocalUncalibrated);
        var counter = new CalibratedTokenCounter(inner);

        counter.TryCountPromptTokens(text: "anything", key: Claude, tokens: out var tokens, source: out var source);

        Assert.Equal(expected: 100, actual: tokens);
        Assert.Equal(expected: TokenCountSource.LocalUncalibrated, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_UntrustedFactor_PassesThroughUncalibrated()
    {
        var inner = new StubCounter(tokens: 100, source: TokenCountSource.LocalUncalibrated);
        var counter = new CalibratedTokenCounter(inner: inner, calibration: new StubCalibration(factor: 1.20d, trusted: false));

        counter.TryCountPromptTokens(text: "anything", key: Claude, tokens: out var tokens, source: out var source);

        Assert.Equal(expected: 100, actual: tokens);
        Assert.Equal(expected: TokenCountSource.LocalUncalibrated, actual: source);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryCountPromptTokens_NonsensicalFactor_IsIgnored(double factor)
    {
        var inner = new StubCounter(tokens: 100, source: TokenCountSource.LocalUncalibrated);
        var counter = new CalibratedTokenCounter(inner: inner, calibration: new StubCalibration(factor));

        counter.TryCountPromptTokens(text: "anything", key: Claude, tokens: out var tokens, source: out var source);

        Assert.Equal(expected: 100, actual: tokens);
        Assert.Equal(expected: TokenCountSource.LocalUncalibrated, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_TinyFactorOnTinyCount_FloorsAtOneNotZero()
    {
        // Text that exists must never calibrate down to "no tokens" - that would read as a free request.
        var inner = new StubCounter(tokens: 1, source: TokenCountSource.LocalUncalibrated);
        var counter = new CalibratedTokenCounter(inner: inner, calibration: new StubCalibration(0.01d));

        counter.TryCountPromptTokens(text: "x", key: Claude, tokens: out var tokens, source: out _);

        Assert.Equal(expected: 1, actual: tokens);
    }

    [Fact]
    public void TryCountPromptTokens_InnerRefuses_StaysRefused()
    {
        var counter = new CalibratedTokenCounter(inner: new StubCounter(serves: false),
            calibration: new StubCalibration(1.20d));

        var counted = counter.TryCountPromptTokens(text: null, key: Claude, tokens: out var tokens,
            source: out var source);

        Assert.False(counted);
        Assert.Equal(expected: 0, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Unavailable, actual: source);
    }

    /// <summary>A counter that returns a fixed answer, so the decorator's behavior is isolated from real tokenization.</summary>
    private sealed class StubCounter : ITokenCounter
    {
        private readonly bool _serves;
        private readonly TokenCountSource _source;
        private readonly int _tokens;

        /// <summary>Initializes a stub returning the given count and source.</summary>
        /// <param name="tokens">The count to report.</param>
        /// <param name="source">The source to report.</param>
        /// <param name="serves">Whether the stub reports success at all.</param>
        public StubCounter(int tokens = 0, TokenCountSource source = TokenCountSource.Unavailable, bool serves = true)
        {
            _tokens = tokens;
            _source = source;
            _serves = serves;
        }

        /// <inheritdoc/>
        public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
        {
            tokens = _serves ? _tokens : 0;
            source = _serves ? _source : TokenCountSource.Unavailable;
            return _serves;
        }
    }

    /// <summary>A calibration source returning a fixed factor and trust verdict.</summary>
    private sealed class StubCalibration : ITokenCalibrationSource
    {
        private readonly double _factor;
        private readonly bool _trusted;

        /// <summary>Initializes a stub with the given factor.</summary>
        /// <param name="factor">The factor to hand back.</param>
        /// <param name="trusted">Whether the factor is reported as trusted.</param>
        public StubCalibration(double factor, bool trusted = true)
        {
            _factor = factor;
            _trusted = trusted;
        }

        /// <inheritdoc/>
        public bool TryGetTrustedFactor(ModelKey key, out double factor)
        {
            factor = _factor;
            return _trusted;
        }
    }
}
