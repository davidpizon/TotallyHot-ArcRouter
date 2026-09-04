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
        Assert.Equal(expected: ReleaseCheckUnavailableReason.NetworkOrApiFailure, actual: result.UnavailableReason);
    }
}