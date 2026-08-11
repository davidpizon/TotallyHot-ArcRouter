using TotallyHot.ArcRouter.Models;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers defaults and domain validation for <see cref="RoutingOptions"/>.
/// </summary>
public class RoutingOptionsTests
{
    /// <summary>
    /// Verifies that phase-2 defaults match the expected contract values.
    /// </summary>
    [Fact]
    public void Defaults_AreExpectedForPhase2Contract()
    {
        var options = new RoutingOptions();

        Assert.Equal(RouterConstants.DefaultModel, options.DefaultModel);
        Assert.Equal(8, options.MaxCandidates);
        Assert.Equal(10, options.MaxNeighborCount);
        Assert.True(options.EnableExploration);
        Assert.Equal(0.05, options.ExplorationRate, 3);
        Assert.Equal(RouterConstants.DefaultPolicy, options.PolicyName);
        Assert.Equal(1.0, options.Epsilon1, 3);
        Assert.Equal(-0.1, options.Epsilon2, 3);
        Assert.Equal(0.3, options.UtilityMinQualityScore, 3);
    }

    /// <summary>
    /// Verifies that unknown default models are rejected by custom validation.
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenDefaultModelIsUnknown()
    {
        var options = new RoutingOptions
        {
            DefaultModel = "unknown-model"
        };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }

    /// <summary>
    /// Verifies that exploration configuration is internally consistent.
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenExplorationDisabledButRateNonZero()
    {
        var options = new RoutingOptions
        {
            EnableExploration = false,
            ExplorationRate = 0.2
        };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }
}

