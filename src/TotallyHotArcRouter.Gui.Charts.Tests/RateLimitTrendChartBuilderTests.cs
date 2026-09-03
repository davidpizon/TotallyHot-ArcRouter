namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>Covers <see cref="RateLimitTrendChartBuilder"/>.</summary>
public class RateLimitTrendChartBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_OrdersPointsChronologicallyRegardlessOfInputOrder()
    {
        var points = new (DateTimeOffset, long?, long?)[]
        {
            (T0.AddMinutes(2), 800, 1000),
            (T0, 1000, 1000),
            (T0.AddMinutes(1), 900, 1000),
        };

        var model = RateLimitTrendChartBuilder.Build("input-tokens", points);

        Assert.Equal(3, model.Points.Count);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), model.Points[0].T);
        Assert.Equal(T0.AddMinutes(1).ToUnixTimeMilliseconds(), model.Points[1].T);
        Assert.Equal(T0.AddMinutes(2).ToUnixTimeMilliseconds(), model.Points[2].T);
    }

    [Fact]
    public void Build_TitleIsHumanReadableDimensionLabel()
    {
        var model = RateLimitTrendChartBuilder.Build("input-tokens", []);

        Assert.Equal("Input tokens", model.Title);
    }

    [Theory]
    [InlineData("requests", "req")]
    [InlineData("input-tokens", "tok")]
    [InlineData("output-tokens", "tok")]
    [InlineData("tokens", "tok")]
    [InlineData("Requests", "req")]
    public void Build_UnitReflectsDimension_RequestsIsReqEverythingElseIsTok(string dimensionName, string expectedUnit)
    {
        var model = RateLimitTrendChartBuilder.Build(dimensionName, []);

        Assert.Equal(expectedUnit, model.Unit);
    }

    [Fact]
    public void Build_KindIsRemainingLine()
    {
        var model = RateLimitTrendChartBuilder.Build("tokens", []);

        Assert.Equal(RateLimitTrendChartKind.RemainingLine, model.Kind);
    }

    [Fact]
    public void Build_NoPoints_HeadlineIsDash()
    {
        var model = RateLimitTrendChartBuilder.Build("tokens", []);

        Assert.Equal("—", model.Headline);
    }

    [Fact]
    public void Build_HeadlineIsMostRecentRemaining()
    {
        var points = new (DateTimeOffset, long?, long?)[]
        {
            (T0, 1000, null),
            (T0.AddMinutes(1), 800, null),
        };

        var model = RateLimitTrendChartBuilder.Build("tokens", points);

        Assert.Equal("800", model.Headline);
    }

    [Fact]
    public void Build_LastPointRemainingUnknown_HeadlineIsDash()
    {
        var points = new (DateTimeOffset, long?, long?)[] { (T0, null, null) };

        var model = RateLimitTrendChartBuilder.Build("tokens", points);

        Assert.Equal("—", model.Headline);
    }

    [Fact]
    public void Build_NullPoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RateLimitTrendChartBuilder.Build("tokens", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_EmptyOrWhitespaceDimensionName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(() => RateLimitTrendChartBuilder.Build(name, []));
    }

    [Fact]
    public void Build_NullDimensionName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RateLimitTrendChartBuilder.Build(null!, []));
    }
}
