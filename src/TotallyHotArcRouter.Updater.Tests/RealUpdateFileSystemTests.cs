using System.IO.Compression;
using AwesomeAssertions;
using TotallyHot.ArcRouter.Updater;

namespace TotallyHot.ArcRouter.Updater.Tests;

/// <summary>
/// Exercises <see cref="RealUpdateFileSystem"/> against real temporary directories - the "real-filesystem
/// test" alternative to a fake, per the auto-update plan's testing requirement. No Windows Service
/// involved: this only covers the filesystem half of the swap.
/// </summary>
public sealed class RealUpdateFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"updater-fs-tests-{Guid.NewGuid():N}");
    private readonly RealUpdateFileSystem _fileSystem = new();

    public RealUpdateFileSystemTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DirectoryExists_ReflectsRealFilesystem()
    {
        var path = Path.Combine(_root, "present");
        Directory.CreateDirectory(path);

        _fileSystem.DirectoryExists(path).Should().BeTrue();
        _fileSystem.DirectoryExists(Path.Combine(_root, "absent")).Should().BeFalse();
    }

    [Fact]
    public void MoveDirectory_RenamesDirectoryWithContents()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "hello");
        var destination = Path.Combine(_root, "destination");

        _fileSystem.MoveDirectory(source, destination);

        Directory.Exists(source).Should().BeFalse();
        File.ReadAllText(Path.Combine(destination, "file.txt")).Should().Be("hello");
    }

    [Fact]
    public void ExtractZip_CreatesDestinationAndExtractsEntries()
    {
        var zipPath = Path.Combine(_root, "update.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("TotallyHotArcRouter.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("binary-content");
        }

        var destination = Path.Combine(_root, "extracted");

        _fileSystem.ExtractZip(zipPath, destination);

        File.Exists(Path.Combine(destination, "TotallyHotArcRouter.exe")).Should().BeTrue();
    }

    [Fact]
    public void DeleteDirectory_RemovesDirectoryRecursively()
    {
        var path = Path.Combine(_root, "to-delete");
        Directory.CreateDirectory(Path.Combine(path, "nested"));
        File.WriteAllText(Path.Combine(path, "nested", "file.txt"), "data");

        _fileSystem.DeleteDirectory(path);

        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void DeleteDirectory_NonExistentPath_DoesNotThrow()
    {
        var act = () => _fileSystem.DeleteDirectory(Path.Combine(_root, "never-existed"));

        act.Should().NotThrow();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
