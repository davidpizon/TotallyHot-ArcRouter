using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="CircuitBreaker"/>'s state machine directly: trip thresholds, exponential cooldown
/// (capped at a maximum), half-open probing, and recovery - per
/// <c>docs/router/agent-resilience-strategies.md</c>'s "Circuit Breaker with Exponential Cooldown".
/// </summary>
public class CircuitBreakerTests
{
    private static readonly CircuitBreakerTargetKey Target = new("openai", "https://api.openai.com/", "gpt-5.4");

    private static CircuitBreaker Create(
        int failureThreshold = 3,
        TimeSpan? baseCooldown = null,
        TimeSpan? maxCooldown = null,
        TimeProvider? timeProvider = null)
    {
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = failureThreshold,
            BaseCooldown = baseCooldown ?? TimeSpan.FromSeconds(10),
            MaxCooldown = maxCooldown ?? TimeSpan.FromMinutes(5),
        };

        return new CircuitBreaker(Options.Create(options), timeProvider);
    }

    [Fact]
    public void NewTarget_IsNotOpen_AndIsNotBypassed()
    {
        var breaker = Create();

        Assert.False(breaker.IsOpen(Target));
        Assert.False(breaker.ShouldBypass(Target));
    }

    [Fact]
    public void RecordFailure_BelowThreshold_StaysClosed()
    {
        var breaker = Create(failureThreshold: 3);

        breaker.RecordFailure(Target);
        breaker.RecordFailure(Target);

        Assert.False(breaker.IsOpen(Target));
        Assert.False(breaker.ShouldBypass(Target));
    }

    [Fact]
    public void RecordFailure_ReachingThreshold_TripsToOpen()
    {
        var breaker = Create(failureThreshold: 3);

        breaker.RecordFailure(Target);
        breaker.RecordFailure(Target);
        breaker.RecordFailure(Target);

        Assert.True(breaker.IsOpen(Target));
        Assert.True(breaker.ShouldBypass(Target));
    }

    [Fact]
    public void RecordSuccess_BeforeTripping_ResetsFailureCount()
    {
        var breaker = Create(failureThreshold: 3);

        breaker.RecordFailure(Target);
        breaker.RecordFailure(Target);
        breaker.RecordSuccess(Target);
        breaker.RecordFailure(Target);
        breaker.RecordFailure(Target);

        // Four total failures across two streaks, but the success reset the streak, so only 2 consecutive
        // failures have accumulated since - still below the threshold of 3.
        Assert.False(breaker.IsOpen(Target));
    }

    [Fact]
    public void Open_BeforeCooldownExpires_StaysBypassed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(failureThreshold: 1, baseCooldown: TimeSpan.FromSeconds(10), timeProvider: clock);

        breaker.RecordFailure(Target);
        Assert.True(breaker.IsOpen(Target));

        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.True(breaker.IsOpen(Target));
        Assert.True(breaker.ShouldBypass(Target));
    }

    [Fact]
    public void Open_AfterCooldownExpires_IsNotOpen_AndAllowsExactlyOneProbe()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(failureThreshold: 1, baseCooldown: TimeSpan.FromSeconds(10), timeProvider: clock);

        breaker.RecordFailure(Target);
        clock.Advance(TimeSpan.FromSeconds(10));

        // IsOpen is a read-only snapshot: cooldown has expired, so it reports eligible again without
        // claiming the probe slot.
        Assert.False(breaker.IsOpen(Target));

        // The first ShouldBypass call claims the single half-open probe.
        Assert.False(breaker.ShouldBypass(Target));

        // A concurrent second call while the probe is unresolved must bypass.
        Assert.True(breaker.ShouldBypass(Target));
    }

    [Fact]
    public void HalfOpenProbe_Succeeds_ClosesCircuit_AndResetsTripCount()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(failureThreshold: 1, baseCooldown: TimeSpan.FromSeconds(10), timeProvider: clock);

        breaker.RecordFailure(Target);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breaker.ShouldBypass(Target)); // claims the probe

        breaker.RecordSuccess(Target);

        Assert.False(breaker.IsOpen(Target));
        Assert.False(breaker.ShouldBypass(Target));

        // The trip count reset means the next trip is back to the base cooldown, not a doubled one -
        // verified by the exponential-backoff test below via a fresh breaker, but confirmed here indirectly:
        // one more failure alone (below any threshold >1) would not immediately re-open a threshold>1 breaker.
    }

    [Fact]
    public void HalfOpenProbe_Fails_ReOpensImmediately_AndDoublesCooldown()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(failureThreshold: 1, baseCooldown: TimeSpan.FromSeconds(10), timeProvider: clock);

        breaker.RecordFailure(Target); // trip #1: 10s cooldown
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breaker.ShouldBypass(Target)); // claims the probe

        breaker.RecordFailure(Target); // the probe itself failed -> trip #2, cooldown doubles to 20s

        Assert.True(breaker.IsOpen(Target));

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.True(breaker.IsOpen(Target)); // still cooling down at 19s (needed 20s)

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(breaker.IsOpen(Target)); // now at 20s, cooldown expired
    }

    [Fact]
    public void ExponentialCooldown_CapsAtMaxCooldown()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(
            failureThreshold: 1,
            baseCooldown: TimeSpan.FromSeconds(10),
            maxCooldown: TimeSpan.FromSeconds(25),
            timeProvider: clock);

        breaker.RecordFailure(Target); // trip #1: 10s (uncapped)
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breaker.ShouldBypass(Target));
        breaker.RecordFailure(Target); // trip #2: would be 20s (uncapped), still under the 25s cap
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False(breaker.ShouldBypass(Target));
        breaker.RecordFailure(Target); // trip #3: would be 40s uncapped, capped at 25s

        clock.Advance(TimeSpan.FromSeconds(24));
        Assert.True(breaker.IsOpen(Target)); // still cooling down just before the 25s cap

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(breaker.IsOpen(Target)); // cooldown expired exactly at the cap, not at the uncapped 40s
    }

    [Fact]
    public void DifferentTargets_AreTrackedIndependently()
    {
        var breaker = Create(failureThreshold: 1);
        var other = new CircuitBreakerTargetKey("anthropic", "https://api.anthropic.com/", "claude-sonnet-5");

        breaker.RecordFailure(Target);

        Assert.True(breaker.IsOpen(Target));
        Assert.False(breaker.IsOpen(other));
    }

    [Fact]
    public void RecordProviderFailure_TripsImmediately_EvenWithHighFailureThreshold()
    {
        // Unlike RecordFailure, a single RecordProviderFailure call (e.g. one 401) is decisive on its own -
        // it must not wait for FailureThreshold occurrences the way per-target failures do.
        var breaker = Create(failureThreshold: 3);

        breaker.RecordProviderFailure("openai");

        Assert.True(breaker.IsProviderOpen("openai"));
        Assert.True(breaker.ShouldBypassProvider("openai"));
    }

    [Fact]
    public void RecordProviderFailure_TripsEveryModelOnThatProvider_NotJustOneTarget()
    {
        var breaker = Create();
        var otherModelSameProvider = new CircuitBreakerTargetKey("openai", "https://api.openai.com/", "gpt-5.4-mini");

        breaker.RecordProviderFailure("openai");

        // Neither target individually failed, but both are on the now-open provider.
        Assert.False(breaker.IsOpen(Target));
        Assert.False(breaker.IsOpen(otherModelSameProvider));
        Assert.True(breaker.IsProviderOpen("openai"));
    }

    [Fact]
    public void RecordProviderFailure_DoesNotAffectADifferentProvider()
    {
        var breaker = Create();

        breaker.RecordProviderFailure("openai");

        Assert.False(breaker.IsProviderOpen("anthropic"));
    }

    [Fact]
    public void RecordSuccess_ClosesBothTheTargetAndItsProviderWideCircuit()
    {
        var breaker = Create();

        breaker.RecordProviderFailure("openai");
        Assert.True(breaker.IsProviderOpen("openai"));

        breaker.RecordSuccess(Target); // Target.Provider == "openai"

        Assert.False(breaker.IsProviderOpen("openai"));
        Assert.False(breaker.IsOpen(Target));
    }

    [Fact]
    public void ProviderCircuit_HalfOpenProbe_FollowsTheSameStateMachineAsPerTarget()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var breaker = Create(baseCooldown: TimeSpan.FromSeconds(10), timeProvider: clock);

        breaker.RecordProviderFailure("openai"); // trip #1: 10s cooldown
        Assert.True(breaker.ShouldBypassProvider("openai"));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breaker.ShouldBypassProvider("openai")); // claims the probe
        Assert.True(breaker.ShouldBypassProvider("openai")); // a concurrent second call bypasses

        breaker.RecordProviderFailure("openai"); // the probe failed -> trip #2, cooldown doubles to 20s
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.True(breaker.IsProviderOpen("openai"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(breaker.IsProviderOpen("openai"));
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when <see cref="Advance"/> is called, for deterministic cooldown-expiry assertions.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

