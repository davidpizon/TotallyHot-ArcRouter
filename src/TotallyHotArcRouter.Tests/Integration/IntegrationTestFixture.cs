namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Shared helpers for integration tests.
/// </summary>
public sealed class IntegrationTestFixture : IDisposable
{
    private readonly List<string> _directoriesToDelete = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var directory in _directoriesToDelete)
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(path: directory, true);
            }
            catch
            {
                // Best-effort cleanup for test temp directories.
            }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates and tracks a unique temporary directory for test use.
    /// </summary>
    /// <returns>Absolute path to a temporary directory.</returns>
    public string CreateTempDirectory()
    {
        var path = Path.Combine(path1: Path.GetTempPath(),
            path2: $"TotallyHotArcRouter_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directoriesToDelete.Add(path);
        return path;
    }
}