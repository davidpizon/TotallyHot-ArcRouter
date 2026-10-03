namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// Owns this test process's scratch space: one directory per run under the shared
/// <c>%TEMP%/arcrouter-tests</c> root, which every test class in this assembly creates its own directories
/// inside. A run that only ever writes under its own root can delete all of it at the end without touching a
/// concurrently running suite in another worktree, which a sweep of the shared root by creation time could not
/// promise (see <see cref="TestTempDirectorySweeper"/>).
/// </summary>
internal static class TestScratchDirectory
{
    /// <summary>The root shared by every test run on this machine; holds one <see cref="RunRoot"/> per run.</summary>
    public static string SharedRoot { get; } = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests");

    /// <summary>
    /// Gets this run's own scratch directory, unique per test process. Named with the process id as well as a
    /// GUID only so a leftover is easy to attribute when someone looks; uniqueness comes from the GUID.
    /// </summary>
    public static string RunRoot { get; } = Path.Combine(path1: SharedRoot,
        path2: $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
}
