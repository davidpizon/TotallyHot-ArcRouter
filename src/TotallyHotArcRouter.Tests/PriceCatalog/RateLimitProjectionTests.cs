using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="RateLimitProjection.Project"/>.</summary>
public class RateLimitProjectionTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-03-01T12:00:00Z");
    private static readonly DateTimeOffset Later = DateTimeOffset.Parse("2026-03-01T12:10:00Z");

    [Fact]
    public void Project_NormalDepletion_ReturnsProjection()
    {
        // 10,000 tokens consumed over 10 minutes -> 1,000/min; 50,000 remain -> 50 minutes to exhaustion.
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 50_000, null);

        var projection = RateLimitProjection.Project(earlier: earlier, later: later);

        Assert.NotNull(projection);
        Assert.Equal(1000d, actual: projection.BurnRatePerMinute, 3);
        Assert.Equal(expected: TimeSpan.FromMinutes(50), actual: projection.TimeToExhaustion);
    }

    [Fact]
    public void Project_Flat_ReturnsNull()
    {
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 60_000, null);

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
    }

    [Fact]
    public void Project_Refilled_ReturnsNull()
    {
        // Remaining went up between observations - a reset landed in between, not a negative burn rate.
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 10_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 60_000, null);

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
    }

    [Fact]
    public void Project_ResetBeforeEmpty_ReturnsNull()
    {
        // At the observed rate exhaustion is 50 minutes out, but the dimension resets in 5 - no fabricated
        // urgency for a limit that will refill long before it empties.
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 50_000, ResetAt: Later.AddMinutes(5));

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
    }

    [Fact]
    public void Project_ResetAfterProjectedExhaustion_StillReturnsProjection()
    {
        // The reset (60 min out) is well after the projected exhaustion (50 min out) - the projection stands.
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 50_000, ResetAt: Later.AddMinutes(60));

        Assert.NotNull(RateLimitProjection.Project(earlier: earlier, later: later));
    }

    [Fact]
    public void Project_EitherRemainingUnknown_ReturnsNull()
    {
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, null, null);
        var later = new RateLimitObservation(ObservedAtUtc: Later, 50_000, null);

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
        Assert.Null(RateLimitProjection.Project(earlier: later, later: earlier with { Remaining = null }));
    }

    [Fact]
    public void Project_ObservationsOutOfOrder_ReturnsNull()
    {
        // "earlier" is actually after "later" chronologically.
        var earlier = new RateLimitObservation(ObservedAtUtc: Later, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Earlier, 50_000, null);

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
    }

    [Fact]
    public void Project_SameInstant_ReturnsNull()
    {
        var earlier = new RateLimitObservation(ObservedAtUtc: Earlier, 60_000, null);
        var later = new RateLimitObservation(ObservedAtUtc: Earlier, 50_000, null);

        Assert.Null(RateLimitProjection.Project(earlier: earlier, later: later));
    }
}