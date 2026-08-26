namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// The filesystem operations <see cref="UpdaterService"/> performs on the install directory: back it up
/// aside (never delete until the new version starts successfully), extract the release zip over it, and
/// clean up. An abstraction over <see cref="System.IO.Directory"/>/<see cref="System.IO.Compression.ZipFile"/>
/// so a unit test can substitute a fake, or exercise <see cref="RealUpdateFileSystem"/> itself against a
/// real temporary directory without needing an actual Windows Service installed.
/// </summary>
public interface IUpdateFileSystem
{
    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Moves (renames) the directory at <paramref name="source"/> to <paramref name="destination"/>.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Extracts every entry in the zip at <paramref name="zipPath"/> into <paramref name="destinationDirectory"/>, creating it if necessary.</summary>
    void ExtractZip(string zipPath, string destinationDirectory);

    /// <summary>Recursively deletes the directory at <paramref name="path"/>, if it exists.</summary>
    void DeleteDirectory(string path);

    /// <summary>
    /// Computes the lowercase-hex SHA256 of the file at <paramref name="path"/>. Behind this seam (rather
    /// than a direct <see cref="System.Security.Cryptography.SHA256"/> call inside
    /// <see cref="UpdaterService"/>) so the re-verification gate at the top of
    /// <see cref="UpdaterService.RunAsync"/> can be unit-tested with a fake supplying any hash, matching
    /// or not, without a test having to craft real files whose hashes collide with a fixture value.
    /// </summary>
    /// <exception cref="System.IO.IOException">The file could not be read.</exception>
    string ComputeSha256(string path);
}

/// <summary>Production <see cref="IUpdateFileSystem"/>, wrapping the real filesystem and <see cref="System.IO.Compression.ZipFile"/> directly.</summary>
public sealed class RealUpdateFileSystem : IUpdateFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    /// <inheritdoc />
    public void ExtractZip(string zipPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationDirectory, overwriteFiles: true);
    }

    /// <inheritdoc />
    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc />
    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
