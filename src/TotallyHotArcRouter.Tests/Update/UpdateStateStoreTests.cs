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
        var result = ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc");

        var before = DateTimeOffset.UtcNow;
        store.Record(result);
        var after = DateTimeOffset.UtcNow;

        Assert.Same(result, store.Current.Result);
        Assert.InRange(store.Current.CheckedAtUtc!.Value, before.AddSeconds(-1), after.AddSeconds(1));
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
        store.Record(ReleaseCheckResult.Resolved("1.0.0", "1.0.0", false, null, null));
        var second = ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, "https://example.test/a.zip", "abc");

        store.Record(second);

        Assert.Same(second, store.Current.Result);
    }
}
