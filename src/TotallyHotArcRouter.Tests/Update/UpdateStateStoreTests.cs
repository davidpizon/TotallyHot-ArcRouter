using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

public sealed class UpdateStateStoreTests
{
    [Fact]
    public void Current_BeforeAnyRecord_HasNullResultAndTimestamp()
    {
        var store = new UpdateStateStore();

        Assert.Null(store.Current.Result);
        Assert.Null(store.Current.CheckedAtUtc);
    }

    [Fact]
    public void Record_StoresResultAndStampsTimestamp()
    {
        var store = new UpdateStateStore();
        var result = ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "2.0.0", true,
            assetDownloadUrl: "https://example.test/a.zip", assetSha256: "abc");

        var before = DateTimeOffset.UtcNow;
        store.Record(result);
        var after = DateTimeOffset.UtcNow;

        Assert.Same(expected: result, actual: store.Current.Result);
        Assert.InRange(actual: store.Current.CheckedAtUtc!.Value, low: before.AddSeconds(-1),
            high: after.AddSeconds(1));
    }

    [Fact]
    public void Record_NullResult_Throws()
    {
        var store = new UpdateStateStore();

        Assert.Throws<ArgumentNullException>(() => store.Record(null!));
    }

    [Fact]
    public void Record_SecondCall_ReplacesFirst()
    {
        var store = new UpdateStateStore();
        store.Record(ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "1.0.0", false, null, null));
        var second = ReleaseCheckResult.Resolved(currentVersion: "1.0.0", latestVersion: "2.0.0", true,
            assetDownloadUrl: "https://example.test/a.zip", assetSha256: "abc");

        store.Record(second);

        Assert.Same(expected: second, actual: store.Current.Result);
    }
}