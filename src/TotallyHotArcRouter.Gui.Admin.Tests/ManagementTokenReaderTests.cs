namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>Covers <see cref="ManagementTokenReader"/>: reading the shared per-user management token file.</summary>
public sealed class ManagementTokenReaderTests
{
    [Fact]
    public void TryRead_FileDoesNotExist_ReturnsNull()
    {
        var path = TempTokenPath();

        var token = ManagementTokenReader.TryRead(path);

        Assert.Null(token);
    }

    [Fact]
    public void TryRead_FileContainsToken_ReturnsTrimmedToken()
    {
        var path = TempTokenPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path: path, contents: "  s3cret-token  \n");

            var token = ManagementTokenReader.TryRead(path);

            Assert.Equal(expected: "s3cret-token", actual: token);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void TryRead_FileIsEmptyOrWhitespace_ReturnsNull()
    {
        var path = TempTokenPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path: path, contents: "   \n  ");

            var token = ManagementTokenReader.TryRead(path);

            Assert.Null(token);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void TryRead_FileLockedByAnotherHandle_ReturnsNullInsteadOfThrowing()
    {
        var path = TempTokenPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path: path, contents: "s3cret");

            // Hold an exclusive handle so File.ReadAllText inside TryRead hits IOException, not a clean read.
            using var exclusiveHandle = new FileStream(path: path, mode: FileMode.Open, access: FileAccess.Read,
                share: FileShare.None);

            var token = ManagementTokenReader.TryRead(path);

            Assert.Null(token);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void TryRead_NoPathOverride_UsesDefaultLocalAppDataLocation()
    {
        // No override means the real %LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt is consulted; this
        // just exercises the default-path branch without asserting a specific outcome (the file may or may
        // not exist on the machine running the test).
        var token = ManagementTokenReader.TryRead();

        Assert.True(token is null || token.Length > 0);
    }

    private static string TempTokenPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-gui-admin-tests",
            path3: Guid.NewGuid().ToString("N"), path4: "management-token.txt");
    }

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(path: directory, true);
    }
}