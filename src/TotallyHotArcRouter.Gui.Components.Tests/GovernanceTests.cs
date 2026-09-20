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
/// Tests for <see cref="Governance"/>: a sub-view toggle between <see cref="ProvidersAdmin"/>,
/// <see cref="GovernanceModelCards"/>, <see cref="PriceSourcesAdmin"/>, <c>PriceOverridesAdmin</c>, and
/// <see cref="BenchmarkData"/> (Providers is the default). Each sub-view's own behavior is covered by its
/// own test file; here they're only smoke-tested via the toggle.
/// </summary>
public sealed class GovernanceTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        // Every sub-view points at an unreachable address - these tests only need each to mount.
        // ProviderAdminStore uses a hangs-forever stub client rather than a real (unreachable) connection:
        // a real loopback connection refusal can occasionally resolve fast enough on a loaded CI runner to
        // flip ProvidersAdmin out of its "Loading providers" state before the default-sub-view test's
        // assertion runs, the same "hangs forever" fix already applied to the other stores below.
        ctx.Services.AddSingleton(new ProviderAdminStore(client: new ProviderAdminClient(new HangingProviderAdminServiceClient())));
        ctx.Services.AddSingleton(new UsageStore(channelProvider: new StubRouterChannelProvider("http://127.0.0.1:59989")));
        ctx.Services.AddSingleton(new PriceSourceStore(new StubPriceSourceAdminClient()));
        ctx.Services.AddSingleton(new BenchmarkDataStore(new StubBenchmarkDataAdminClient()));
        ctx.Services.AddSingleton(new LlmRouterModelStore(new StubLlmRouterModelAdminClient()));
        return ctx;
    }

    [Fact]
    public void Defaults_to_the_providers_sub_view()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();

        // The toggle offers every sub-view, and Providers (ProvidersAdmin) is mounted first.
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should()
            .Contain(["Providers", "Models", "Price Sources", "Benchmark Data"]);
        cut.Markup.Should().Contain("Loading providers");
    }

    [Fact]
    public async Task Switching_to_the_models_sub_view_renders_GovernanceModelCards()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        // See Switching_to_the_price_sources_sub_view_renders_PriceSourcesAdmin's remarks on why this is
        // InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Models").Click());

        cut.Markup.Should().Contain("Loading");
    }

    [Fact]
    public void Budgets_sub_view_is_gone()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Budgets");
    }

    [Fact]
    public async Task Switching_to_the_price_sources_sub_view_renders_PriceSourcesAdmin()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        // InvokeAsync makes Find-then-Click atomic on the renderer's synchronization context:
        // ProviderAdminStore/UsageStore above are backed by a deliberately-unavailable stub
        // channel, whose background failure continuations can re-render
        // between a plain Find() and Click(), leaving Click() dispatching against an event handler ID the
        // re-render already invalidated (Bunit.Rendering.UnknownEventHandlerIdException).
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Price Sources").Click());

        cut.Markup.Should().Contain("Loading price sources");
    }

    [Fact]
    public async Task Switching_to_the_benchmark_data_sub_view_renders_BenchmarkData()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Governance>();
        // See Switching_to_the_price_sources_sub_view_renders_PriceSourcesAdmin's remarks on why this is
        // InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Benchmark Data").Click());

        cut.Markup.Should().Contain("Loading benchmark data status");
    }

    /// <summary>
    /// A <c>ProviderAdminService</c> test double whose <c>ListProviders</c> RPC never completes - so the
    /// Providers pane stays in its "Loading providers" state for the default-sub-view smoke test,
    /// deterministically instead of racing a real (deliberately-unreachable) connection's failure
    /// continuation. Distinct from <see cref="StubProviderAdminServiceClient"/> (used by
    /// <c>ProviderAdminStoreTests</c> for canned responses/failures), which always completes. Overrides
    /// only the <c>CallOptions</c> overload: the generated convenience overloads delegate to it.
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

    /// <summary>Hangs forever, so the panel stays in its "loading" state for the toggle smoke test.</summary>
    private sealed class StubPriceSourceAdminClient : IPriceSourceAdminClient
    {
        public Task<PriceSourceList> ListAsync(CancellationToken cancellationToken = default)
        {
            return new TaskCompletionSource<PriceSourceList>().Task;
        }

        public Task<PriceSourceList> SetEnabledAsync(string name, bool enabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PriceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PriceRefreshResult> ReorderAsync(IReadOnlyList<string> namesInPriorityOrder,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Hangs forever, so the panel stays in its "loading" state for the toggle smoke test.</summary>
    private sealed class StubBenchmarkDataAdminClient : IBenchmarkDataAdminClient
    {
        public Task<BenchmarkDataStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return new TaskCompletionSource<BenchmarkDataStatusInfo>().Task;
        }

        public Task<BenchmarkDataStatusInfo> RecheckAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<BenchmarkSyncEvent> SyncAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Hangs forever, so the Local Voter Model section stays in its "loading" state for the toggle smoke test.</summary>
    private sealed class StubLlmRouterModelAdminClient : ILlmRouterModelAdminClient
    {
        public Task<LlmRouterModelStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return new TaskCompletionSource<LlmRouterModelStatusInfo>().Task;
        }

        public Task<LlmRouterModelStatusInfo> SetBaseUrlAsync(string baseUrl,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<LlmRouterModelSyncEvent> SyncAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}