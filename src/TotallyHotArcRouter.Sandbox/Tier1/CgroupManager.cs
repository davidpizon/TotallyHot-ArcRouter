using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// Creates and tears down a per-run cgroup v2 leaf and reports its OOM/peak-memory accounting. All
/// filesystem operations are best-effort: on a host where the process lacks the privilege to write the
/// cgroup tree, limits simply are not applied (the run still executes under the other controls) and this
/// is logged rather than thrown.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CgroupManager
{
    private const string CgroupRoot = "/sys/fs/cgroup";
    private const string Parent = "acrouter";

    private readonly ILogger<CgroupManager> _logger;

    /// <summary>Initializes a new instance of the <see cref="CgroupManager"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public CgroupManager(ILogger<CgroupManager> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Creates a leaf cgroup and applies the resource limits from <paramref name="spec"/>.</summary>
    /// <param name="name">A unique leaf name for this run.</param>
    /// <param name="spec">The run spec carrying the rendered limit values.</param>
    /// <returns>The absolute path of the created leaf cgroup, or null if creation failed.</returns>
    public string? Create(string name, JailSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(spec);

        try
        {
            var parentPath = Path.Combine(CgroupRoot, Parent);
            Directory.CreateDirectory(parentPath);
            TryWrite(Path.Combine(parentPath, "cgroup.subtree_control"), "+memory +pids +cpu");

            var leaf = Path.Combine(parentPath, name);
            Directory.CreateDirectory(leaf);

            TryWrite(Path.Combine(leaf, "memory.max"), spec.MemoryMax);
            TryWrite(Path.Combine(leaf, "memory.swap.max"), CgroupLimits.SwapMax);
            TryWrite(Path.Combine(leaf, "pids.max"), spec.PidsMax);
            TryWrite(Path.Combine(leaf, "cpu.max"), spec.CpuMax);

            return leaf;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to create cgroup leaf {Name}; running without cgroup limits.", name);
            return null;
        }
    }

    /// <summary>Moves a process into the leaf cgroup so its limits apply.</summary>
    /// <param name="leafPath">The leaf cgroup path from <see cref="Create"/>.</param>
    /// <param name="pid">The process id to move.</param>
    public void AddProcess(string leafPath, int pid)
    {
        if (string.IsNullOrEmpty(leafPath))
        {
            return;
        }

        TryWrite(Path.Combine(leafPath, "cgroup.procs"), pid.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Reads the number of OOM kills recorded by the leaf cgroup.</summary>
    /// <param name="leafPath">The leaf cgroup path.</param>
    /// <returns>The OOM-kill count, or 0 when unavailable.</returns>
    public long ReadOomKills(string? leafPath)
    {
        if (string.IsNullOrEmpty(leafPath))
        {
            return 0;
        }

        try
        {
            foreach (var line in File.ReadLines(Path.Combine(leafPath, "memory.events")))
            {
                if (line.StartsWith("oom_kill ", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("oom_kill ".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    return count;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read memory.events for {Leaf}.", leafPath);
        }

        return 0;
    }

    /// <summary>Reads the peak resident memory recorded by the leaf cgroup.</summary>
    /// <param name="leafPath">The leaf cgroup path.</param>
    /// <returns>The peak memory in bytes, or 0 when unavailable.</returns>
    public long ReadPeakMemory(string? leafPath)
    {
        if (string.IsNullOrEmpty(leafPath))
        {
            return 0;
        }

        try
        {
            var text = File.ReadAllText(Path.Combine(leafPath, "memory.peak")).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var peak) ? peak : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read memory.peak for {Leaf}.", leafPath);
            return 0;
        }
    }

    /// <summary>Removes the leaf cgroup.</summary>
    /// <param name="leafPath">The leaf cgroup path.</param>
    public void Remove(string? leafPath)
    {
        if (string.IsNullOrEmpty(leafPath))
        {
            return;
        }

        try
        {
            // A cgroup leaf is removed by rmdir once empty; Directory.Delete maps to that on cgroupfs.
            Directory.Delete(leafPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to remove cgroup leaf {Leaf}.", leafPath);
        }
    }

    /// <summary>Writes a value to a cgroup control file, logging and swallowing any I/O or permission failure rather than throwing.</summary>
    private void TryWrite(string path, string value)
    {
        try
        {
            File.WriteAllText(path, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to write cgroup control file {Path}.", path);
        }
    }
}

