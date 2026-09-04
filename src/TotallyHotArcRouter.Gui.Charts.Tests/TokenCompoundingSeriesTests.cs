namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Covers <see cref="TokenCompoundingSeries"/>: the cumulative "hockey stick" series for the Cost
/// Analytics chart and the compact per-turn series for the conversation summary sparkline.
/// </summary>
public class TokenCompoundingSeriesTests
{
    [Fact]
    public void Build_NullTurns_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TokenCompoundingSeries.Build(null!));
    }

    [Fact]
    public void Build_EmptyList_ReturnsEmpty()
    {
        var result = TokenCompoundingSeries.Build([]);

        Assert.Empty(result);
    }

    [Fact]
    public void Build_SingleTurn_ReturnsThatTurnsTotalsAsCumulative()
    {
        var result = TokenCompoundingSeries.Build([new TurnTokenPoint(1, 100, 40)]);

        var point = Assert.Single(result);
        Assert.Equal(1, actual: point.TurnNumber);
        Assert.Equal(100, actual: point.CumulativePromptTokens);
        Assert.Equal(40, actual: point.CumulativeCompletionTokens);
        Assert.Equal(140, actual: point.CumulativeTotalTokens);
    }

    [Fact]
    public void Build_MultipleTurnsInOrder_AccumulatesCorrectly()
    {
        IReadOnlyList<TurnTokenPoint> turns =
        [
            new(1, 2104, 891),
            new(2, 3240, 1205),
            new(3, 4567, 1798)
        ];

        var result = TokenCompoundingSeries.Build(turns);

        Assert.Equal(3, actual: result.Count);

        Assert.Equal(1, actual: result[0].TurnNumber);
        Assert.Equal(2104, actual: result[0].CumulativePromptTokens);
        Assert.Equal(891, actual: result[0].CumulativeCompletionTokens);

        Assert.Equal(2, actual: result[1].TurnNumber);
        Assert.Equal(expected: 2104 + 3240, actual: result[1].CumulativePromptTokens);
        Assert.Equal(expected: 891 + 1205, actual: result[1].CumulativeCompletionTokens);

        Assert.Equal(3, actual: result[2].TurnNumber);
        Assert.Equal(expected: 2104 + 3240 + 4567, actual: result[2].CumulativePromptTokens);
        Assert.Equal(expected: 891 + 1205 + 1798, actual: result[2].CumulativeCompletionTokens);
        Assert.Equal(expected: result[2].CumulativePromptTokens + result[2].CumulativeCompletionTokens,
            actual: result[2].CumulativeTotalTokens);
    }

    [Fact]
    public void Build_UnsortedInput_IsSortedByTurnNumberBeforeAccumulating()
    {
        IReadOnlyList<TurnTokenPoint> turns =
        [
            new(2, 300, 30),
            new(1, 100, 10),
            new(3, 500, 50)
        ];

        var result = TokenCompoundingSeries.Build(turns);

        Assert.Equal(expected: [1, 2, 3], actual: result.Select(p => p.TurnNumber));
        // Turn 1 must be accumulated first regardless of input order, or turn 2's cumulative would be wrong.
        Assert.Equal(100, actual: result[0].CumulativePromptTokens);
        Assert.Equal(expected: 100 + 300, actual: result[1].CumulativePromptTokens);
        Assert.Equal(expected: 100 + 300 + 500, actual: result[2].CumulativePromptTokens);
    }

    [Fact]
    public void Build_ZeroCompletionTokens_StillAccumulatesPrompt()
    {
        // Mirrors a real mock turn: a final summary-only turn with 0 completion tokens.
        IReadOnlyList<TurnTokenPoint> turns =
        [
            new(1, 1000, 200),
            new(2, 500, 0)
        ];

        var result = TokenCompoundingSeries.Build(turns);

        Assert.Equal(1500, actual: result[1].CumulativePromptTokens);
        Assert.Equal(200, actual: result[1].CumulativeCompletionTokens);
    }

    [Fact]
    public void BuildSparkline_NullTurns_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TokenCompoundingSeries.BuildSparkline(null!));
    }

    [Fact]
    public void BuildSparkline_EmptyList_ReturnsEmpty()
    {
        var result = TokenCompoundingSeries.BuildSparkline([]);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildSparkline_ReturnsPerTurnTotalsNotCumulative()
    {
        IReadOnlyList<TurnTokenPoint> turns =
        [
            new(1, 100, 20),
            new(2, 150, 25)
        ];

        var result = TokenCompoundingSeries.BuildSparkline(turns);

        // Per-turn totals (120, 175), not cumulative (120, 295) - a sparkline shows the trend shape,
        // not a running sum.
        Assert.Equal(expected: [120, 175], actual: result);
    }

    [Fact]
    public void BuildSparkline_UnsortedInput_IsSortedByTurnNumber()
    {
        IReadOnlyList<TurnTokenPoint> turns =
        [
            new(3, 50, 10),
            new(1, 100, 20),
            new(2, 150, 25)
        ];

        var result = TokenCompoundingSeries.BuildSparkline(turns);

        Assert.Equal(expected: [120, 175, 60], actual: result);
    }
}