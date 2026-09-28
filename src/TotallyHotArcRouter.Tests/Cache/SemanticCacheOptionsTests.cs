using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Cache;

namespace TotallyHot.ArcRouter.Tests.Cache;

/// <summary>
/// Covers the documented defaults and validation for <see cref="SemanticCacheOptions"/>.
/// </summary>
public class SemanticCacheOptionsTests
{
    /// <summary>
    /// The cache ships off, with a similarity bar stricter than routing's neighbor threshold.
    /// </summary>
    [Fact]
    public void Defaults_AreOffWithAConservativeThresholdAndOneHourTtl()
    {
        var options = new SemanticCacheOptions();

        Assert.False(options.Enabled);
        Assert.Equal(0.92, actual: options.SimilarityThreshold, precision: 3);
        Assert.Equal(TimeSpan.FromHours(1), actual: options.TimeToLive);
        Assert.Equal(256, actual: options.MaxEntries);
        Assert.Equal(1024 * 1024, actual: options.MaxStoredResponseBytes);
        Assert.Equal(0, actual: options.CacheEpoch);
        options.EnsureValid();
    }

    /// <summary>A threshold of 0 or above 1 would make every lookup a hit or a miss by accident.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    public void EnsureValid_RejectsSimilarityOutsideTheOpenUnitInterval(double threshold)
    {
        var options = new SemanticCacheOptions { SimilarityThreshold = threshold };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>A non-positive TTL would either never expire or expire everything immediately.</summary>
    [Fact]
    public void EnsureValid_RejectsNonPositiveTimeToLive()
    {
        var options = new SemanticCacheOptions { TimeToLive = TimeSpan.Zero };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }
}
