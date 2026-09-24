using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProviderAdminStore"/>'s reachability/error-surfacing contract. The stub channel
/// fails every RPC as unavailable - exactly the "proxy isn't running" path the store is built to
/// degrade gracefully on, per its own class remarks.
/// </summary>
public sealed class ProviderAdminStoreTests
{
    private const string UnreachableAddress = "http://127.0.0.1:59991";

    [Fact]
    public void Providers_and_IsLoaded_start_empty_before_any_load()
    {
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        store.Providers.Should().BeEmpty();
        store.IsLoaded.Should().BeFalse();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().BeNull();
        store.ServerAddress.Should().Be(UnreachableAddress);
    }

    [Fact]
    public async Task LoadAsync_surfaces_unreachability_instead_of_throwing()
    {
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().NotBeNullOrEmpty();
        store.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_raises_Changed_even_on_failure()
    {
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var raised = false;
        store.Changed += () => raised = true;

        await store.LoadAsync(TestContext.Current.CancellationToken);

        raised.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertProviderAsync_propagates_a_GrpcAdminException_when_unreachable()
    {
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        var body = new ProviderWriteRequest(
            BaseUrl: "https://example.com");

        var act = () => store.UpsertProviderAsync(key: "test", body: body);

        await act.Should().ThrowAsync<GrpcAdminException>();
    }

    [Fact]
    public async Task UpsertProviderAsync_logs_the_base_url_without_userinfo_or_query()
    {
        var logger = new CapturingLogger();
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress),
            logger: logger);
        var body = new ProviderWriteRequest(
            BaseUrl: "https://operator:secret-token@api.example.com:8443/v1?api_key=secret-token#frag");

        var act = () => store.UpsertProviderAsync(key: "openai", body: body);

        await act.Should().ThrowAsync<GrpcAdminException>();
        var message = logger.Messages.Should()
            .ContainSingle(m => m.Contains("Updating provider", StringComparison.Ordinal)).Which;
        message.Should().Contain("openai");
        message.Should().Contain("https://api.example.com:8443/v1");
        message.Should().NotContain("secret-token");
        message.Should().NotContain("operator");
        message.Should().NotContain("api_key");
        logger.Messages.Should().NotContain(m => m.Contains("secret-token", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_buildingItsOwnChannel_DoesNotThrow()
    {
        var act = () => new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task LoadRateLimitHistoryAsync_unreachable_does_not_throw()
    {
        var store = new ProviderAdminStore(channelProvider: new StubRouterChannelProvider(UnreachableAddress));

        var act = () =>
            store.LoadRateLimitHistoryAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadRateLimitHistoryAsync_deadlineExceeded_is_swallowed_not_propagated()
    {
        // Every RpcException ProviderAdminClient's calls can raise - including a timeout's
        // DeadlineExceeded - is wrapped into GrpcAdminException, which this method catches. Called
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

    /// <summary>Records formatted log messages so a test can assert what left the store.</summary>
    private sealed class CapturingLogger : ILogger<ProviderAdminStore>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
