using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Finds processes that could still write into a data directory after migration has locked it (plan
/// §3.1, "Look for writers"). Once every directory in the old tree is root-owned and <c>0700</c>, no
/// other account can open anything new in it, so the only remaining writers are processes that already
/// held a file open for writing, or mapped shared and writable. A copy cannot see those writes, so
/// migration stops if any exist.
/// </summary>
public interface IOpenWriterScanner
{
    /// <summary>
    /// Returns one description per process that holds a file under <paramref name="root"/> open for
    /// writing (or, where the platform cannot tell, mapped at all). Empty when there are none.
    /// </summary>
    IReadOnlyList<string> FindWriters(string root);
}

/// <summary>
/// The Linux scanner: reads each process's <c>/proc/&lt;pid&gt;/fdinfo</c> access flags for descriptors,
/// and <c>/proc/&lt;pid&gt;/maps</c> for shared writable mappings. A mapping keeps its file writable after
/// its descriptor closes, so the descriptor scan alone would miss it. A process it may not inspect is
/// reported too, so migration fails closed rather than assuming that process writes nothing.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxOpenWriterScanner : IOpenWriterScanner
{
    private const int AccessModeMask = 3;

    /// <inheritdoc/>
    public IReadOnlyList<string> FindWriters(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var ownPid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        List<string> writers = [];

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(processDirectory);
            if (!pid.All(char.IsAsciiDigit) || pid == ownPid) continue;

            try
            {
                ScanDescriptors(processDirectory, pid, normalizedRoot, writers);
                ScanMappings(processDirectory, pid, normalizedRoot, writers);
            }
            catch (UnauthorizedAccessException)
            {
                // Fail closed. A process whose descriptors cannot be read may hold a file in the tree open
                // for writing, and treating it as holding nothing would let migration copy a file that is
                // still being written. Root normally may read every process; this happens without
                // CAP_SYS_PTRACE (a rootless container, Yama ptrace_scope=3, an SELinux denial). Found by the
                // Linux container check on 2026-10-04, where the first version skipped such processes.
                writers.Add(
                    $"process {pid} ({ProcessName(processDirectory)}) could not be inspected (permission denied), so it may be writing");
            }
            catch (IOException)
            {
                // The process exited mid-scan, so it holds nothing open in the tree any more.
            }
        }

        return writers;
    }

    private static void ScanDescriptors(string processDirectory, string pid, string root, List<string> writers)
    {
        foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(processDirectory, "fd")))
        {
            if (new FileInfo(descriptor).LinkTarget is not { } target || !IsUnder(target, root)) continue;

            var flagsLine = File.ReadLines(Path.Combine(processDirectory, "fdinfo", Path.GetFileName(descriptor)))
                .FirstOrDefault(line => line.StartsWith("flags:", StringComparison.Ordinal));
            if (flagsLine is null) continue;

            var flags = Convert.ToInt64(flagsLine["flags:".Length..].Trim(), 8);
            if ((flags & AccessModeMask) != 0)
                writers.Add($"process {pid} ({ProcessName(processDirectory)}) has '{target}' open for writing");
        }
    }

    private static void ScanMappings(string processDirectory, string pid, string root, List<string> writers)
    {
        foreach (var line in File.ReadLines(Path.Combine(processDirectory, "maps")))
        {
            // address perms offset dev inode pathname
            var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6) continue;

            var permissions = fields[1];
            var path = fields[5].Trim();
            if (permissions.Length >= 4 && permissions[1] == 'w' && permissions[3] == 's' && IsUnder(path, root))
                writers.Add($"process {pid} ({ProcessName(processDirectory)}) has '{path}' mapped shared and writable");
        }
    }

    private static string ProcessName(string processDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(processDirectory, "comm")).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static bool IsUnder(string path, string root)
    {
        return path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    }
}

/// <summary>
/// The macOS scanner, built on <c>lsof</c>. <c>lsof</c> reports a descriptor's access mode but not a
/// mapping's, so any mapping of a file in the tree counts as a writer here (plan §3.1).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOpenWriterScanner : IOpenWriterScanner
{
    /// <inheritdoc/>
    public IReadOnlyList<string> FindWriters(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var startInfo = new ProcessStartInfo("lsof")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-n", "-P", "-F", "pfan", "+D", root }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start lsof to look for open writers.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        var errors = errorTask.GetAwaiter().GetResult();
        process.WaitForExit();

        List<string> writers = [.. Parse(output, Environment.ProcessId)];

        // Fail closed on an incomplete scan, as the Linux scanner does: lsof exits 1 both for "nothing open"
        // and for errors, and reports what it could not inspect only on stderr. Only exit 0, or exit 1 with
        // nothing on stderr, is a complete answer.
        var firstError = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (process.ExitCode > 1 || firstError is not null)
            writers.Add(
                $"lsof could not inspect everything under '{root}' (exit {process.ExitCode}: {firstError ?? "no detail"}), so a writer may have been missed");

        return writers;
    }

    /// <summary>
    /// Parses <c>lsof -F pfan</c> output: <c>p</c> starts a process, <c>f</c> a descriptor, and <c>a</c>
    /// and <c>n</c> give that descriptor's access mode and file name.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string output, int ownPid)
    {
        List<string> writers = [];
        string? pid = null;
        string? descriptor = null;
        var access = "";

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line[1..];
            switch (line[0])
            {
                case 'p':
                    pid = value;
                    break;
                case 'f':
                    descriptor = value;
                    access = "";
                    break;
                case 'a':
                    access = value;
                    break;
                case 'n' when pid is not null && descriptor is not null &&
                              pid != ownPid.ToString(CultureInfo.InvariantCulture):
                    if (descriptor is "txt" or "mem" or "mmap")
                        writers.Add($"process {pid} has '{value}' mapped");
                    else if (descriptor.All(char.IsAsciiDigit) && (access.Contains('w') || access.Contains('u')))
                        writers.Add($"process {pid} has '{value}' open for writing");
                    break;
            }
        }

        return writers;
    }
}
