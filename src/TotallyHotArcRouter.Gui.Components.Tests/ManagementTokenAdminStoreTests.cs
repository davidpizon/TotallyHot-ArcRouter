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

        await store.LoadAsync("authz", TestContext.Current.CancellationToken);

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

        await store.LoadAsync("authz", TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.Token.Should().BeNull();
    }

    [Fact]
    public async Task RegenerateAsync_Success_PublishesTheNewToken()
    {
        var client = new FakeManagementTokenAdminClient { TokenResult = "abc123", RegenerateResult = "xyz789" };
        var store = new ManagementTokenAdminStore(client);
        await store.LoadAsync("authz", TestContext.Current.CancellationToken);

        await store.RegenerateAsync("authz", TestContext.Current.CancellationToken);

        store.Token.Should().Be("xyz789");
        store.IsReachable.Should().BeTrue();
    }

    [Fact]
    public async Task RegenerateAsync_Rejected_RethrowsAndRecordsTheFailure()
    {
        var client = new FakeManagementTokenAdminClient
        { RegenerateFailure = new GrpcAdminException(message: "regenerate blew up", isUnavailable: false) };
        var store = new ManagementTokenAdminStore(client);

        var act = () => store.RegenerateAsync("authz", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GrpcAdminException>();
        store.LastError.Should().Be("regenerate blew up");
    }

    [Fact]
    public async Task LoadAsync_SendsTheOneOperationAuthorizationToTheClient()
    {
        var client = new FakeManagementTokenAdminClient { TokenResult = "abc123" };
        var store = new ManagementTokenAdminStore(client);

        await store.LoadAsync("authz-get", TestContext.Current.CancellationToken);

        client.LastAuthorizationToken.Should().Be("authz-get");
    }

    [Fact]
    public async Task RegenerateAsync_SendsTheOneOperationAuthorizationToTheClient()
    {
        var client = new FakeManagementTokenAdminClient { RegenerateResult = "xyz789" };
        var store = new ManagementTokenAdminStore(client);

        await store.RegenerateAsync("authz-regen", TestContext.Current.CancellationToken);

        client.LastAuthorizationToken.Should().Be("authz-regen");
    }

    [Fact]
    public async Task LoadAsync_RefusedByTheRouter_ReturnsFalseAndLeavesNoToken()
    {
        var client = new FakeManagementTokenAdminClient
        { GetFailure = new GrpcAdminException(message: "Passkey verification is required for this operation.") };
        var store = new ManagementTokenAdminStore(client);

        var loaded = await store.LoadAsync("spent", TestContext.Current.CancellationToken);

        loaded.Should().BeFalse();
        store.Token.Should().BeNull();
        store.IsReachable.Should().BeTrue("a rejection reached the router");
        store.LastError.Should().Be("Passkey verification is required for this operation.");
    }

    [Fact]
    public async Task ClearToken_DropsTheHeldTokenAndNotifies()
    {
        var client = new FakeManagementTokenAdminClient { TokenResult = "abc123" };
        var store = new ManagementTokenAdminStore(client);
        await store.LoadAsync("authz", TestContext.Current.CancellationToken);
        var changed = 0;
        store.Changed += () => changed++;

        store.ClearToken();
        store.ClearToken();

        store.Token.Should().BeNull();
        changed.Should().Be(1, "a second clear with nothing held changes nothing");
    }

    [Fact]
    public void LoadAsync_BlankAuthorization_Throws()
    {
        var store = new ManagementTokenAdminStore(new FakeManagementTokenAdminClient());

        var act = () =>
        {
            _ = store.LoadAsync(string.Empty, TestContext.Current.CancellationToken);
        };

        act.Should().Throw<ArgumentException>();
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
        public Exception? GetFailure { get; init; }
        public Exception? RegenerateFailure { get; init; }
        public string RegenerateResult { get; init; } = string.Empty;
        public string TokenResult { get; init; } = string.Empty;

        public void Dispose()
        {
            Disposed = true;
        }

        public string? LastAuthorizationToken { get; private set; }

        public Task<string> GetTokenAsync(string authorizationToken, CancellationToken cancellationToken = default)
        {
            LastAuthorizationToken = authorizationToken;
            return GetFailure is not null ? Task.FromException<string>(GetFailure) : Task.FromResult(TokenResult);
        }

        public Task<string> RegenerateAsync(string authorizationToken, CancellationToken cancellationToken = default)
        {
            LastAuthorizationToken = authorizationToken;
            return RegenerateFailure is not null
                ? Task.FromException<string>(RegenerateFailure)
                : Task.FromResult(RegenerateResult);
        }
    }
}
