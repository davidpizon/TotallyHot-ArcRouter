using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RoutingModeAdminGrpcService.GetRoutingMode"/>: it must report <em>every</em> voter
/// with its configured enablement/weight, and the orchestrator/exploration settings currently bound into
/// <see cref="RoutingOptions"/>. An earlier revision of this suite asserted a four-name sequence, which
/// is what let the service keep omitting <c>cluster_best</c> after Phase T3 added it - the sequence
/// assertion below is deliberately exhaustive so a future voter cannot be dropped silently.
/// </summary>
public sealed class RoutingModeAdminGrpcServiceTests
{
    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetRoutingMode_ReportsEveryVoterInFixedOrderWithTheirConfiguredWeights()
    {
        var options = new RoutingOptions
        {
            EnableOrchestratorPolicy = true,
            EnableExploration = true,
            ExplorationRate = 0.05,
            EnableDimBestVoter = true,
            DimBestVoterWeight = 0.9,
            EnableMemoryKnnVoter = true,
            MemoryKnnVoterWeight = 0.57,
            EnableLogRegVoter = false,
            LogRegVoterWeight = 0.43,
            EnableLlmRouterVoter = true,
            LlmRouterVoterWeight = 0.64,
            EnableClusterBestVoter = true,
            ClusterBestVoterWeight = 0.5
        };
        var service = new RoutingModeAdminGrpcService(Options.Create(options));

        var response =
            await service.GetRoutingMode(request: new Contract.GetRoutingModeRequest(), context: CreateContext());

        response.OrchestratorEnabled.Should().Be(true);
        response.ExplorationEnabled.Should().Be(true);
        response.ExplorationRate.Should().Be(0.05);
        response.Voters.Select(v => v.Name).Should()
            .Equal(VoterNames.DimBest, VoterNames.MemoryKnn, VoterNames.LogReg, VoterNames.LlmRouter,
                VoterNames.ClusterBest);
        response.Voters[0].Enabled.Should().BeTrue();
        response.Voters[0].Weight.Should().Be(0.9);
        response.Voters[2].Enabled.Should().BeFalse();
        response.Voters[2].Weight.Should().Be(0.43);
        response.Voters[4].Enabled.Should().BeTrue();
        response.Voters[4].Weight.Should().Be(0.5);
    }

    [Fact]
    public async Task GetRoutingMode_ReportsTheOrchestratorKillSwitchWhenDisabled()
    {
        var options = new RoutingOptions
            { EnableOrchestratorPolicy = false, EnableExploration = false, ExplorationRate = 0 };
        var service = new RoutingModeAdminGrpcService(Options.Create(options));

        var response =
            await service.GetRoutingMode(request: new Contract.GetRoutingModeRequest(), context: CreateContext());

        response.OrchestratorEnabled.Should().BeFalse();
        response.ExplorationEnabled.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var act = () => new RoutingModeAdminGrpcService(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}