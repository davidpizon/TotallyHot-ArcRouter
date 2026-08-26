namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// The filesystem operations <see cref="UpdateApplier"/> needs in order to refresh the sibling
/// <c>...\Updater\</c> directory before handing the Router swap off to it: probe for the updater
/// executable, rename the directory aside as a backup, extract the new Updater zip in its place, and
/// clean up. An abstraction over <see cref="System.IO.File"/>/<see cref="System.IO.Directory"/>/
/// <see cref="System.IO.Compression.ZipFile"/> in the same shape as this codebase's other seams
/// (<c>IEnvironmentVariableProvider</c>, <c>IUpdaterProcessLauncher</c>, the Updater project's own
/// <c>IUpdateFileSystem</c>), so a unit test can drive the backup/extract/restore failure paths without
/// any real <c>%ProgramFiles%</c> access.
/// </summary>
public interface IUpdateFileOperations
{
    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Moves (renames) the directory at <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <exception cref="IOException">The directory could not be moved.</exception>
    void MoveDirectory(string source, string destination);

    /// <summary>Extracts every entry in the zip at <paramref name="zipPath"/> into <paramref name="destinationDirectory"/>, creating it if necessary.</summary>
    /// <exception cref="IOException">The zip could not be read or the destination could not be written.</exception>
    /// <exception cref="InvalidDataException">The file at <paramref name="zipPath"/> is not a valid zip archive.</exception>
    void ExtractZip(string zipPath, string destinationDirectory);

    /// <summary>Recursively deletes the directory at <paramref name="path"/>, if it exists.</summary>
    /// <exception cref="IOException">The directory exists but could not be deleted.</exception>
    void DeleteDirectory(string path);
}

/// <summary>Production <see cref="IUpdateFileOperations"/>, wrapping the real filesystem and <see cref="System.IO.Compression.ZipFile"/> directly.</summary>
public sealed class RealUpdateFileOperations : IUpdateFileOperations
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

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
}
