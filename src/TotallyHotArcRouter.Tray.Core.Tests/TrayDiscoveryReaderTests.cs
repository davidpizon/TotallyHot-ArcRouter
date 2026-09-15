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
    public void TryRead_MissingFile_ReturnsNull()
    {
        var result = TrayDiscoveryReader.TryRead(TempPath());

        result.Should().BeNull();
    }

    [Fact]
    public void TryRead_ValidFile_ParsesEveryField()
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
            result!.WebUrl.Should().Be("https://localhost:5004");
            result.CaThumbprint.Should().Be("AB12CD34");
            result.WrittenAtUtc.Should().Be(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
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
            result!.WebUrl.Should().BeNull();
            result.CaThumbprint.Should().BeNull();
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
