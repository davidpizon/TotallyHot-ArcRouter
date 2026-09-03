using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="GuiSettingsStore"/>: default fallback, round-tripping through the file, and
/// tolerance of a missing/corrupt settings file.
/// </summary>
public sealed class GuiSettingsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

    [Fact]
    public void Load_WithNoFile_ReturnsDefault()
    {
        var store = new GuiSettingsStore(TempPath());

        var settings = store.Load();

        settings.TelemetryServerAddress.Should().Be(TelemetryChannelFactory.DefaultServerAddress);
    }

    [Fact]
    public void Save_then_Load_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var store = new GuiSettingsStore(path);

            store.Save(new GuiSettings("https://example.test:1234"));

            store.Load().TelemetryServerAddress.Should().Be("https://example.test:1234");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithCorruptFile_ReturnsDefault()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "not json");
            var store = new GuiSettingsStore(path);

            store.Load().TelemetryServerAddress.Should().Be(TelemetryChannelFactory.DefaultServerAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesTheDirectoryIfMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "gui-settings.json");
        try
        {
            var store = new GuiSettingsStore(path);

            store.Save(new GuiSettings("https://example.test:4321"));

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
