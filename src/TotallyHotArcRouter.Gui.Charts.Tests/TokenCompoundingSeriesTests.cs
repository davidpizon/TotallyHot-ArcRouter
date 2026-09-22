namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Covers <see cref="TokenCompoundingSeries.BuildSparkline"/>, the compact per-turn series for the
/// conversation summary sparkline.
/// </summary>
public class TokenCompoundingSeriesTests
{
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
