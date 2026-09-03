using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="RoutingModeAdmin"/>: the Governance tab's read-only Routing Mode panel. Driven
/// through a fake <see cref="IRoutingModeAdminClient"/> so nothing here needs a live proxy or a gRPC
/// channel.
/// </summary>
public sealed class RoutingModeAdminTests
{
    private static BunitContext NewContext(IRoutingModeAdminClient client)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new RoutingModeStore(client));
        return ctx;
    }

    private static RoutingMode DefaultMode(bool orchestratorEnabled = true, bool explorationEnabled = true)
    {
        return new RoutingMode(
            OrchestratorEnabled: orchestratorEnabled,
            ExplorationEnabled: explorationEnabled,
            0.05,
            Voters:
            [
                new VoterMode(Name: "dim_best", true, 0.9),
                new VoterMode(Name: "memory_kNN", true, 0.57),
                new VoterMode(Name: "logreg", true, 0.43),
                new VoterMode(Name: "llm_router", false, 0.64),
                // The fifth voter, added by self-organizing-classification-plan.md Phase T3. Present in this
                // fixture because the component under test renders whatever the service sends, and the
                // service sends five - a four-voter fixture under a test named "every voter" is the same
                // drift that let RoutingModeAdminGrpcService omit this voter in the first place.
                new VoterMode(Name: "cluster_best", true, 0.5)
            ]);
    }

    [Fact]
    public void Renders_the_orchestrator_state_and_every_voter()
    {
        using var ctx = NewContext(new FakeClient(DefaultMode()));

        var cut = ctx.Render<RoutingModeAdmin>();

        cut.Markup.Should().Contain("Orchestrator Live");
        cut.Markup.Should().Contain("dim_best");
        cut.Markup.Should().Contain("memory_kNN");
        cut.Markup.Should().Contain("logreg");
        cut.Markup.Should().Contain("llm_router");
        cut.Markup.Should().Contain("cluster_best");
        cut.Markup.Should().Contain("weight 0.90");
        cut.Markup.Should().Contain("disabled");
    }

    [Fact]
    public void Reports_the_orchestrator_disabled_when_the_kill_switch_is_off()
    {
        using var ctx = NewContext(new FakeClient(DefaultMode(orchestratorEnabled: false)));

        var cut = ctx.Render<RoutingModeAdmin>();

        cut.Markup.Should().Contain("Orchestrator Disabled");
        cut.Markup.Should().Contain("memory-only ranking");
    }

    [Fact]
    public void Shows_the_exploration_rate_only_when_exploration_is_enabled()
    {
        using var ctx = NewContext(new FakeClient(DefaultMode(explorationEnabled: true)));

        var cut = ctx.Render<RoutingModeAdmin>();

        cut.Markup.Should().Contain("Exploration On");
        cut.Markup.Should().Contain("5% of decisions");
    }

    [Fact]
    public void Omits_the_exploration_rate_when_exploration_is_disabled()
    {
        using var ctx = NewContext(new FakeClient(DefaultMode(explorationEnabled: false)));

        var cut = ctx.Render<RoutingModeAdmin>();

        cut.Markup.Should().Contain("Exploration Off");
        cut.Markup.Should().NotContain("of decisions");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(new FakeClient
            { Error = new RoutingModeAdminException(message: "nope", isUnavailable: true) });

        var cut = ctx.Render<RoutingModeAdmin>();

        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Retry_reloads_after_the_router_becomes_reachable()
    {
        var client = new FakeClient { Error = new RoutingModeAdminException(message: "nope", isUnavailable: true) };
        using var ctx = NewContext(client);
        var cut = ctx.Render<RoutingModeAdmin>();
        cut.Markup.Should().Contain("Router unreachable");

        client.Error = null;
        client.Mode = DefaultMode();
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Orchestrator Live");
    }

    private sealed class FakeClient(RoutingMode? mode = null) : IRoutingModeAdminClient
    {
        public RoutingMode? Mode { get; set; } = mode;

        public RoutingModeAdminException? Error { get; set; }

        public Task<RoutingMode> GetAsync(CancellationToken cancellationToken = default)
        {
            return Error is not null
                ? Task.FromException<RoutingMode>(Error)
                : Task.FromResult(Mode!);
        }
    }
}