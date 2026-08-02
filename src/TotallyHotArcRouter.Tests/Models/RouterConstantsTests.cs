using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers constant contract values for <see cref="RouterConstants"/>.
/// </summary>
public class RouterConstantsTests
{
    /// <summary>
    /// Verifies baseline constants remain stable.
    /// </summary>
    [Fact]
    public void Constants_MatchExpectedContract()
    {
        Assert.Equal("kimi-k2.5", RouterConstants.DefaultModel);
        Assert.Equal("fallback", RouterConstants.FallbackReason);
        Assert.Equal("hierarchical", RouterConstants.DefaultPolicy);
    }

    /// <summary>
    /// Verifies supported model list includes default and has no duplicates.
    /// </summary>
    [Fact]
    public void SupportedModels_ContainsDefaultModel_AndHasNoDuplicates()
    {
        Assert.Contains(RouterConstants.DefaultModel, RouterConstants.SupportedModels);

        var distinctCount = RouterConstants.SupportedModels
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(distinctCount, RouterConstants.SupportedModels.Count);
    }
}

