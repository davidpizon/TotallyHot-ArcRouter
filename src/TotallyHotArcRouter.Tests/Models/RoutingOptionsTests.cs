using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers defaults and domain validation for <see cref="RoutingOptions"/>.
/// </summary>
public class RoutingOptionsTests
{
    /// <summary>
    /// Verifies that phase-2 defaults match the expected contract values.
    /// </summary>
    [Fact]
    public void Defaults_AreExpectedForPhase2Contract()
    {
        var options = new RoutingOptions();

        Assert.Equal(expected: RouterConstants.DefaultModel, actual: options.DefaultModel);
        Assert.Equal(8, actual: options.MaxCandidates);
        Assert.Equal(10, actual: options.MaxNeighborCount);
        Assert.True(options.EnableExploration);
        Assert.Equal(0.05, actual: options.ExplorationRate, 3);
        Assert.Equal(1.0, actual: options.Epsilon1, 3);
        Assert.Equal(-0.1, actual: options.Epsilon2, 3);
        Assert.Equal(0.3, actual: options.UtilityMinQualityScore, 3);
        Assert.Equal(expected: "router_embedding_memory.db", actual: options.EmbeddingMemoryDatabasePath);
        Assert.Equal(0.5, actual: options.EmbeddingSimilarityThreshold, 3);
        Assert.Equal(20_000, actual: options.EmbeddingMemoryCapacity);
        Assert.True(options.EnableOrchestratorPolicy);
    }

    /// <summary>
    /// Verifies the Always-<i>m</i> baseline is undeclared by default and that the router's own tokens are
    /// charged at research-doc §5.1's canonical self-hosted rate.
    /// </summary>
    [Fact]
    public void Defaults_DeclareNoAlwaysBaselineAndUseTheManuscriptSelfHostedRate()
    {
        var options = new RoutingOptions();

        // Null, not a guessed model: an auto-picked baseline would manufacture a savings figure the
        // operator never chose. See AlwaysBaselineModel's remarks.
        Assert.Null(options.AlwaysBaselineModel);
        Assert.Equal(0.054m, actual: options.SelfHostedRouterPricePerMillionTokens);
    }

    /// <summary>
    /// Verifies that a present-but-blank Always-<i>m</i> baseline is rejected rather than silently read as
    /// "no baseline declared".
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenAlwaysBaselineModelIsBlank()
    {
        var options = new RoutingOptions
        {
            AlwaysBaselineModel = "   "
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>
    /// Verifies that omitting the Always-<i>m</i> baseline entirely is valid - declaring no baseline is a
    /// supported configuration, not an incomplete one.
    /// </summary>
    [Fact]
    public void EnsureValid_Succeeds_WhenAlwaysBaselineModelIsOmitted()
    {
        var options = new RoutingOptions();

        options.EnsureValid();
    }

    /// <summary>
    /// Verifies that a blank embedding memory database path is rejected.
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenEmbeddingMemoryDatabasePathIsBlank()
    {
        var options = new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = "   "
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>
    /// Verifies that unknown default models are rejected by custom validation.
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenDefaultModelIsUnknown()
    {
        var options = new RoutingOptions
        {
            DefaultModel = "unknown-model"
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>
    /// Verifies that exploration configuration is internally consistent.
    /// </summary>
    [Fact]
    public void EnsureValid_Throws_WhenExplorationDisabledButRateNonZero()
    {
        var options = new RoutingOptions
        {
            EnableExploration = false,
            ExplorationRate = 0.2
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>
    /// Verifies subagent-aware routing (issue #163) is on by default, every per-signal flag included.
    /// </summary>
    [Fact]
    public void SubagentBias_DefaultsToEnabledForEverySignal()
    {
        var bias = new RoutingOptions().SubagentBias;

        Assert.True(bias.Enabled);
        Assert.True(bias.ClaudeCodeAgentId);
        Assert.True(bias.ClaudeCodeHintHeaders);
        Assert.True(bias.CodexTurnMetadata);
        Assert.True(bias.CopilotUtilityAlias);
        Assert.Equal(0.9, actual: bias.LightSubagentRelativeFloor, 3);
    }

    /// <summary>
    /// Verifies <see cref="RoutingOptions.EnsureValid"/> rejects a light-subagent relative floor outside (0, 1], so a
    /// typo fails at startup instead of silently disabling or widening the light-subagent route.
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void EnsureValid_Throws_WhenLightSubagentRelativeFloorOutOfRange(double floor)
    {
        var options = new RoutingOptions { SubagentBias = new SubagentBiasOptions { LightSubagentRelativeFloor = floor } };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    /// <summary>Verifies the boundary value 1 (only the best known model qualifies) is accepted.</summary>
    [Fact]
    public void EnsureValid_AcceptsARelativeFloorOfOne()
    {
        var options = new RoutingOptions { SubagentBias = new SubagentBiasOptions { LightSubagentRelativeFloor = 1d } };

        options.EnsureValid();
    }

    /// <summary>
    /// Verifies <c>Routing:SubagentBias</c> binds from configuration, so an operator can flip the kill switch
    /// or drop one marker without code changes.
    /// </summary>
    [Fact]
    public void SubagentBias_BindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Routing:SubagentBias:Enabled"] = "false",
                ["Routing:SubagentBias:CodexTurnMetadata"] = "false"
            })
            .Build();

        var options = configuration.GetSection(RoutingOptions.SectionName).Get<RoutingOptions>()!;

        Assert.False(options.SubagentBias.Enabled);
        Assert.False(options.SubagentBias.CodexTurnMetadata);
        Assert.True(options.SubagentBias.ClaudeCodeAgentId);
    }
}
