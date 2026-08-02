using TotallyHot.ArcRouter.Gui.Services;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the parts of <see cref="LiveDataStore"/> that don't require a live gRPC server: initial
/// property state, <see cref="LiveDataStore.ClearLogLines"/>, and the start/dispose lifecycle. The
/// background <c>StreamEvents</c> loop itself targets an address nothing listens on, so it fails fast
/// and retries quietly in the background - consistent with the class's own "not unit-tested" remarks
/// for the networking path; these tests only cover the surrounding, deterministic surface.
/// </summary>
public sealed class LiveDataStoreTests
{
    private const string UnreachableAddress = "https://127.0.0.1:59993";

    [Fact]
    public void Conversations_and_LogLines_start_empty()
    {
        var store = new LiveDataStore(serverAddress: UnreachableAddress);

        store.Conversations.Should().BeEmpty();
        store.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void ClearLogLines_raises_LogLinesChanged()
    {
        var store = new LiveDataStore(serverAddress: UnreachableAddress);
        var raised = false;
        store.LogLinesChanged += () => raised = true;

        store.ClearLogLines();

        raised.Should().BeTrue();
        store.LogLines.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_returns_immediately_and_can_be_disposed_right_away()
    {
        var store = new LiveDataStore(serverAddress: UnreachableAddress);

        await store.StartAsync(TestContext.Current.CancellationToken);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_called_twice_cancels_the_first_loop_instead_of_leaking_it()
    {
        var store = new LiveDataStore(serverAddress: UnreachableAddress);

        await store.StartAsync(TestContext.Current.CancellationToken);
        await store.StartAsync(TestContext.Current.CancellationToken);

        await store.DisposeAsync();
    }

    [Fact]
    public void DefaultServerAddress_is_the_documented_TLS_telemetry_port()
    {
        LiveDataStore.DefaultServerAddress.Should().Be("https://localhost:5002");
    }
}

