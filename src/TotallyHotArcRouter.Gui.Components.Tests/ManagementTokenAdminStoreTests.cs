using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ManagementTokenAdminStore"/>: the load round trip, the unreachable-load state, a
/// successful regenerate publishing the new token, and a rejected regenerate rethrowing while still
/// recording the failure (web GUI migration plan Phase P9).
/// </summary>
public sealed class ManagementTokenAdminStoreTests
{
    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new ManagementTokenAdminStore((IManagementTokenAdminClient)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task LoadAsync_Success_PopulatesToken()
    {
        var client = new FakeManagementTokenAdminClient { TokenResult = "abc123" };
        var store = new ManagementTokenAdminStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        store.Token.Should().Be("abc123");
    }

    [Fact]
    public async Task LoadAsync_ClientThrows_SetsUnreachableWithoutThrowing()
    {
        var client = new FakeManagementTokenAdminClient
        { GetFailure = new GrpcAdminException(message: "router is gone", isUnavailable: true) };
        var store = new ManagementTokenAdminStore(client);

        await store.LoadAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.Token.Should().BeNull();
    }

    [Fact]
    public async Task RegenerateAsync_Success_PublishesTheNewToken()
    {
        var client = new FakeManagementTokenAdminClient { TokenResult = "abc123", RegenerateResult = "xyz789" };
        var store = new ManagementTokenAdminStore(client);
        await store.LoadAsync(TestContext.Current.CancellationToken);

        await store.RegenerateAsync(TestContext.Current.CancellationToken);

        store.Token.Should().Be("xyz789");
        store.IsReachable.Should().BeTrue();
    }

    [Fact]
    public async Task RegenerateAsync_Rejected_RethrowsAndRecordsTheFailure()
    {
        var client = new FakeManagementTokenAdminClient
        { RegenerateFailure = new GrpcAdminException(message: "regenerate blew up", isUnavailable: false) };
        var store = new ManagementTokenAdminStore(client);

        var act = () => store.RegenerateAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GrpcAdminException>();
        store.LastError.Should().Be("regenerate blew up");
    }

    [Fact]
    public void Dispose_OverCallerSuppliedClient_DoesNotDisposeTheClient()
    {
        var client = new FakeManagementTokenAdminClient();
        var store = new ManagementTokenAdminStore(client);

        store.Dispose();

        client.Disposed.Should().BeFalse();
    }

    private sealed class FakeManagementTokenAdminClient : IManagementTokenAdminClient, IDisposable
    {
        public bool Disposed { get; private set; }
        public Exception? GetFailure { get; set; }
        public Exception? RegenerateFailure { get; set; }
        public string RegenerateResult { get; set; } = string.Empty;
        public string TokenResult { get; set; } = string.Empty;

        public void Dispose()
        {
            Disposed = true;
        }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return GetFailure is not null ? Task.FromException<string>(GetFailure) : Task.FromResult(TokenResult);
        }

        public Task<string> RegenerateAsync(CancellationToken cancellationToken = default)
        {
            return RegenerateFailure is not null
                ? Task.FromException<string>(RegenerateFailure)
                : Task.FromResult(RegenerateResult);
        }
    }
}
