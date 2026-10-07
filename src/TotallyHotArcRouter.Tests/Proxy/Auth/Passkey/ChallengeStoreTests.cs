using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

public sealed class ChallengeStoreTests
{
    [Fact]
    public void TryConsume_RemovesChallenge_SecondConsumeFails()
    {
        var store = new ChallengeStore(new PasskeyOptions { ChallengeIssuanceBurst = 100 });
        var challenge = store.Issue();

        Assert.True(store.TryConsume(challenge, out var pending));
        Assert.NotNull(pending);
        Assert.False(store.TryConsume(challenge, out _));
    }

    [Fact]
    public void Issue_EvictsOldestWhenThirtyThirdPending()
    {
        var options = new PasskeyOptions { ChallengeIssuancePerMinute = 10_000, ChallengeIssuanceBurst = 10_000 };
        var store = new ChallengeStore(options);
        var first = store.Issue();
        for (var i = 0; i < ChallengeStore.MaxPending; i++) store.Issue();

        Assert.False(store.TryConsume(first, out _));
    }

    [Fact]
    public void Issue_WhenBucketEmpty_ThrowsResourceExhausted()
    {
        var options = new PasskeyOptions { ChallengeIssuancePerMinute = 1, ChallengeIssuanceBurst = 1 };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ChallengeStore(options, clock);
        store.Issue();

        var ex = Assert.Throws<PasskeyGateException>(() => store.Issue());
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }
}
