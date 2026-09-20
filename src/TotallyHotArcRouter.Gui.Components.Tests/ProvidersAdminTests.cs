using AwesomeAssertions;
using Bunit;
using Grpc.Core;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProvidersAdmin"/>'s reachability states. The unreachable cases use
/// <see cref="StubRouterChannelProvider"/> so <c>OnInitializedAsync</c>'s <c>Store.LoadAsync()</c>
/// resolves to the "unreachable" branch - <see cref="ProviderAdminStoreTests"/> covers the store's own
/// contract for that path; this only checks the component renders it correctly and can retry.
/// </summary>
public sealed class ProvidersAdminTests
{
    private static BunitContext NewUnreachableContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new ProviderAdminStore(
            channelProvider: new StubRouterChannelProvider("http://127.0.0.1:59995")));
        return ctx;
    }

    [Fact]
    public void Shows_loading_before_the_initial_load_completes()
    {
        // A hang, not an instant Unavailable: the stub channel fails fast enough that the first
        // render already shows the unreachable banner instead of "Loading providers".
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(new ProviderAdminStore(
            client: new ProviderAdminClient(new HangingProviderAdminServiceClient())));

        var cut = ctx.Render<ProvidersAdmin>();

        cut.Markup.Should().Contain("Loading providers");
    }

    [Fact]
    public void Shows_the_unreachable_state_once_the_load_fails()
    {
        using var ctx = NewUnreachableContext();

        var cut = ctx.Render<ProvidersAdmin>();

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));
        cut.Markup.Should().Contain("http://127.0.0.1:59995");
        cut.Markup.Should().NotContain(TelemetryChannelFactory.DefaultServerAddress);
    }

    [Fact]
    public void Retry_button_re_triggers_a_load()
    {
        using var ctx = NewUnreachableContext();

        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));

        var act = () => cut.Find("button").Click();
        act.Should().NotThrow();

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_store_without_throwing()
    {
        using var ctx = NewUnreachableContext();
        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));

        var act = cut.Dispose;
        act.Should().NotThrow();
    }

    /// <summary>
    /// A <c>ProviderAdminService</c> test double whose <c>ListProviders</c> RPC never completes, so the
    /// loading-state test can assert the first render without racing an instant Unavailable failure.
    /// </summary>
    private sealed class HangingProviderAdminServiceClient : Contract.ProviderAdminService.ProviderAdminServiceClient
    {
        public override AsyncUnaryCall<Contract.ProviderListResponse> ListProvidersAsync(
            Contract.ListProvidersRequest request, CallOptions options)
        {
            return new AsyncUnaryCall<Contract.ProviderListResponse>(
                responseAsync: new TaskCompletionSource<Contract.ProviderListResponse>().Task,
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}