using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers the promotion predicate docs/router/self-organizing-classification-plan.md Phase T4 defines -
/// the pure function its exit bar requires be "implemented and tested as a pure function over the two MAE
/// series and the coverage rate".
/// </summary>
public sealed class TaxonomyPromotionCriterionTests
{
    /// <summary>Builds a window, defaulting to one that comfortably qualifies so each test varies only what it is about.</summary>
    private static TaxonomyComparisonWindow Window(
        double? dimensionError = 0.30,
        double? clusterError = 0.10,
        double coverage = 0.95) =>
        new(dimensionError, clusterError, coverage);

    [Fact]
    public void IsMet_FourConsecutiveQualifyingWindows_ReturnsTrue()
    {
        var windows = new[] { Window(), Window(), Window(), Window() };

        Assert.True(TaxonomyPromotionCriterion.IsMet(windows));
    }

    [Fact]
    public void IsMet_FewerWindowsThanRequired_ReturnsFalse()
    {
        // Three perfect windows still cannot satisfy a K=4 criterion - the evidence simply is not in yet.
        var windows = new[] { Window(), Window(), Window() };

        Assert.False(TaxonomyPromotionCriterion.IsMet(windows));
    }

    [Fact]
    public void IsMet_OnlyTheMostRecentWindowsAreExamined()
    {
        // An early failing window is history: the criterion is "over K consecutive windows", so four good
        // ones after a bad one qualify.
        var windows = new[] { Window(clusterError: 0.9), Window(), Window(), Window(), Window() };

        Assert.True(TaxonomyPromotionCriterion.IsMet(windows));
    }

    [Fact]
    public void IsMet_MostRecentWindowRegresses_ReturnsFalse()
    {
        var windows = new[] { Window(), Window(), Window(), Window(), Window(clusterError: 0.9) };

        Assert.False(TaxonomyPromotionCriterion.IsMet(windows));
    }

    [Fact]
    public void Qualifies_ClusterErrorEqualToDimensionError_ReturnsFalse()
    {
        // "Strictly lower" - a tie is not evidence the learned taxonomy explains traffic better.
        Assert.False(TaxonomyPromotionCriterion.Qualifies(Window(dimensionError: 0.2, clusterError: 0.2)));
    }

    [Theory]
    [InlineData(0.79, false)]
    [InlineData(0.80, true)]
    [InlineData(0.81, true)]
    public void Qualifies_CoverageFloorIsInclusive(double coverage, bool expected)
    {
        Assert.Equal(expected, TaxonomyPromotionCriterion.Qualifies(Window(coverage: coverage)));
    }

    [Fact]
    public void Qualifies_MissingClusterError_ReturnsFalse()
    {
        // An unmeasurable comparison is a failure to demonstrate, never a pass by default.
        Assert.False(TaxonomyPromotionCriterion.Qualifies(Window(clusterError: null)));
    }

    [Fact]
    public void Qualifies_MissingDimensionError_ReturnsFalse()
    {
        Assert.False(TaxonomyPromotionCriterion.Qualifies(Window(dimensionError: null)));
    }

    [Fact]
    public void IsMet_EmptyHistory_ReturnsFalse()
    {
        Assert.False(TaxonomyPromotionCriterion.IsMet([]));
    }

    [Fact]
    public void IsMet_NonPositiveWindowCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxonomyPromotionCriterion.IsMet([Window()], consecutiveWindows: 0));
    }
}
