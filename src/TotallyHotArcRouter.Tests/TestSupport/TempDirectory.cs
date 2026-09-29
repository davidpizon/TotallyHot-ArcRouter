namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// A uniquely named directory under the system temp path that is deleted, best-effort, on dispose. The
/// shared replacement for the per-file <c>Path.GetTempPath()</c> + <c>Guid</c> + cleanup boilerplate.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="TempDirectory"/> class and creates the directory.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(path1: System.IO.Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Gets the absolute path of the directory.</summary>
    public string Path { get; }

    /// <summary>Combines <paramref name="fileName"/> with the directory path.</summary>
    /// <param name="fileName">A file name relative to the directory.</param>
    /// <returns>The absolute path of the file.</returns>
    public string FileInDirectory(string fileName)
    {
        return System.IO.Path.Combine(path1: Path, path2: fileName);
    }

    /// <summary>Deletes the directory, ignoring failures (a locked file must not fail a passing test).</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(path: Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
