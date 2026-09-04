using AwesomeAssertions;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Tests for <see cref="RoutingGateStore"/>: default-enabled fallback, round-tripping through the file, and
/// tolerance of a missing/corrupt state file. Mirrors <c>GuiSettingsStoreTests</c>' file-store coverage
/// shape.
/// </summary>
public sealed class RoutingGateStoreTests
{
    private static string TempPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid() + ".json");
    }

    [Fact]
    public void IsEnabled_WithNoFile_DefaultsToTrue()
    {
        var store = new RoutingGateStore(TempPath());

        store.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void SetEnabled_PersistsAcrossInstances()
    {
        var path = TempPath();
        try
        {
            var store = new RoutingGateStore(path);

            store.SetEnabled(false);

            new RoutingGateStore(path).IsEnabled.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SetEnabled_ThenSetEnabledTrue_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var store = new RoutingGateStore(path);

            store.SetEnabled(false);
            store.SetEnabled(true);

            new RoutingGateStore(path).IsEnabled.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsEnabled_WithCorruptFile_DefaultsToTrue()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: "not json");
            var store = new RoutingGateStore(path);

            store.IsEnabled.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SetEnabled_CreatesTheDirectoryIfMissing()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid().ToString());
        var path = Path.Combine(path1: directory, path2: "routing-gate.json");
        try
        {
            var store = new RoutingGateStore(path);

            store.SetEnabled(false);

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(path: directory, true);
        }
    }
}