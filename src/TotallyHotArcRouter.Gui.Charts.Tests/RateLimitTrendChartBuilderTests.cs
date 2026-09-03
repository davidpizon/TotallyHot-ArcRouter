namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>Covers <see cref="RateLimitTrendChartBuilder"/>.</summary>
public class RateLimitTrendChartBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);

    [Fact]
    public void Build_OrdersPointsChronologicallyRegardlessOfInputOrder()
    {
        var points = new (DateTimeOffset, long?, long?)[]
        {
            (T0.AddMinutes(2), 800, 1000),
            (T0, 1000, 1000),
            (T0.AddMinutes(1), 900, 1000)
        };

        var model = RateLimitTrendChartBuilder.Build(dimensionName: "input-tokens", points: points);

        Assert.Equal(3, actual: model.Points.Count);
        Assert.Equal(expected: T0.ToUnixTimeMilliseconds(), actual: model.Points[0].T);
        Assert.Equal(expected: T0.AddMinutes(1).ToUnixTimeMilliseconds(), actual: model.Points[1].T);
        Assert.Equal(expected: T0.AddMinutes(2).ToUnixTimeMilliseconds(), actual: model.Points[2].T);
    }

    [Fact]
    public void Build_TitleIsHumanReadableDimensionLabel()
    {
        var model = RateLimitTrendChartBuilder.Build(dimensionName: "input-tokens", points: []);

        Assert.Equal(expected: "Input tokens", actual: model.Title);
    }

    [Theory]
    [InlineData("requests", "req")]
    [InlineData("input-tokens", "tok")]
    [InlineData("output-tokens", "tok")]
    [InlineData("tokens", "tok")]
    [InlineData("Requests", "req")]
    public void Build_UnitReflectsDimension_RequestsIsReqEverythingElseIsTok(string dimensionName, string expectedUnit)
    {
        var model = RateLimitTrendChartBuilder.Build(dimensionName: dimensionName, points: []);

        Assert.Equal(expected: expectedUnit, actual: model.Unit);
    }

    [Fact]
    public void Build_KindIsRemainingLine()
    {
        var model = RateLimitTrendChartBuilder.Build(dimensionName: "tokens", points: []);

        Assert.Equal(expected: RateLimitTrendChartKind.RemainingLine, actual: model.Kind);
    }

    [Fact]
    public void Build_NoPoints_HeadlineIsDash()
    {
        var model = RateLimitTrendChartBuilder.Build(dimensionName: "tokens", points: []);

        Assert.Equal(expected: "—", actual: model.Headline);
    }

    [Fact]
    public void Build_HeadlineIsMostRecentRemaining()
    {
        var points = new (DateTimeOffset, long?, long?)[]
        {
            (T0, 1000, null),
            (T0.AddMinutes(1), 800, null)
        };

        var model = RateLimitTrendChartBuilder.Build(dimensionName: "tokens", points: points);

        Assert.Equal(expected: "800", actual: model.Headline);
    }

    [Fact]
    public void Build_LastPointRemainingUnknown_HeadlineIsDash()
    {
        var points = new (DateTimeOffset, long?, long?)[] { (T0, null, null) };

        var model = RateLimitTrendChartBuilder.Build(dimensionName: "tokens", points: points);

        Assert.Equal(expected: "—", actual: model.Headline);
    }

    [Fact]
    public void Build_NullPoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RateLimitTrendChartBuilder.Build(dimensionName: "tokens", points: null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_EmptyOrWhitespaceDimensionName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(() => RateLimitTrendChartBuilder.Build(dimensionName: name, points: []));
    }

    [Fact]
    public void Build_NullDimensionName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RateLimitTrendChartBuilder.Build(dimensionName: null!, points: []));
    }
}