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
