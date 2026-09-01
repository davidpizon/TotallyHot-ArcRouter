using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProviderInteractionStatusStore"/>: the in-memory record of each provider's most
/// recent admin-initiated interaction outcome (the AdminAction track, read back into
/// <c>ProviderView.AdminAction</c>) and, since
/// docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md, its most recent
/// classified live-traffic outcome (the LiveTraffic track, read back into
/// <c>ProviderView.LiveTraffic</c>) - two independently-maintained tracks so the GUI can show a
/// persistent warning on a provider whose last admin action failed, whose live traffic is currently
/// unhealthy, or both at once.
/// </summary>
public sealed class ProviderInteractionStatusStoreTests
{
    [Fact]
    public void Get_ForAProviderNeverRecorded_ReturnsNull()
    {
        var store = new ProviderInteractionStatusStore();

        Assert.Null(store.Get("openai"));
    }

    [Fact]
    public void RecordSuccess_IsReadBackAsOk()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ProviderInteractionStatusStore(clock);

        store.RecordSuccess("openai", "Refresh from endpoint");

        var status = store.Get("openai");
        Assert.NotNull(status);
        Assert.True(status!.Ok);
        Assert.Equal("Refresh from endpoint", status.Operation);
        Assert.Null(status.Message);
        Assert.Equal(clock.GetUtcNow(), status.AtUtc);
    }

    [Fact]
    public void RecordFailure_IsReadBackAsNotOk_WithTheMessage()
    {
        var store = new ProviderInteractionStatusStore();

        store.RecordFailure("openai", "Refresh from endpoint", "Provider returned 401 for https://api.openai.com/v1/models.");

        var status = store.Get("openai");
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Equal("Refresh from endpoint", status.Operation);
        Assert.Equal("Provider returned 401 for https://api.openai.com/v1/models.", status.Message);
    }

    [Fact]
    public void RecordSuccess_AfterAFailure_OverwritesIt()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure("openai", "Refresh from endpoint", "boom");

        store.RecordSuccess("openai", "Refresh from endpoint");

        Assert.True(store.Get("openai")!.Ok);
    }

    [Fact]
    public void Remove_ClearsTheRecordedStatus()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure("openai", "Refresh from endpoint", "boom");

        store.Remove("openai");

        Assert.Null(store.Get("openai"));
    }

    [Fact]
    public void ProviderKeys_AreCaseInsensitive()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure("OpenAI", "Refresh from endpoint", "boom");

        Assert.NotNull(store.Get("openai"));
    }

    [Fact]
    public void GetLiveTraffic_ForAProviderNeverRecorded_ReturnsNull()
    {
        var store = new ProviderInteractionStatusStore();

        Assert.Null(store.GetLiveTraffic("openai"));
    }

    [Fact]
    public void RecordLiveTrafficFailure_IsReadBackAsNotOk_WithTheKindAndMessage()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ProviderInteractionStatusStore(clock);

        store.RecordLiveTrafficFailure("openai", ProviderInteractionKind.OutOfCredits, "Your credit balance is too low.");

        var status = store.GetLiveTraffic("openai");
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Equal("Live traffic", status.Operation);
        Assert.Equal(ProviderInteractionKind.OutOfCredits, status.Kind);
        Assert.Equal("Your credit balance is too low.", status.Message);
        Assert.Equal(clock.GetUtcNow(), status.AtUtc);
    }

    [Fact]
    public void RecordLiveTrafficSuccess_AfterAFailure_OverwritesIt()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordLiveTrafficFailure("openai", ProviderInteractionKind.OutOfCredits, "boom");

        store.RecordLiveTrafficSuccess("openai", "Live traffic");

        var status = store.GetLiveTraffic("openai");
        Assert.NotNull(status);
        Assert.True(status!.Ok);
        Assert.Equal(ProviderInteractionKind.None, status.Kind);
    }

    [Fact]
    public void AdminActionAndLiveTraffic_AreIndependentTracks()
    {
        // The core "track separation" invariant: a write to one track must never be visible through the
        // other's reader, in either direction.
        var store = new ProviderInteractionStatusStore();

        store.RecordFailure("openai", "Refresh from endpoint", "admin failure");
        Assert.Null(store.GetLiveTraffic("openai"));

        store.RecordLiveTrafficFailure("openai", ProviderInteractionKind.OutOfCredits, "live-traffic failure");
        Assert.False(store.Get("openai")!.Ok);
        Assert.Equal("admin failure", store.Get("openai")!.Message);

        store.RecordSuccess("openai", "Refresh from endpoint");
        Assert.False(store.GetLiveTraffic("openai")!.Ok);

        store.RecordLiveTrafficSuccess("openai", "Live traffic");
        Assert.True(store.Get("openai")!.Ok);
        Assert.True(store.GetLiveTraffic("openai")!.Ok);
    }

    [Fact]
    public void Remove_ClearsBothTracks()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure("openai", "Refresh from endpoint", "admin failure");
        store.RecordLiveTrafficFailure("openai", ProviderInteractionKind.OutOfCredits, "live-traffic failure");

        store.Remove("openai");

        Assert.Null(store.Get("openai"));
        Assert.Null(store.GetLiveTraffic("openai"));
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when explicitly advanced, for deterministic timestamp assertions.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
