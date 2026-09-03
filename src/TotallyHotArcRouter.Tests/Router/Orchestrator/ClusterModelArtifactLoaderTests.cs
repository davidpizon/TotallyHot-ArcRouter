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
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(path: _tempDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        // The honest state of a fresh install: the artifact is per-installation and never checked in.
        var path = Path.Combine(path1: _tempDirectory, path2: "absent.json");

        Assert.Null(ClusterModelArtifactLoader.TryLoad(path: path, logger: NullLogger.Instance, consumer: "test"));
    }

    [Fact]
    public void TryLoad_UnparseableFile_ReturnsNullRatherThanThrowing()
    {
        var path = Path.Combine(path1: _tempDirectory, path2: "corrupt.json");
        File.WriteAllText(path: path, contents: "{ this is not the artifact you are looking for");

        Assert.Null(ClusterModelArtifactLoader.TryLoad(path: path, logger: NullLogger.Instance, consumer: "test"));
    }

    [Fact]
    public void TryLoad_ValidArtifact_RoundTripsThroughTheSerializer()
    {
        var path = Path.Combine(path1: _tempDirectory, path2: "model.json");
        var artifact = new ClusterModelArtifact(
            2,
            Centroids: [[1f, 0f], [0f, 1f]],
            2,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [3, 4],
            ClusterDimensionHistograms: [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms: [[], []],
            TrainedFrom: "live",
            0,
            7);
        File.WriteAllText(path: path, contents: ClusterModelArtifactSerializer.Serialize(artifact));

        var loaded = ClusterModelArtifactLoader.TryLoad(path: path, logger: NullLogger.Instance, consumer: "test");

        Assert.NotNull(loaded);
        Assert.Equal(2, actual: loaded.ChosenK);
        Assert.Equal(expected: "live", actual: loaded.TrainedFrom);
        Assert.Equal(2, actual: loaded.EmbeddingDimension);
    }

    [Fact]
    public void TryLoad_BlankArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() =>
            ClusterModelArtifactLoader.TryLoad(path: " ", logger: NullLogger.Instance, consumer: "test"));
        Assert.Throws<ArgumentException>(() =>
            ClusterModelArtifactLoader.TryLoad(path: "x.json", logger: NullLogger.Instance, consumer: " "));
        Assert.Throws<ArgumentNullException>(() =>
            ClusterModelArtifactLoader.TryLoad(path: "x.json", logger: null!, consumer: "test"));
    }
}