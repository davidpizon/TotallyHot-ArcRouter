using System.Text.Json;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// File-backed <see cref="IRoutingGate"/>: the current enabled/disabled state persists to
/// <c>%ProgramData%\TotallyHotArcRouter\routing-gate.json</c> so a deliberate "Disable Routing" from the
/// tray survives the Windows Service restarting (crash, update, reboot) rather than silently resuming
/// traffic.
/// </summary>
/// <remarks>
/// Machine-wide <c>%ProgramData%</c>, not the per-user <c>%LOCALAPPDATA%</c> the management token and
/// telemetry certificate use: the service installs and runs as <c>LocalSystem</c>
/// (<c>TotallyHotArcRouter.Installer/Package.wxs</c>), whose own profile is not the interactive user's, so a
/// per-user path would be unreadable/unwritable in the installed configuration. This is safe only because
/// nothing else needs to read this file directly - the GUI observes and changes this state exclusively
/// through <see cref="RoutingGateAdminGrpcService"/>, never the file.
/// </remarks>
public sealed class RoutingGateStore : IRoutingGate
{
    private const string FileName = "routing-gate.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private bool _isEnabled;

    /// <summary>Initializes a new instance of the <see cref="RoutingGateStore"/> class, loading any persisted state.</summary>
    /// <param name="path">The state file path override; only meant for tests. Production callers omit it.</param>
    public RoutingGateStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _isEnabled = Load(_path);
    }

    /// <summary>Gets the default state file path: <c>%ProgramData%\TotallyHotArcRouter\routing-gate.json</c>.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TotallyHotArcRouter", FileName);

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isEnabled;
            }
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _isEnabled = enabled;
            Save(_path, enabled);
        }
    }

    /// <summary>Loads the persisted state, defaulting to enabled when no file exists yet or it can't be read.</summary>
    private static bool Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<RoutingGateState>(json);
            return state?.Enabled ?? true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Persists the state, creating the containing directory if it doesn't exist yet.</summary>
    private static void Save(string path, bool enabled)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new RoutingGateState(enabled)));
    }

    /// <summary>The on-disk shape of <see cref="RoutingGateStore"/>'s persisted state.</summary>
    /// <param name="Enabled">Whether the proxy currently accepts LLM-forwarding requests.</param>
    private sealed record RoutingGateState(bool Enabled);
}
