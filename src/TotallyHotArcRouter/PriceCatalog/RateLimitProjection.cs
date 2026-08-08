namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One observation of a rate-limit dimension's remaining budget at a point in time - the input to
/// <see cref="RateLimitProjection.Project"/>.
/// </summary>
/// <param name="ObservedAtUtc">When this observation was captured.</param>
/// <param name="Remaining">The dimension's remaining budget at this instant, or <see langword="null"/> if unknown.</param>
/// <param name="ResetAt">When the dimension is due to reset/refill, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitObservation(DateTimeOffset ObservedAtUtc, long? Remaining, DateTimeOffset? ResetAt);

/// <summary>
/// A projected time-to-exhaustion for one rate-limit dimension, computed from two observations of its
/// remaining budget.
/// </summary>
/// <param name="TimeToExhaustion">How long until the dimension would hit zero at the observed burn rate.</param>
/// <param name="BurnRatePerMinute">The observed consumption rate, in the dimension's own unit (tokens or requests) per minute.</param>
public sealed record RateLimitExhaustionProjection(TimeSpan TimeToExhaustion, double BurnRatePerMinute);

/// <summary>
/// Projects when a rate-limit dimension will exhaust from two observations of its remaining budget, per
/// <c>docs/router/token-tracking-improvements.md</c> §5.9. Pure and stateless - no I/O, no wall-clock
/// reads - so it is unit-testable against fixture observations without a database or a clock.
/// </summary>
public static class RateLimitProjection
{
    /// <summary>
    /// Projects <paramref name="later"/>'s time to exhaustion from the burn rate observed between
    /// <paramref name="earlier"/> and <paramref name="later"/>.
    /// </summary>
    /// <param name="earlier">The older observation.</param>
    /// <param name="later">The newer observation.</param>
    /// <returns>
    /// The projection, or <see langword="null"/> - deliberately "no fabricated urgency" - when: either
    /// observation's remaining count is unknown; the observations aren't in chronological order (or share
    /// the same instant); remaining is flat or increased between them (no burn, or a refill already
    /// happened); or the dimension is due to reset before the projected exhaustion instant (it will refill
    /// before it empties, so there is nothing to warn about).
    /// </returns>
    public static RateLimitExhaustionProjection? Project(RateLimitObservation earlier, RateLimitObservation later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        if (earlier.Remaining is not { } earlierRemaining || later.Remaining is not { } laterRemaining)
        {
            return null;
        }

        var elapsedMinutes = (later.ObservedAtUtc - earlier.ObservedAtUtc).TotalMinutes;
        if (elapsedMinutes <= 0)
        {
            return null;
        }

        var consumed = earlierRemaining - laterRemaining;
        if (consumed <= 0)
        {
            // Flat (no traffic since the earlier observation) or refilled (a reset landed between the two
            // observations) - either way, the current gap can't be trusted as a burn rate.
            return null;
        }

        var ratePerMinute = consumed / elapsedMinutes;
        var minutesToExhaustion = laterRemaining / ratePerMinute;
        var exhaustionAt = later.ObservedAtUtc.AddMinutes(minutesToExhaustion);

        if (later.ResetAt is { } resetAt && resetAt <= exhaustionAt)
        {
            // Reset-before-empty: the dimension refills before it would run out at this rate.
            return null;
        }

        return new RateLimitExhaustionProjection(TimeSpan.FromMinutes(minutesToExhaustion), ratePerMinute);
    }
}
