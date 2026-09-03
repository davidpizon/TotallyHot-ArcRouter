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

        store.RecordSuccess(providerKey: "openai", operation: "Refresh from endpoint");

        var status = store.Get("openai");
        Assert.NotNull(status);
        Assert.True(status!.Ok);
        Assert.Equal(expected: "Refresh from endpoint", actual: status.Operation);
        Assert.Null(status.Message);
        Assert.Equal(expected: clock.GetUtcNow(), actual: status.AtUtc);
    }

    [Fact]
    public void RecordFailure_IsReadBackAsNotOk_WithTheMessage()
    {
        var store = new ProviderInteractionStatusStore();

        store.RecordFailure(providerKey: "openai", operation: "Refresh from endpoint",
            message: "Provider returned 401 for https://api.openai.com/v1/models.");

        var status = store.Get("openai");
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Equal(expected: "Refresh from endpoint", actual: status.Operation);
        Assert.Equal(expected: "Provider returned 401 for https://api.openai.com/v1/models.", actual: status.Message);
    }

    [Fact]
    public void RecordSuccess_AfterAFailure_OverwritesIt()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure(providerKey: "openai", operation: "Refresh from endpoint", message: "boom");

        store.RecordSuccess(providerKey: "openai", operation: "Refresh from endpoint");

        Assert.True(store.Get("openai")!.Ok);
    }

    [Fact]
    public void Remove_ClearsTheRecordedStatus()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure(providerKey: "openai", operation: "Refresh from endpoint", message: "boom");

        store.Remove("openai");

        Assert.Null(store.Get("openai"));
    }

    [Fact]
    public void ProviderKeys_AreCaseInsensitive()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure(providerKey: "OpenAI", operation: "Refresh from endpoint", message: "boom");

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

        store.RecordLiveTrafficFailure(providerKey: "openai", kind: ProviderInteractionKind.OutOfCredits,
            message: "Your credit balance is too low.");

        var status = store.GetLiveTraffic("openai");
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Equal(expected: "Live traffic", actual: status.Operation);
        Assert.Equal(expected: ProviderInteractionKind.OutOfCredits, actual: status.Kind);
        Assert.Equal(expected: "Your credit balance is too low.", actual: status.Message);
        Assert.Equal(expected: clock.GetUtcNow(), actual: status.AtUtc);
    }

    [Fact]
    public void RecordLiveTrafficSuccess_AfterAFailure_OverwritesIt()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordLiveTrafficFailure(providerKey: "openai", kind: ProviderInteractionKind.OutOfCredits,
            message: "boom");

        store.RecordLiveTrafficSuccess(providerKey: "openai", operation: "Live traffic");

        var status = store.GetLiveTraffic("openai");
        Assert.NotNull(status);
        Assert.True(status!.Ok);
        Assert.Equal(expected: ProviderInteractionKind.None, actual: status.Kind);
    }

    [Fact]
    public void AdminActionAndLiveTraffic_AreIndependentTracks()
    {
        // The core "track separation" invariant: a write to one track must never be visible through the
        // other's reader, in either direction.
        var store = new ProviderInteractionStatusStore();

        store.RecordFailure(providerKey: "openai", operation: "Refresh from endpoint", message: "admin failure");
        Assert.Null(store.GetLiveTraffic("openai"));

        store.RecordLiveTrafficFailure(providerKey: "openai", kind: ProviderInteractionKind.OutOfCredits,
            message: "live-traffic failure");
        Assert.False(store.Get("openai")!.Ok);
        Assert.Equal(expected: "admin failure", actual: store.Get("openai")!.Message);

        store.RecordSuccess(providerKey: "openai", operation: "Refresh from endpoint");
        Assert.False(store.GetLiveTraffic("openai")!.Ok);

        store.RecordLiveTrafficSuccess(providerKey: "openai", operation: "Live traffic");
        Assert.True(store.Get("openai")!.Ok);
        Assert.True(store.GetLiveTraffic("openai")!.Ok);
    }

    [Fact]
    public void Remove_ClearsBothTracks()
    {
        var store = new ProviderInteractionStatusStore();
        store.RecordFailure(providerKey: "openai", operation: "Refresh from endpoint", message: "admin failure");
        store.RecordLiveTrafficFailure(providerKey: "openai", kind: ProviderInteractionKind.OutOfCredits,
            message: "live-traffic failure");

        store.Remove("openai");

        Assert.Null(store.Get("openai"));
        Assert.Null(store.GetLiveTraffic("openai"));
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock only moves when explicitly advanced, for deterministic timestamp
    /// assertions.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}