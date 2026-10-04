namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>How a <c>--migrate-data-directory</c> run left the data directory.</summary>
public enum DataDirectoryMigrationOutcome
{
    /// <summary>The directory was already protected; at most, leftovers from an earlier run were cleaned up.</summary>
    AlreadyProtected,

    /// <summary>No directory existed, so a fresh protected one was created.</summary>
    Created,

    /// <summary>A legacy directory was migrated into a new protected tree that now stands in its place.</summary>
    Migrated,

    /// <summary>
    /// The directory was owned by an untrusted account. It was renamed aside untouched, and a fresh
    /// protected directory was created in its place.
    /// </summary>
    SquatSetAside
}

/// <summary>The result of one migration run, for the command's log and exit code.</summary>
/// <param name="Outcome">What the run did.</param>
/// <param name="Adopted">The number of files carried into the protected tree.</param>
/// <param name="Quarantined">The number of files kept out of the live tree, in the quarantine folder (or, on Linux and macOS, the locked old tree).</param>
/// <param name="Discarded">The number of re-downloadable files, links and extra hard-link names removed.</param>
/// <param name="SetAsidePath">Where a squatted tree, or the locked old tree on Linux and macOS, now lives.</param>
/// <param name="QuarantinePath">The quarantine folder holding this run's rejected files, when it kept any.</param>
public sealed record DataDirectoryMigrationResult(
    DataDirectoryMigrationOutcome Outcome,
    int Adopted = 0,
    int Quarantined = 0,
    int Discarded = 0,
    string? SetAsidePath = null,
    string? QuarantinePath = null);

/// <summary>
/// Thrown when migration must stop before changing anything further, because finishing would be unsafe:
/// another process holds a file open, an entry could not be deleted, or a tree could not be renamed.
/// The data directory is left in a state the next elevated run resumes from, and the service refuses to
/// start until then.
/// </summary>
public sealed class DataDirectoryMigrationBlockedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DataDirectoryMigrationBlockedException"/> class with a message.</summary>
    public DataDirectoryMigrationBlockedException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DataDirectoryMigrationBlockedException"/> class with a message and cause.</summary>
    public DataDirectoryMigrationBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
