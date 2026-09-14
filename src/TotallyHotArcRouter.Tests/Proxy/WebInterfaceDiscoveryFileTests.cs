using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>Covers <see cref="WebInterfaceDiscoveryFile"/>'s round-trip and missing/corrupt-file handling.</summary>
public sealed class WebInterfaceDiscoveryFileTests
{
    [Fact]
    public void WriteThenTryRead_RoundTripsEveryField()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-interface-{Guid.NewGuid():N}.json");
        try
        {
            var info = new WebInterfaceDiscoveryInfo(WebUrl: "https://localhost:5004", CaThumbprint: "ABCDEF01");

            WebInterfaceDiscoveryFile.Write(info: info, path: path);
            var read = WebInterfaceDiscoveryFile.TryRead(path: path);

            Assert.NotNull(read);
            Assert.Equal(expected: info.WebUrl, actual: read.WebUrl);
            Assert.Equal(expected: info.CaThumbprint, actual: read.CaThumbprint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_CreatesContainingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"web-interface-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, WebInterfaceDiscoveryFile.FileName);
        try
        {
            WebInterfaceDiscoveryFile.Write(info: new WebInterfaceDiscoveryInfo(null, null), path: path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(path: directory, recursive: true);
        }
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-interface-missing-{Guid.NewGuid():N}.json");

        Assert.Null(WebInterfaceDiscoveryFile.TryRead(path: path));
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-interface-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path: path, contents: "{ not valid json");

            Assert.Null(WebInterfaceDiscoveryFile.TryRead(path: path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_NullInfo_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WebInterfaceDiscoveryFile.Write(info: null!));
    }
}
