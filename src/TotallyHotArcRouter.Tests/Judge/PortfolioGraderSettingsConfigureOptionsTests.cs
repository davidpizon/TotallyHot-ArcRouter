using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.Proxy;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PortfolioGraderSettingsConfigureOptions"/>: each of the three Q3 grader flags is
/// independently overridable from <c>router_settings</c>, and each falls back to the same
/// "on when a free backbone exists" computed default the judge's own <see cref="JudgeOptions.Enabled"/> uses.
/// </summary>
public sealed class PortfolioGraderSettingsConfigureOptionsTests
{
    [Fact]
    public void Configure_NoStoredRowsAndAFreeBackboneExists_TurnsAllThreeOn()
    {
        var options = new PortfolioGraderOptions();

        new PortfolioGraderSettingsConfigureOptions(store: CreateStore(), routeResolver: OneFreeModel(),
            logger: NullLogger<PortfolioGraderSettingsConfigureOptions>.Instance).Configure(options);

        options.CodeJudgeEnabled.Should().BeTrue();
        options.IceScoreEnabled.Should().BeTrue();
        options.RaceEnabled.Should().BeTrue();
    }

    [Fact]
    public void Configure_NoStoredRowsAndNoFreeBackbone_LeavesAllThreeOff()
    {
        var options = new PortfolioGraderOptions();

        new PortfolioGraderSettingsConfigureOptions(store: CreateStore(), routeResolver: NoFreeModels(),
            logger: NullLogger<PortfolioGraderSettingsConfigureOptions>.Instance).Configure(options);

        options.CodeJudgeEnabled.Should().BeFalse();
        options.IceScoreEnabled.Should().BeFalse();
        options.RaceEnabled.Should().BeFalse();
    }

    [Fact]
    public void Configure_OneFlagStoredTrue_OverridesOnlyThatFlag()
    {
        var store = CreateStore();
        store.SetBool(key: RouterSettingsStore.CodeJudgeEnabledKey, true);

        var options = new PortfolioGraderOptions();
        new PortfolioGraderSettingsConfigureOptions(store: store, routeResolver: NoFreeModels(),
            logger: NullLogger<PortfolioGraderSettingsConfigureOptions>.Instance).Configure(options);

        options.CodeJudgeEnabled.Should().BeTrue();
        options.IceScoreEnabled.Should().BeFalse();
        options.RaceEnabled.Should().BeFalse();
    }

    // An operator who explicitly switched a grader off must stay off however many free models appear
    // later - the same "default, not a gate" guarantee JudgeOptions.Enabled makes.
    [Fact]
    public void Configure_StoredFalse_BeatsTheAutoDetectEvenWithAFreeBackbone()
    {
        var store = CreateStore();
        store.SetBool(key: RouterSettingsStore.RaceEnabledKey, false);

        var options = new PortfolioGraderOptions();
        new PortfolioGraderSettingsConfigureOptions(store: store, routeResolver: OneFreeModel(),
            logger: NullLogger<PortfolioGraderSettingsConfigureOptions>.Instance).Configure(options);

        options.RaceEnabled.Should().BeFalse();
        options.CodeJudgeEnabled.Should().BeTrue();
        options.IceScoreEnabled.Should().BeTrue();
    }

    private static IModelRouteResolver OneFreeModel()
    {
        return Resolver(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["lmstudio"] = new() { BaseUrl = "http://localhost:1234/v1", IsFree = true }
            },
            ModelList = [new ModelRouteEntry { ModelName = "free-a", Provider = "lmstudio", ProviderModelId = "a" }]
        });
    }

    private static IModelRouteResolver NoFreeModels()
    {
        return Resolver(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["paid"] = new() { BaseUrl = "https://api.example.com/v1", IsFree = false }
            },
            ModelList = [new ModelRouteEntry { ModelName = "paid-a", Provider = "paid", ProviderModelId = "a" }]
        });
    }

    private static IModelRouteResolver Resolver(ModelRoutingOptions options)
    {
        return new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(path1: tempDirectory, path2: "router_embedding_memory.db");
        var database =
            new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database: database, logger: NullLogger<RouterSettingsStore>.Instance);
    }
}
