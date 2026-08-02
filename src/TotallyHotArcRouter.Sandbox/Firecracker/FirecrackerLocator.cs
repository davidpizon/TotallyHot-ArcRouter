namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Locates the <c>firecracker</c> binary from configuration, common install paths, or <c>PATH</c>. Used to
/// gate Tier-2 registration so a host without Firecracker never routes work to a microVM.
/// </summary>
public static class FirecrackerLocator
{
    private static readonly string[] CommonPaths =
    [
        "/usr/bin/firecracker",
        "/usr/local/bin/firecracker",
        "/opt/firecracker/firecracker",
    ];

    /// <summary>Finds the Firecracker binary path, or null if none can be found.</summary>
    /// <param name="configuredPath">An explicit path from configuration, checked first.</param>
    /// <returns>The resolved absolute path, or null.</returns>
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var path in CommonPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, "firecracker");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Indicates whether Firecracker can run on this host (Linux, KVM present, binary found).</summary>
    /// <param name="configuredPath">An explicit binary path from configuration.</param>
    /// <returns><see langword="true"/> when a microVM launch is feasible.</returns>
    public static bool IsAvailable(string? configuredPath) =>
        OperatingSystem.IsLinux() && File.Exists("/dev/kvm") && Find(configuredPath) is not null;
}

