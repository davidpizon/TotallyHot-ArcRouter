using TotallyHot.ArcRouter.Hosting.DataDirectory;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers the platform-neutral parts of ADR-0024's migration: <see cref="DataDirectoryMigrationRules"/>'s
/// per-file decisions and sibling naming, and <see cref="MacOpenWriterScanner"/>'s parsing of
/// <c>lsof</c> output.
/// </summary>
public sealed class DataDirectoryMigrationRulesTests
{
    [Theory]
    [InlineData("transcripts.db")]
    [InlineData("logs/arcrouter-20261003.log")]
    [InlineData("secrets.dat")]
    [InlineData("keys/key-1.xml")]
    public void OrdinaryFiles_AreAdopted(string relativePath)
    {
        Assert.Equal((MigrationFileDecision.Adopt, (string?)null), DataDirectoryMigrationRules.Decide(relativePath));
    }

    [Theory]
    [InlineData("appsettings.local.json")]
    [InlineData("APPSETTINGS.LOCAL.JSON")]
    public void TheOverlay_IsNeverAdopted(string relativePath)
    {
        Assert.Equal(MigrationFileDecision.Quarantine, DataDirectoryMigrationRules.Decide(relativePath).Decision);
    }

    [Fact]
    public void AnOverlayInASubdirectory_IsJustAFile()
    {
        // Only the root overlay is loaded by the router, so only it is special.
        Assert.Equal(MigrationFileDecision.Adopt, DataDirectoryMigrationRules.Decide("logs/appsettings.local.json").Decision);
    }

    [Fact]
    public void TheEmbeddingFiles_AreAdoptedOnlyAgainstTheirPinnedHashes()
    {
        Assert.Equal((MigrationFileDecision.AdoptIfHashMatches, EmbeddingOptions.DefaultModelSha256),
            DataDirectoryMigrationRules.Decide("models/bge-large-en-v1.5/model.onnx"));
        Assert.Equal((MigrationFileDecision.AdoptIfHashMatches, EmbeddingOptions.DefaultTokenizerJsonSha256),
            DataDirectoryMigrationRules.Decide(@"models\bge-large-en-v1.5\tokenizer.json"));
    }

    [Theory]
    [InlineData("models/llm_router/model.onnx")]
    [InlineData("models/bge-large-en-v1.5/model.onnx.download")]
    [InlineData("models/anything-else.bin")]
    public void OtherModelFiles_AreDiscarded(string relativePath)
    {
        Assert.Equal(MigrationFileDecision.Discard, DataDirectoryMigrationRules.Decide(relativePath).Decision);
    }

    [Fact]
    public void FindStaging_FindsOnlyThisRootsStagingSiblings()
    {
        var scratch = Path.Combine(TestScratchDirectory.RunRoot, "rules-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var root = Path.Combine(scratch, "TotallyHotArcRouter");
            var staging = DataDirectoryMigrationRules.NewStagingPath(root);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(DataDirectoryMigrationRules.NewRetiredPath(root));
            Directory.CreateDirectory(Path.Combine(scratch, "OtherApp.migrating-123"));

            Assert.Equal([staging], DataDirectoryMigrationRules.FindStaging(root));
            Assert.Single(DataDirectoryMigrationRules.FindRetired(root));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void LsofParse_ReportsWritersAndMappings_ButNotReadersOrItself()
    {
        const string output = "p100\nf4\nar\nn/data/transcripts.db\n" +
                              "p200\nf7\naw\nn/data/transcripts.db-wal\nf8\nau\nn/data/agent_telemetry.db\n" +
                              "p300\nftxt\nn/data/models/model.onnx\nfcwd\nn/data\n" +
                              "p42\nf3\naw\nn/data/own.log\n";

        var writers = MacOpenWriterScanner.Parse(output, ownPid: 42);

        Assert.Equal(3, writers.Count);
        Assert.Contains(writers, w => w.Contains("200") && w.Contains("transcripts.db-wal"));
        Assert.Contains(writers, w => w.Contains("200") && w.Contains("agent_telemetry.db"));
        Assert.Contains(writers, w => w.Contains("300") && w.Contains("mapped"));
        Assert.DoesNotContain(writers, w => w.Contains("process 100"));
        Assert.DoesNotContain(writers, w => w.Contains("own.log"));
    }
}
