using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterModelArtifactLoader"/> - the artifact read shared by the <c>cluster_best</c>
/// voter (Phase T3) and the taxonomy comparison job (Phase T4). Both degrade rather than throw when no
/// usable model exists, and this is the single place that decides what "usable" means.
/// </summary>
public sealed class ClusterModelArtifactLoaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public ClusterModelArtifactLoaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        // The honest state of a fresh install: the artifact is per-installation and never checked in.
        var path = Path.Combine(_tempDirectory, "absent.json");

        Assert.Null(ClusterModelArtifactLoader.TryLoad(path, NullLogger.Instance, "test"));
    }

    [Fact]
    public void TryLoad_UnparseableFile_ReturnsNullRatherThanThrowing()
    {
        var path = Path.Combine(_tempDirectory, "corrupt.json");
        File.WriteAllText(path, "{ this is not the artifact you are looking for");

        Assert.Null(ClusterModelArtifactLoader.TryLoad(path, NullLogger.Instance, "test"));
    }

    [Fact]
    public void TryLoad_ValidArtifact_RoundTripsThroughTheSerializer()
    {
        var path = Path.Combine(_tempDirectory, "model.json");
        var artifact = new ClusterModelArtifact(
            EmbeddingDimension: 2,
            Centroids: [[1f, 0f], [0f, 1f]],
            ChosenK: 2,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [3, 4],
            ClusterDimensionHistograms: [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms: [[], []],
            TrainedFrom: "live",
            BootstrapTaskCount: 0,
            MemoryEntryCount: 7);
        File.WriteAllText(path, ClusterModelArtifactSerializer.Serialize(artifact));

        var loaded = ClusterModelArtifactLoader.TryLoad(path, NullLogger.Instance, "test");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.ChosenK);
        Assert.Equal("live", loaded.TrainedFrom);
        Assert.Equal(2, loaded.EmbeddingDimension);
    }

    [Fact]
    public void TryLoad_BlankArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => ClusterModelArtifactLoader.TryLoad(" ", NullLogger.Instance, "test"));
        Assert.Throws<ArgumentException>(() => ClusterModelArtifactLoader.TryLoad("x.json", NullLogger.Instance, " "));
        Assert.Throws<ArgumentNullException>(() => ClusterModelArtifactLoader.TryLoad("x.json", null!, "test"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }
}
