using TotallyHot.ArcRouter.Gui.Telemetry;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="PriceSourceAdminClient"/> - the wire-to-view mapping and error translation behind
/// the Governance → Price Sources panel.
/// </summary>
/// <remarks>
/// Driven through a subclassed generated stub rather than a live server: the generated client exposes a
/// protected parameterless constructor precisely for test doubles, and overriding the <c>CallOptions</c>
/// overload catches the convenience overloads too (they delegate to it). This project is plain net10.0, so
/// unlike the bUnit panel tests these run in CI.
/// </remarks>
public class PriceSourceAdminClientTests
{
    [Fact]
    public async Task ListAsync_maps_every_field_off_the_wire()
    {
        var stub = new StubClient
        {
            ListResponse = Response(Source("litellm", enabled: true, rank: 3, prices: 2505)),
        };
        using var client = new PriceSourceAdminClient(stub);

        var source = (await client.ListAsync(TestContext.Current.CancellationToken)).Sources.Single();

        source.Name.Should().Be("litellm");
        source.Enabled.Should().BeTrue();
        source.PriorityScore.Should().Be(3);
        source.PriceCount.Should().Be(2505);
    }

    [Fact]
    public async Task ListAsync_maps_a_source_that_has_never_polled()
    {
        // A seeded source owns no price rows until its first successful poll, and must still be listed - a
        // source the operator cannot see is one they cannot switch back on.
        var stub = new StubClient
        {
            ListResponse = Response(Source("litellm", enabled: true, rank: 0, prices: 0)),
        };
        using var client = new PriceSourceAdminClient(stub);

        (await client.ListAsync(TestContext.Current.CancellationToken)).Sources.Single().PriceCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_maps_the_schedule_and_derives_the_next_pull()
    {
        var anchor = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var response = Response(Source("litellm", enabled: true, rank: 0, prices: 10));
        response.Schedule = new Contract.PriceSchedule
        {
            PollIntervalSeconds = (int)TimeSpan.FromHours(6).TotalSeconds,
            ScheduleAnchorUtc = Timestamp.FromDateTimeOffset(anchor),
        };
        var stub = new StubClient { ListResponse = response };
        using var client = new PriceSourceAdminClient(stub);

        var schedule = (await client.ListAsync(TestContext.Current.CancellationToken)).Schedule;

        schedule.PollInterval.Should().Be(TimeSpan.FromHours(6));
        schedule.ScheduleAnchorUtc.Should().Be(anchor);
        // The client adds the two together; the router never ships a precomputed deadline.
        schedule.NextPullUtc.Should().Be(anchor.AddHours(6));
    }

    [Fact]
    public async Task A_response_with_no_schedule_falls_back_instead_of_throwing()
    {
        // An unset message field arrives as null - an older router, or a fake that doesn't populate it. The
        // source list is the part that matters and must still render; only the countdown is approximated.
        var stub = new StubClient
        {
            ListResponse = Response(Source("litellm", enabled: true, rank: 0, prices: 10)),
        };
        using var client = new PriceSourceAdminClient(stub);

        var list = await client.ListAsync(TestContext.Current.CancellationToken);

        list.Sources.Should().ContainSingle();
        list.Schedule.PollInterval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task RefreshAsync_carries_the_schedule_the_cycle_just_reanchored()
    {
        // The panel's countdown resets off this field rather than a follow-up List, so a pull that didn't
        // return its new anchor would leave the clock counting down to a pull that already happened.
        var anchor = DateTimeOffset.UtcNow;
        var stub = new StubClient
        {
            RefreshResponse = new Contract.RefreshPriceSourcesResponse
            {
                FreshPriceCount = 1,
                Schedule = new Contract.PriceSchedule
                {
                    PollIntervalSeconds = (int)TimeSpan.FromHours(4).TotalSeconds,
                    ScheduleAnchorUtc = Timestamp.FromDateTimeOffset(anchor),
                },
            },
        };
        using var client = new PriceSourceAdminClient(stub);

        var result = await client.RefreshAsync(TestContext.Current.CancellationToken);

        result.Schedule.PollInterval.Should().Be(TimeSpan.FromHours(4));
        result.Schedule.NextPullUtc.Should().BeCloseTo(anchor.AddHours(4), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetEnabledAsync_sends_the_name_and_flag_and_returns_the_updated_list()
    {
        var stub = new StubClient
        {
            SetEnabledResponse = Response(Source("litellm", enabled: false, rank: 0, prices: 10)),
        };
        using var client = new PriceSourceAdminClient(stub);

        var list = await client.SetEnabledAsync("litellm", enabled: false, TestContext.Current.CancellationToken);

        stub.LastSetEnabledRequest!.Name.Should().Be("litellm");
        stub.LastSetEnabledRequest!.Enabled.Should().BeFalse();
        // Mutations return the full list so the caller replaces state in one shot rather than refetching.
        list.Sources.Single().Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetEnabledAsync_rejects_a_blank_name_without_a_round_trip(string name)
    {
        var stub = new StubClient();
        using var client = new PriceSourceAdminClient(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetEnabledAsync(name, enabled: true, TestContext.Current.CancellationToken));
        stub.LastSetEnabledRequest.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_maps_outcomes_and_the_refreshed_list()
    {
        var stub = new StubClient
        {
            RefreshResponse = new Contract.RefreshPriceSourcesResponse
            {
                FreshPriceCount = 2505,
                Outcomes = { new Contract.PriceSourceOutcome { Source = "litellm", Succeeded = true, PriceCount = 2505 } },
                Sources = { Source("litellm", enabled: true, rank: 0, prices: 2505) },
            },
        };
        using var client = new PriceSourceAdminClient(stub);

        var result = await client.RefreshAsync(TestContext.Current.CancellationToken);

        result.FreshPriceCount.Should().Be(2505);
        var outcome = result.Outcomes.Single();
        outcome.Source.Should().Be("litellm");
        outcome.Succeeded.Should().BeTrue();
        outcome.PriceCount.Should().Be(2505);
        outcome.Error.Should().BeNull();
        result.Sources.Single().PriceCount.Should().Be(2505);
    }

    [Fact]
    public async Task RefreshAsync_maps_a_failed_outcome_error()
    {
        var stub = new StubClient
        {
            RefreshResponse = new Contract.RefreshPriceSourcesResponse
            {
                FreshPriceCount = 0,
                Outcomes = { new Contract.PriceSourceOutcome { Source = "litellm", Succeeded = false, PriceCount = 0, Error = "disabled during fetch" } },
            },
        };
        using var client = new PriceSourceAdminClient(stub);

        var outcome = (await client.RefreshAsync(TestContext.Current.CancellationToken)).Outcomes.Single();

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Be("disabled during fetch");
    }

    [Fact]
    public async Task Unavailable_becomes_a_plain_language_message()
    {
        // The proxy not running is an ordinary state for a GUI that outlives it - the user gets a sentence,
        // not a gRPC status dump.
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() => client.ListAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not read the price sources: the router is not reachable.");
        ex.InnerException.Should().BeOfType<RpcException>();
        // Flagged, not left for the caller to infer by parsing the message.
        ex.IsUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_rejection_is_not_flagged_unavailable()
    {
        // The router answered - it just said no. Callers key off this to decide whether the failure is about
        // the whole connection or about one request, so conflating them would let a bad argument masquerade
        // as the router being down.
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.NotFound, "No price source named 'nope' exists.")) };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() =>
            client.SetEnabledAsync("nope", enabled: true, TestContext.Current.CancellationToken));

        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_server_rejection_keeps_the_servers_own_detail()
    {
        // NotFound's "no price source named X" text is written on the server; discarding it here would leave
        // the panel unable to say what actually went wrong.
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.NotFound, "No price source named 'nope' exists.")) };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() => client.SetEnabledAsync("nope", enabled: true, TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not enable 'nope': No price source named 'nope' exists.");
    }

    [Fact]
    public async Task The_disable_failure_message_names_the_action_attempted()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Internal, "boom")) };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() => client.SetEnabledAsync("litellm", enabled: false, TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not disable 'litellm': boom");
    }

    [Fact]
    public async Task A_failed_refresh_is_wrapped_too()
    {
        var stub = new StubClient { Failure = new RpcException(new Status(StatusCode.Unavailable, "failed to connect")) };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() => client.RefreshAsync(TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not refresh the price sources: the router is not reachable.");
    }

    [Fact]
    public async Task ReorderAsync_sendsTheOrder_andMapsTheSameShapeAsRefresh()
    {
        var stub = new StubClient
        {
            ReorderResponse = new Contract.RefreshPriceSourcesResponse
            {
                FreshPriceCount = 2505,
                Outcomes = { new Contract.PriceSourceOutcome { Source = "litellm", Succeeded = true, PriceCount = 2505 } },
                Sources =
                {
                    Source("openrouter", enabled: true, rank: 1, prices: 300),
                    Source("litellm", enabled: true, rank: 0, prices: 2505),
                },
            },
        };
        using var client = new PriceSourceAdminClient(stub);

        var result = await client.ReorderAsync(["openrouter", "litellm"], TestContext.Current.CancellationToken);

        stub.LastReorderRequest!.SourceNamesInPriorityOrder.Should().Equal("openrouter", "litellm");
        result.FreshPriceCount.Should().Be(2505);
        result.Sources.Select(s => s.Name).Should().Equal("openrouter", "litellm");
    }

    [Fact]
    public async Task ReorderAsync_emptyOutcomes_mapsToAnEmptyOutcomeList()
    {
        // A real reorder recomputes from storage rather than fetching, so the server's Outcomes is always
        // empty (no fetch occurred) - the client must map that faithfully rather than choking on it.
        var stub = new StubClient
        {
            ReorderResponse = new Contract.RefreshPriceSourcesResponse
            {
                FreshPriceCount = 2505,
                Sources = { Source("litellm", enabled: true, rank: 1, prices: 2505) },
            },
        };
        using var client = new PriceSourceAdminClient(stub);

        var result = await client.ReorderAsync(["litellm", "openrouter"], TestContext.Current.CancellationToken);

        result.Outcomes.Should().BeEmpty();
        result.FreshPriceCount.Should().Be(2505);
    }

    [Fact]
    public async Task ReorderAsync_rejection_isSurfacedWithTheServersDetail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(
                StatusCode.InvalidArgument,
                "The submitted order must name every existing price source exactly once.")),
        };
        using var client = new PriceSourceAdminClient(stub);

        var ex = await Assert.ThrowsAsync<PriceSourceAdminException>(() =>
            client.ReorderAsync(["litellm"], TestContext.Current.CancellationToken));

        ex.Message.Should().Be("Could not reorder the price sources: The submitted order must name every existing price source exactly once.");
        ex.IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_client_over_a_caller_supplied_stub_does_not_dispose_the_callers_channel()
    {
        // The stub overload exists for tests and for callers who own the channel; only the address overload
        // owns what it created.
        var client = new PriceSourceAdminClient(new StubClient());

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void The_address_overload_owns_the_channel_it_creates()
    {
        // Safe to construct without a server: a gRPC channel connects lazily, on the first call. This covers
        // the constructor the GUI actually uses, and its disposal.
        var client = new PriceSourceAdminClient("https://127.0.0.1:65001");

        client.Dispose();
    }

    [Fact]
    public void The_default_address_overload_targets_the_proxys_grpc_port()
    {
        using var client = new PriceSourceAdminClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_null_stub()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PriceSourceAdminClient((Contract.PriceSourceAdminService.PriceSourceAdminServiceClient)null!));
    }

    private static Contract.ListPriceSourcesResponse Response(params Contract.PriceSource[] sources)
    {
        var response = new Contract.ListPriceSourcesResponse();
        response.Sources.AddRange(sources);
        return response;
    }

    private static Contract.PriceSource Source(string name, bool enabled, int rank, int prices) =>
        new()
        {
            Name = name,
            Enabled = enabled,
            PriorityScore = rank,
            PriceCount = prices,
        };

    /// <summary>
    /// A generated-client test double. Overrides only the <c>CallOptions</c> overloads: the generated
    /// convenience overloads delegate to them, so this intercepts both call shapes.
    /// </summary>
    private sealed class StubClient : Contract.PriceSourceAdminService.PriceSourceAdminServiceClient
    {
        public Contract.ListPriceSourcesResponse ListResponse { get; init; } = new();

        public Contract.ListPriceSourcesResponse SetEnabledResponse { get; init; } = new();

        public Contract.RefreshPriceSourcesResponse RefreshResponse { get; init; } = new();

        public Contract.RefreshPriceSourcesResponse ReorderResponse { get; init; } = new();

        public RpcException? Failure { get; init; }

        public Contract.SetPriceSourceEnabledRequest? LastSetEnabledRequest { get; private set; }

        public Contract.ReorderPriceSourcesRequest? LastReorderRequest { get; private set; }

        public override AsyncUnaryCall<Contract.ListPriceSourcesResponse> ListPriceSourcesAsync(
            Contract.ListPriceSourcesRequest request,
            CallOptions options) =>
            Call(ListResponse);

        public override AsyncUnaryCall<Contract.ListPriceSourcesResponse> SetPriceSourceEnabledAsync(
            Contract.SetPriceSourceEnabledRequest request,
            CallOptions options)
        {
            LastSetEnabledRequest = request;
            return Call(SetEnabledResponse);
        }

        public override AsyncUnaryCall<Contract.RefreshPriceSourcesResponse> RefreshPriceSourcesAsync(
            Contract.RefreshPriceSourcesRequest request,
            CallOptions options) =>
            Call(RefreshResponse);

        public override AsyncUnaryCall<Contract.RefreshPriceSourcesResponse> ReorderPriceSourcesAsync(
            Contract.ReorderPriceSourcesRequest request,
            CallOptions options)
        {
            LastReorderRequest = request;
            return Call(ReorderResponse);
        }

        private AsyncUnaryCall<T> Call<T>(T response) =>
            new(
                Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }
}

