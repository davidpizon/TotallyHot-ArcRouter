using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TokenizerRatio"/>, whose whole reason to exist is that <c>1.0</c> is ambiguous: it is
/// the right answer for two models that share a tokenizer, and also what falls out when both were counted
/// with the same stand-in encoding. These tests pin which of those the type will call a measurement.
/// </summary>
public class TokenizerRatioTests
{
    [Fact]
    public void Assumed_IsOneAndSaysSo()
    {
        Assert.Equal(expected: 1d, actual: TokenizerRatio.Assumed.Value, tolerance: 0.0001d);
        Assert.False(TokenizerRatio.Assumed.IsMeasured);
    }

    [Fact]
    public void Measure_BothSidesNative_IsMeasured()
    {
        var ratio = TokenizerRatio.Measure(
            baselineTokens: 120, baselineSource: TokenCountSource.LocalNative,
            routedTokens: 100, routedSource: TokenCountSource.LocalNative);

        Assert.True(ratio.IsMeasured);
        Assert.Equal(expected: 1.2d, actual: ratio.Value, tolerance: 0.0001d);
    }

    [Fact]
    public void Measure_BothSidesCalibrated_IsMeasured()
    {
        // A proxy encoding corrected toward its model does represent that model, so a ratio between two
        // such counts is a real comparison.
        var ratio = TokenizerRatio.Measure(
            baselineTokens: 135, baselineSource: TokenCountSource.LocalCalibrated,
            routedTokens: 100, routedSource: TokenCountSource.LocalCalibrated);

        Assert.True(ratio.IsMeasured);
        Assert.Equal(expected: 1.35d, actual: ratio.Value, tolerance: 0.0001d);
    }

    [Fact]
    public void Measure_BothSidesUncorrectedProxy_IsNotMeasuredEvenThoughItLooksLikeACleanOne()
    {
        // The case this type exists for. Claude and Gemini both fall back to cl100k_base, so the counts are
        // identical and the ratio is exactly 1.0 - which is a fact about the stand-in encoding, not about
        // the two models' real tokenizers.
        var ratio = TokenizerRatio.Measure(
            baselineTokens: 100, baselineSource: TokenCountSource.LocalProxy,
            routedTokens: 100, routedSource: TokenCountSource.LocalProxy);

        Assert.False(ratio.IsMeasured);
        Assert.Equal(expected: 1d, actual: ratio.Value, tolerance: 0.0001d);
    }

    [Theory]
    [InlineData(TokenCountSource.LocalProxy, TokenCountSource.LocalNative)]
    [InlineData(TokenCountSource.LocalNative, TokenCountSource.LocalProxy)]
    [InlineData(TokenCountSource.Heuristic, TokenCountSource.LocalNative)]
    [InlineData(TokenCountSource.LocalNative, TokenCountSource.Heuristic)]
    public void Measure_EitherSideBelowTheBar_IsNotMeasured(TokenCountSource baseline, TokenCountSource routed)
    {
        // One good count does not rescue the comparison: a ratio is a statement about *both* models.
        var ratio = TokenizerRatio.Measure(baselineTokens: 130, baselineSource: baseline, routedTokens: 100,
            routedSource: routed);

        Assert.False(ratio.IsMeasured);
        Assert.Equal(expected: 1d, actual: ratio.Value, tolerance: 0.0001d);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void Measure_NonPositiveCounts_FallBackToAssumed(int baselineTokens, int routedTokens)
    {
        var ratio = TokenizerRatio.Measure(
            baselineTokens: baselineTokens, baselineSource: TokenCountSource.LocalNative,
            routedTokens: routedTokens, routedSource: TokenCountSource.LocalNative);

        Assert.False(ratio.IsMeasured);
        Assert.Equal(expected: 1d, actual: ratio.Value, tolerance: 0.0001d);
    }

    [Fact]
    public void TrustLadder_OrdersFromLeastToMostTrustworthy()
    {
        // Measure() compares sources with '<', so the declaration order is load-bearing rather than cosmetic.
        Assert.True(TokenCountSource.Unavailable < TokenCountSource.Heuristic);
        Assert.True(TokenCountSource.Heuristic < TokenCountSource.LocalProxy);
        Assert.True(TokenCountSource.LocalProxy < TokenCountSource.LocalCalibrated);
        Assert.True(TokenCountSource.LocalCalibrated < TokenCountSource.LocalNative);
        Assert.True(TokenCountSource.LocalNative < TokenCountSource.ProviderExact);
    }
}
