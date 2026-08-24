using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeSettingsConfigureOptions"/>: the judge's two operator-facing settings come from
/// the <c>router_settings</c> table and nowhere else. The rule that matters is the same one
/// <see cref="RouterSettingsConfigureOptions"/> follows - a missing row means "no override", leaving the
/// coded default untouched rather than re-asserting it.
/// </summary>
public sealed class JudgeSettingsConfigureOptionsTests
{
    [Fact]
    public void Configure_NoStoredRows_LeavesTheCodedDefaultsUntouched()
    {
        var options = new JudgeOptions { Enabled = true, ModelName = "set-by-code" };

        new JudgeSettingsConfigureOptions(CreateStore()).Configure(options);

        options.Enabled.Should().BeTrue();
        options.ModelName.Should().Be("set-by-code");
    }

    [Fact]
    public void Configure_StoredRows_OverrideTheCodedDefaults()
    {
        var store = CreateStore();
        store.SetBool(RouterSettingsStore.JudgeEnabledKey, true);
        store.SetString(RouterSettingsStore.JudgeModelNameKey, "free-judge");

        var options = new JudgeOptions();
        new JudgeSettingsConfigureOptions(store).Configure(options);

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
        store.SetString(RouterSettingsStore.JudgeModelNameKey, string.Empty);

        var options = new JudgeOptions { ModelName = "previously-chosen" };
        new JudgeSettingsConfigureOptions(store).Configure(options);

        options.ModelName.Should().BeEmpty();
    }

    private static RouterSettingsStore CreateStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "router_embedding_memory.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        return new RouterSettingsStore(database, NullLogger<RouterSettingsStore>.Instance);
    }
}
