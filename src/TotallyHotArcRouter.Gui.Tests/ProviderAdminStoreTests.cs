using AwesomeAssertions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProviderAdminStore"/>'s reachability/error-surfacing contract. No proxy is
/// running in the test process, so every request against the loopback address below fails fast with a
/// connection refusal - exactly the "proxy isn't running" path the store is built to degrade gracefully
/// on, per its own class remarks.
/// </summary>
public sealed class ProviderAdminStoreTests
{
    // An address nothing listens on, so the underlying HttpClient.SendAsync fails fast with a
    // connection refusal rather than depending on whether an actual proxy happens to be running
    // on the store's real default port (5001) on the machine running this test.
    private const string UnreachableAddress = "http://127.0.0.1:59991";

    [Fact]
    public void Providers_and_IsLoaded_start_empty_before_any_load()
    {
        var store = new ProviderAdminStore(managementAddress: UnreachableAddress);

        store.Providers.Should().BeEmpty();
        store.IsLoaded.Should().BeFalse();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_surfaces_unreachability_instead_of_throwing()
    {
        var store = new ProviderAdminStore(managementAddress: UnreachableAddress);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().NotBeNullOrEmpty();
        store.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_raises_Changed_even_on_failure()
    {
        var store = new ProviderAdminStore(managementAddress: UnreachableAddress);
        var raised = false;
        store.Changed += () => raised = true;

        await store.LoadAsync(TestContext.Current.CancellationToken);

        raised.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertProviderAsync_propagates_a_ProviderAdminException_when_unreachable()
    {
        var store = new ProviderAdminStore(managementAddress: UnreachableAddress);
        var body = new ProviderWriteRequest(
            BaseUrl: "https://example.com", AuthHeaderName: "Authorization");

        var act = () => store.UpsertProviderAsync(key: "test", body: body);

        await act.Should().ThrowAsync<ProviderAdminException>();
    }

    [Fact]
    public void Constructor_normalizes_a_management_address_missing_a_trailing_slash()
    {
        // No exception on construction - the normalized address is only exercised on send, which the
        // other tests cover; this just guards the constructor path itself doesn't throw either way.
        var act = () => new ProviderAdminStore(managementAddress: "http://127.0.0.1:59991/already-slashed/");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task LoadRateLimitHistoryAsync_unreachable_does_not_throw()
    {
        var store = new ProviderAdminStore(managementAddress: UnreachableAddress);

        var act = () =>
            store.LoadRateLimitHistoryAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadRateLimitHistoryAsync_timeout_is_swallowed_not_propagated()
    {
        // ProviderAdminClient.SendAsync only wraps HttpRequestException into ProviderAdminException; a
        // request timeout surfaces as a raw TaskCanceledException instead. Called fire-and-forget from
        // ProvidersAdmin.razor, this method must swallow that too rather than let it become an
        // unobserved task exception.
        var store = new ProviderAdminStore(managementAddress: "http://127.0.0.1:59991",
            transport: new TimingOutHandler());

        var act = () =>
            store.LoadRateLimitHistoryAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadRateLimitHistoryAsync_ManyProvidersConcurrently_AllLandWithoutCorruption()
    {
        // ProvidersAdmin.razor fires one fire-and-forget LoadRateLimitHistoryAsync call per provider, so
        // several can complete around the same time and write into the shared cache concurrently -
        // RateLimitHistory is backed by a ConcurrentDictionary specifically so this doesn't throw or drop
        // entries.
        const string HistoryJson =
            """{"dimensions":{"tokens":[{"bucketUtc":"2026-03-01T12:00:00Z","remaining":1000,"limit":2000}]}}""";
        var store = new ProviderAdminStore(managementAddress: "http://127.0.0.1:59991",
            transport: new StubHandler(HistoryJson));
        var providerKeys = Enumerable.Range(0, 50).Select(i => $"provider-{i}").ToArray();

        await Task.WhenAll(providerKeys.Select(key =>
            store.LoadRateLimitHistoryAsync(key: key, cancellationToken: TestContext.Current.CancellationToken)));

        store.RateLimitHistory.Should().HaveCount(providerKeys.Length);
        foreach (var key in providerKeys) store.RateLimitHistory.Should().ContainKey(key);
    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new TaskCanceledException("Simulated request timeout.");
        }
    }

    private sealed class StubHandler(string jsonBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: jsonBody, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            });
        }
    }
}