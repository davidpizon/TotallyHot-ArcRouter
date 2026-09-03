using System.Text.Json;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// The GUI's local, per-user settings. Currently just the telemetry server address; more fields can
/// be added here as further settings-persistence needs arise (see docs/gui/backlog.md).
/// </summary>
/// <param name="TelemetryServerAddress">
/// The proxy's TLS gRPC endpoint <see cref="LiveDataStore"/> and <see cref="PriceSourceStore"/>
/// connect to. Defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
/// </param>
public sealed record GuiSettings(string TelemetryServerAddress)
{
    /// <summary>The settings a fresh install (or a corrupt/missing settings file) falls back to.</summary>
    public static GuiSettings Default { get; } = new(TelemetryChannelFactory.DefaultServerAddress);
}

/// <summary>Persists and loads <see cref="GuiSettings"/> across GUI restarts.</summary>
public interface IGuiSettingsStore
{
    /// <summary>Loads the persisted settings, or <see cref="GuiSettings.Default"/> if none are stored yet.</summary>
    GuiSettings Load();

    /// <summary>Persists <paramref name="settings"/>, overwriting whatever was previously stored.</summary>
    void Save(GuiSettings settings);
}

/// <summary>
/// File-based <see cref="IGuiSettingsStore"/>, JSON-serialized under the same per-user directory the
/// telemetry certificate and management token already use
/// (<c>%LOCALAPPDATA%\TotallyHotArcRouter\gui-settings.json</c>). MAUI's <c>Preferences</c> API was the
/// other option, but it requires a live platform host to back it and so cannot be exercised by a plain
/// xUnit/bUnit test the way this file-based store can - see <c>MauiProgram</c>'s remarks on why its own
/// coverage is excluded for the same reason.
/// </summary>
public sealed class GuiSettingsStore : IGuiSettingsStore
{
    private const string SettingsFileName = "gui-settings.json";

    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="GuiSettingsStore"/> class.</summary>
    /// <param name="path">The settings file path override; only meant for tests. Production callers omit it.</param>
    public GuiSettingsStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
    }

    /// <inheritdoc/>
    public GuiSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return GuiSettings.Default;

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json);
            return string.IsNullOrWhiteSpace(settings?.TelemetryServerAddress) ? GuiSettings.Default : settings;
        }
        catch (IOException)
        {
            return GuiSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return GuiSettings.Default;
        }
        catch (JsonException)
        {
            return GuiSettings.Default;
        }
    }

    /// <inheritdoc/>
    public void Save(GuiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path: _path, contents: JsonSerializer.Serialize(settings));
    }

    /// <summary>Gets the default settings file path: <c>%LOCALAPPDATA%\TotallyHotArcRouter\gui-settings.json</c>.</summary>
    public static string DefaultPath()
    {
        return Path.Combine(path1: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path2: "TotallyHotArcRouter", path3: SettingsFileName);
    }
}