using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Covers <see cref="TrayDiscoveryReader"/> against the exact camelCase JSON shape
/// <c>WebInterfaceDiscoveryFile.Write</c> produces, plus the missing/empty/corrupt-file tolerance the
/// tray depends on to treat "no discovery file" as "no running router found" rather than crash.
/// </summary>
public sealed class TrayDiscoveryReaderTests
{
    private static string TempPath()
    {
        return Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid() + ".json");
    }

    [Fact]
    public void DefaultPath_IsTheDiscoveryFileUnderProgramData()
    {
        // Checks the location without reading the file there: it is live state a running router (or, before
        // the router's tests were redirected to a scratch directory, a test run) rewrites, so a test that read
        // it would pass or fail depending on what else this machine happened to be doing.
        var expected = Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            path2: "TotallyHotArcRouter",
            path3: "web-interface.json");

        TrayDiscoveryReader.DefaultPath().Should().Be(expected);
    }

    [Fact]
    public void DefaultPaths_CheckProgramDataThenThePerUserDirectory()
    {
        var perUser = Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path2: "TotallyHotArcRouter",
            path3: "web-interface.json");

        TrayDiscoveryReader.DefaultPaths().Should().Equal(TrayDiscoveryReader.DefaultPath(), perUser);
    }

    [Fact]
    public void TryReadFirst_FirstFileHasNoWebUrl_FallsThroughToALaterFileThatDoes()
    {
        var stale = TempPath();
        var live = TempPath();
        try
        {
            File.WriteAllText(path: stale, contents: """{ "caThumbprint": "AB12" }""");
            File.WriteAllText(path: live, contents: """{ "webUrl": "https://localhost:48804" }""");

            TrayDiscoveryReader.TryReadFirst([stale, live])!.WebUrl.Should().Be("https://localhost:48804");
        }
        finally
        {
            File.Delete(stale);
            File.Delete(live);
        }
    }

    [Fact]
    public void TryReadFirst_NoFileHasWebUrl_ReturnsTheFirstParsedFile()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: """{ "caThumbprint": "AB12" }""");

            var result = TrayDiscoveryReader.TryReadFirst([TempPath(), path]);

            result.Should().NotBeNull();
            result.WebUrl.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFirst_NoFilesExist_ReturnsNull()
    {
        TrayDiscoveryReader.TryReadFirst([TempPath(), TempPath()]).Should().BeNull();
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var result = TrayDiscoveryReader.TryRead(TempPath());

        result.Should().BeNull();
    }

    [Fact]
    public void TryRead_ValidFile_ParsesWebUrl_AndIgnoresUnusedFields()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: """
                {
                  "webUrl": "https://localhost:5004",
                  "caThumbprint": "AB12CD34",
                  "writtenAtUtc": "2026-09-15T12:00:00Z"
                }
                """);

            var result = TrayDiscoveryReader.TryRead(path);

            result.Should().NotBeNull();
            result.WebUrl.Should().Be("https://localhost:5004");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_NullFields_ParsesAsNull()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: """
                { "webUrl": null, "caThumbprint": null, "writtenAtUtc": "2026-09-15T12:00:00Z" }
                """);

            var result = TrayDiscoveryReader.TryRead(path);

            result.Should().NotBeNull();
            result.WebUrl.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_EmptyFile_ReturnsNull()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: string.Empty);

            var result = TrayDiscoveryReader.TryRead(path);

            result.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNull()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path: path, contents: "not json");

            var result = TrayDiscoveryReader.TryRead(path);

            result.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
