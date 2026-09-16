using TotallyHot.ArcRouter.Proxy.Auth;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth;

/// <summary>
/// Covers <see cref="LoginRateLimiter"/>: the Phase P4 exit criterion "login throttling works", plus that
/// a success clears the counter and a different key is never penalized by another key's failures.
/// </summary>
public sealed class LoginRateLimiterTests
{
    [Fact]
    public void ShouldThrottle_BeforeAnyFailures_ReturnsFalse()
    {
        var limiter = new LoginRateLimiter();

        Assert.False(limiter.ShouldThrottle("1.2.3.4"));
    }

    [Fact]
    public void ShouldThrottle_AfterMaxAttempts_ReturnsTrue()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < LoginRateLimiter.MaxAttempts; i++) limiter.RecordFailure("1.2.3.4");

        Assert.True(limiter.ShouldThrottle("1.2.3.4"));
    }

    [Fact]
    public void ShouldThrottle_FewerThanMaxAttempts_ReturnsFalse()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < LoginRateLimiter.MaxAttempts - 1; i++) limiter.RecordFailure("1.2.3.4");

        Assert.False(limiter.ShouldThrottle("1.2.3.4"));
    }

    [Fact]
    public void RecordSuccess_ClearsTheCounter()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < LoginRateLimiter.MaxAttempts; i++) limiter.RecordFailure("1.2.3.4");

        limiter.RecordSuccess("1.2.3.4");

        Assert.False(limiter.ShouldThrottle("1.2.3.4"));
    }

    [Fact]
    public void ShouldThrottle_DifferentKey_IsIndependent()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < LoginRateLimiter.MaxAttempts; i++) limiter.RecordFailure("1.2.3.4");

        Assert.False(limiter.ShouldThrottle("5.6.7.8"));
    }

    [Fact]
    public void ShouldThrottle_AfterWindowExpires_ReturnsFalse()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new LoginRateLimiter(time);
        for (var i = 0; i < LoginRateLimiter.MaxAttempts; i++) limiter.RecordFailure("1.2.3.4");
        Assert.True(limiter.ShouldThrottle("1.2.3.4"));

        time.Advance(LoginRateLimiter.Window + TimeSpan.FromSeconds(1));

        Assert.False(limiter.ShouldThrottle("1.2.3.4"));
    }

    [Fact]
    public void Sweep_RemovesBucketsWhoseWindowHasElapsed()
    {
        // Regression coverage for real, unbounded memory growth: an address that fails once and never
        // returns used to leave its bucket in the dictionary forever, since nothing else ever revisits a
        // key nobody queries again - a real concern once the web port can be reached non-loopback (e.g.
        // Docker's BindAddress=0.0.0.0 default) and an attacker can fail logins from many distinct
        // addresses.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new LoginRateLimiter(time);
        for (var i = 0; i < 10; i++) limiter.RecordFailure($"10.0.0.{i}");
        Assert.Equal(expected: 10, actual: limiter.BucketCount);

        time.Advance(LoginRateLimiter.Window + TimeSpan.FromSeconds(1));
        limiter.ForceSweep();

        Assert.Equal(expected: 0, actual: limiter.BucketCount);
    }

    [Fact]
    public void Sweep_LeavesStillActiveBucketsInPlace()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new LoginRateLimiter(time);
        limiter.RecordFailure("1.2.3.4");

        limiter.ForceSweep();

        Assert.Equal(expected: 1, actual: limiter.BucketCount);
    }

    [Fact]
    public void RecordFailure_SweepsAutomaticallyEveryNCalls()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new LoginRateLimiter(time);
        limiter.RecordFailure("stale-address");

        time.Advance(LoginRateLimiter.Window + TimeSpan.FromSeconds(1));

        // Enough distinct-key failures to cross the internal sweep threshold without ever touching
        // "stale-address" again - each of these gets its own fresh (not-yet-expired) bucket, so only the
        // automatic sweep inside RecordFailure itself, not a call on the stale key, can be what removes
        // it. 300 fresh buckets plus the one stale one is 301; if the sweep never ran, BucketCount would
        // be 301.
        for (var i = 0; i < 300; i++) limiter.RecordFailure($"fresh-{i}");

        Assert.Equal(expected: 300, actual: limiter.BucketCount);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }
}
