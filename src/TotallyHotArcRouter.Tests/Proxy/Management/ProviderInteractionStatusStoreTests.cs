using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProviderInteractionStatusStore"/>: the in-memory record of each provider's most
/// recent admin-initiated interaction outcome, which <see cref="ManagementFacade"/> reads back into
/// <c>ProviderView.LastInteraction</c> so the GUI can show a persistent warning on a provider whose last
/// refresh/scan/discovery failed.
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

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when explicitly advanced, for deterministic timestamp assertions.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
