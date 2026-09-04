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
        Assert.Equal(expected: "kimi-k2.5", actual: RouterConstants.DefaultModel);
        Assert.Equal(expected: "fallback", actual: RouterConstants.FallbackReason);
    }

    /// <summary>
    /// Verifies supported model list includes default and has no duplicates.
    /// </summary>
    [Fact]
    public void SupportedModels_ContainsDefaultModel_AndHasNoDuplicates()
    {
        Assert.Contains(expected: RouterConstants.DefaultModel, collection: RouterConstants.SupportedModels);

        var distinctCount = RouterConstants.SupportedModels
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(expected: distinctCount, actual: RouterConstants.SupportedModels.Count);
    }
}