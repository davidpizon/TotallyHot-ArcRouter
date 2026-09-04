using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Tuning knobs for the per-upstream-target circuit breaker (<c>docs/router/agent-resilience-strategies.md</c>'s
/// "Circuit Breaker with Exponential Cooldown"), bound from the <c>CircuitBreaker</c> configuration section.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// Gets the configuration section name used for circuit-breaker settings.
    /// </summary>
    public const string SectionName = "CircuitBreaker";

    /// <summary>
    /// Gets the number of consecutive failures (timeouts, 429s, 5xxs) against one upstream target before
    /// its circuit trips to OPEN. Defaults to 3, matching the design doc's example.
    /// </summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>
    /// Gets the isolation cooldown applied on a target's first trip. Each subsequent consecutive trip
    /// doubles the previous cooldown (capped at <see cref="MaxCooldown"/>) - see
    /// <c>TotallyHot.ArcRouter.Proxy.CircuitBreaker</c>. Defaults to 10 seconds, matching the design doc's example.
    /// </summary>
    public TimeSpan BaseCooldown { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the ceiling the exponentially-doubling cooldown never exceeds, so a target that keeps failing
    /// probes is retried at most this often rather than backing off indefinitely. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaxCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the configuration is inconsistent.</exception>
    public void EnsureValid()
    {
        var errors = new List<string>();

        if (FailureThreshold < 1) errors.Add("FailureThreshold must be at least 1.");

        if (BaseCooldown <= TimeSpan.Zero) errors.Add("BaseCooldown must be a positive duration.");

        if (MaxCooldown < BaseCooldown) errors.Add("MaxCooldown must be greater than or equal to BaseCooldown.");

        if (errors.Count > 0)
            throw new OptionsValidationException(optionsName: nameof(CircuitBreakerOptions),
                optionsType: typeof(CircuitBreakerOptions), failureMessages: errors);
    }
}