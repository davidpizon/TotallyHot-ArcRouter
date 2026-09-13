using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

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
    public void Constructor_buildingItsOwnChannel_DoesNotThrow()
    {
        // GrpcChannel.ForAddress validates and resolves the address eagerly enough that a malformed one
        // would throw here rather than only on first send - this guards the constructor path itself.
        var act = () => new ProviderAdminStore(managementAddress: UnreachableAddress);
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
    public async Task LoadRateLimitHistoryAsync_deadlineExceeded_is_swallowed_not_propagated()
    {
        // Every RpcException ProviderAdminClient's calls can raise - including a timeout's
        // DeadlineExceeded - is wrapped into ProviderAdminException, which this method catches. Called
        // fire-and-forget from ProvidersAdmin.razor, it must never let a failure become an unobserved task
        // exception.
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(statusCode: StatusCode.DeadlineExceeded, detail: "timed out"))
        };
        var store = new ProviderAdminStore(client: new ProviderAdminClient(stub));

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
        var response = new Contract.RateLimitHistoryResponse();
        var series = new Contract.RateLimitHistorySeries();
        series.Points.Add(new Contract.RateLimitHistoryPoint
        {
            BucketUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T12:00:00Z")), Remaining = 1000,
            Limit = 2000
        });
        response.Dimensions["tokens"] = series;
        var stub = new StubClient { RateLimitHistoryResponse = response };
        var store = new ProviderAdminStore(client: new ProviderAdminClient(stub));
        var providerKeys = Enumerable.Range(0, 50).Select(i => $"provider-{i}").ToArray();

        await Task.WhenAll(providerKeys.Select(key =>
            store.LoadRateLimitHistoryAsync(key: key, cancellationToken: TestContext.Current.CancellationToken)));

        store.RateLimitHistory.Should().HaveCount(providerKeys.Length);
        foreach (var key in providerKeys) store.RateLimitHistory.Should().ContainKey(key);
    }

    /// <summary>
    /// A generated-client test double covering only <see cref="GetRateLimitHistoryAsync"/>, which is all
    /// this file's stubbed tests need. Overrides only the <c>CallOptions</c> overload: the generated
    /// convenience overloads delegate to it.
    /// </summary>
    private sealed class StubClient : Contract.ProviderAdminService.ProviderAdminServiceClient
    {
        public Contract.RateLimitHistoryResponse RateLimitHistoryResponse { get; init; } = new();

        public RpcException? Failure { get; init; }

        public override AsyncUnaryCall<Contract.RateLimitHistoryResponse> GetRateLimitHistoryAsync(
            Contract.GetRateLimitHistoryRequest request, CallOptions options)
        {
            return new AsyncUnaryCall<Contract.RateLimitHistoryResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(RateLimitHistoryResponse)
                    : Task.FromException<Contract.RateLimitHistoryResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}