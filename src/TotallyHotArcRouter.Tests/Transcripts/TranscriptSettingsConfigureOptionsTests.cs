using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TranscriptSettingsConfigureOptions"/>'s precedence contract, mirroring
/// <see cref="TotallyHot.ArcRouter.Tests.Router.RouterSettingsConfigureOptionsTests"/> for <see cref="RoutingOptions"/>: a stored override
/// beats whatever <c>appsettings.json</c>/the coded default already produced, and an absent stored value
/// leaves that prior value untouched rather than re-asserting the coded default a second time.
/// </summary>
public sealed class TranscriptSettingsConfigureOptionsTests
{
    [Fact]
    public void Configure_NoStoredOverride_LeavesOptionsUntouched()
    {
        var store = CreateStore();
        var configure = new TranscriptSettingsConfigureOptions(store);
        var options = new TranscriptOptions { Enabled = false };

        configure.Configure(options);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Configure_StoredEnabledOverride_BeatsWhateverWasAlreadyBound()
    {
        var store = CreateStore();
        store.SetBool(key: RouterSettingsStore.TranscriptCaptureEnabledKey, false);
        var configure = new TranscriptSettingsConfigureOptions(store);
        // Simulates the appsettings.json-bound value the preceding Configure<IConfiguration> step already
        // produced - true, the coded default - which this step must overwrite.
        var options = new TranscriptOptions { Enabled = true };

        configure.Configure(options);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStore()
    {
        Assert.Throws<ArgumentNullException>(() => new TranscriptSettingsConfigureOptions(null!));
    }

    [Fact]
    public void Configure_ThrowsOnNullOptions()
    {
        var configure = new TranscriptSettingsConfigureOptions(CreateStore());

        Assert.Throws<ArgumentNullException>(() => configure.Configure(null!));
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