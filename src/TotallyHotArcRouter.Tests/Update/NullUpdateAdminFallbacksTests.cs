using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

public sealed class NullUpdateAdminFallbacksTests
{
    [Fact]
    public async Task NullReleaseCheckClient_CheckAsync_ReturnsUnavailable()
    {
        var client = new NullReleaseCheckClient();

        var result = await client.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(ReleaseCheckUnavailableReason.NetworkOrApiFailure, result.UnavailableReason);
    }

    [Fact]
    public async Task NullUpdateApplier_ApplyAsync_ReturnsFailure()
    {
        var applier = new NullUpdateApplier();
        var update = ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc");

        var result = await applier.ApplyAsync(update, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }
}
