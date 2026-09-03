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
/// Covers <see cref="JudgeSettingsConfigureOptions"/>: the judge's two operator-facing settings come from
/// the <c>router_settings</c> table and nowhere else. <see cref="JudgeOptions.ModelName"/> follows the same
/// rule <see cref="RouterSettingsConfigureOptions"/> does - a missing row means "no override", leaving the
/// coded default untouched. <see cref="JudgeOptions.Enabled"/> deliberately does not: absent a stored row
/// its default is computed from whether a free backbone exists, since the judge is now one of the two
/// graders feeding router memory rather than an optional analysis aid.
/// </summary>
public sealed class JudgeSettingsConfigureOptionsTests
{
    [Fact]
    public void Configure_NoStoredRows_LeavesTheCodedModelNameUntouched()
    {
        var options = new JudgeOptions { ModelName = "set-by-code" };

        new JudgeSettingsConfigureOptions(store: CreateStore(), routeResolver: NoFreeModels(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.ModelName.Should().Be("set-by-code");
    }

    [Fact]
    public void Configure_NoStoredEnabledRowAndAFreeBackboneExists_TurnsTheJudgeOn()
    {
        var options = new JudgeOptions { Enabled = false };

        new JudgeSettingsConfigureOptions(store: CreateStore(), routeResolver: OneFreeModel(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Configure_NoStoredEnabledRowAndNoFreeBackbone_LeavesTheJudgeOff()
    {
        var options = new JudgeOptions { Enabled = true };

        new JudgeSettingsConfigureOptions(store: CreateStore(), routeResolver: NoFreeModels(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.Enabled.Should().BeFalse();
    }

    // The auto-detect is a default, not a gate. An operator who switched the judge off must stay switched
    // off however many free models turn up afterwards, or the toggle would silently undo itself.
    [Fact]
    public void Configure_StoredEnabledFalse_BeatsTheAutoDetectEvenWithAFreeBackbone()
    {
        var store = CreateStore();
        store.SetBool(key: RouterSettingsStore.JudgeEnabledKey, false);

        var options = new JudgeOptions { Enabled = true };
        new JudgeSettingsConfigureOptions(store: store, routeResolver: OneFreeModel(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Configure_StoredRows_OverrideTheCodedDefaults()
    {
        var store = CreateStore();
        store.SetBool(key: RouterSettingsStore.JudgeEnabledKey, true);
        store.SetString(key: RouterSettingsStore.JudgeModelNameKey, value: "free-judge");

        var options = new JudgeOptions();
        new JudgeSettingsConfigureOptions(store: store, routeResolver: NoFreeModels(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.Enabled.Should().BeTrue();
        options.ModelName.Should().Be("free-judge");
    }

    /// <summary>
    /// An explicitly stored empty model name is a real value - the operator choosing "Automatic" - and must
    /// override a non-empty coded default rather than being treated as an absent row.
    /// </summary>
    [Fact]
    public void Configure_StoredEmptyModelName_OverridesToAutomatic()
    {
        var store = CreateStore();
        store.SetString(key: RouterSettingsStore.JudgeModelNameKey, value: string.Empty);

        var options = new JudgeOptions { ModelName = "previously-chosen" };
        new JudgeSettingsConfigureOptions(store: store, routeResolver: NoFreeModels(),
            logger: NullLogger<JudgeSettingsConfigureOptions>.Instance).Configure(options);

        options.ModelName.Should().BeEmpty();
    }

    /// <summary>One free, enabled, OpenAI-shaped provider - so <c>Resolve()</c> finds a backbone.</summary>
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

    /// <summary>Only a paid provider, so <c>Resolve()</c> abstains and the judge has nothing to run on.</summary>
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