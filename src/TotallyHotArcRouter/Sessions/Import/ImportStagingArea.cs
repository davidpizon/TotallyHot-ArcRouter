using System.Collections.Concurrent;
using System.Security.Cryptography;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>A copy of a zip the router made for itself so it can be approved and imported without changing underneath.</summary>
/// <param name="Id">Opaque, unguessable id the approval request and the import name the copy by.</param>
/// <param name="StagedPath">Where the copy lives, inside the router's session folder.</param>
/// <param name="SourcePath">Where the zip came from, as the operator should see it.</param>
/// <param name="Sha256Hex">The copy's SHA-256 as 64 lowercase hex digits, computed by the router while it copied.</param>
/// <param name="SizeBytes">The copy's size.</param>
/// <param name="ExpiresAtUtc">When the router discards the copy if it has not been imported.</param>
public sealed record StagedImport(
    string Id,
    string StagedPath,
    string SourcePath,
    string Sha256Hex,
    long SizeBytes,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Makes private, hashed copies of zips to import (#165 phase 4, ADR-0020 amendment). A passkey approval for an
/// import is bound to the SHA-256 of the bytes that will be imported, and those bytes must not be able to change
/// between the operator's approval and the read. So the router copies the zip once, hashes the copy as it writes
/// it, and imports only from the copy: whatever happens to the original afterwards is irrelevant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where.</b> A copy lives in the session folder, which inherits the admin-only protection of the data
/// directory, under a random <c>.tmp</c> name. Startup recovery deletes every <c>.tmp</c> file in that folder that
/// no index row names, so copies do not survive a restart.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxStaged"/> copies exist at once and each is discarded after
/// <see cref="Lifetime"/>, so staging cannot be used to fill the disk with copies nobody imports. The free-space
/// check counts the copy plus the same reserve an import uses.
/// </para>
/// <para>
/// <b>What may be staged.</b> A regular file at an absolute path outside the router's data directory (link-aware,
/// as for an export destination), whose first bytes are a zip signature. It is not parsed here; the importer
/// validates the archive when it reads it.
/// </para>
/// </remarks>
public sealed class ImportStagingArea
{
    /// <summary>How long a copy is kept for the operator to approve and import before it is discarded.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>The most copies held at once.</summary>
    public const int MaxStaged = 4;

    private const long MinimumReserveBytes = 1L << 30;
    private const int CopyBufferBytes = 80 * 1024;
    private const int IdBytes = 16;

    private readonly ConcurrentDictionary<string, StagedImport> _staged = new(StringComparer.Ordinal);
    private readonly Lock _admission = new();
    private readonly Func<string, (long FreeBytes, long TotalBytes)> _volumeSpace;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="ImportStagingArea"/> class.</summary>
    /// <param name="volumeSpace">Reports free and total bytes of the volume holding a path; a test passes a fake. Null reads the drive.</param>
    /// <param name="timeProvider">The clock for expiry; null uses the system clock.</param>
    public ImportStagingArea(
        Func<string, (long FreeBytes, long TotalBytes)>? volumeSpace = null, TimeProvider? timeProvider = null)
    {
        _volumeSpace = volumeSpace ?? ConversationExportWriter.DriveSpace;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the store's session folder and hashes the copy.
    /// </summary>
    /// <param name="store">The store whose folder receives the copy and whose data directory the source must be outside of.</param>
    /// <param name="sourcePath">The zip to stage: an absolute path to a regular file.</param>
    /// <param name="cancellationToken">Cancels the copy, which then leaves no file behind.</param>
    /// <returns>The staged copy.</returns>
    /// <exception cref="ConversationImportException">When the source breaks a rule, too many copies are staged, or the volume is too full.</exception>
    public StagedImport Stage(SessionStore store, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sourcePath);

        var source = ValidateSource(store, sourcePath);
        var size = new FileInfo(source).Length;

        // Admission and capacity are one critical section so concurrent callers cannot all see room.
        string id;
        lock (_admission)
        {
            Sweep();
            if (_staged.Count >= MaxStaged)
            {
                throw new ConversationImportException(
                    ConversationImportFailure.TooManyStaged,
                    "Too many staged imports are waiting. Import or let the open ones lapse, then try again.");
            }

            id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdBytes));

            // Reserved before the copy so a second caller counts this one against the limit.
            _staged[id] = new StagedImport(id, string.Empty, source, string.Empty, size, DateTimeOffset.MaxValue);
        }

        var stagedPath = Path.Combine(store.Folder, id + ".tmp");
        try
        {
            EnsureFreeSpace(store, size);
            var (sha256, length) = Copy(source, stagedPath, cancellationToken);
            var entry = new StagedImport(id, stagedPath, source, sha256, length, _time.GetUtcNow() + Lifetime);
            _staged[id] = entry;
            return entry;
        }
        catch
        {
            _staged.TryRemove(id, out _);
            DeleteQuietly(stagedPath);
            throw;
        }
    }

    /// <summary>Returns the staged copy with this id if it is still held.</summary>
    /// <param name="id">The copy's id.</param>
    /// <returns>The copy, or <see langword="null"/> when it is unknown, discarded, lapsed or still being written.</returns>
    public StagedImport? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        Sweep();
        return _staged.TryGetValue(id, out var entry) && entry.Sha256Hex.Length > 0 ? entry : null;
    }

    /// <summary>Deletes a staged copy and forgets it. Safe to call for an unknown id.</summary>
    /// <param name="id">The copy's id.</param>
    public void Discard(string id)
    {
        if (_staged.TryRemove(id, out var entry) && entry.StagedPath.Length > 0) DeleteQuietly(entry.StagedPath);
    }

    /// <summary>Discards every copy past its expiry.</summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, entry) in _staged)
        {
            if (now < entry.ExpiresAtUtc) continue;

            if (_staged.TryRemove(new KeyValuePair<string, StagedImport>(id, entry)) && entry.StagedPath.Length > 0)
            {
                DeleteQuietly(entry.StagedPath);
            }
        }
    }

    /// <summary>
    /// Checks the source rules: absolute, an existing regular file, and not inside the router's data directory
    /// (the session folder's parent), looked at with every link followed so a link into it is refused.
    /// </summary>
    /// <param name="store">The store whose folders are off limits.</param>
    /// <param name="path">The requested source.</param>
    /// <returns>The normalized absolute path.</returns>
    /// <exception cref="ConversationImportException">When a rule is broken.</exception>
    private static string ValidateSource(SessionStore store, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw Invalid("The import source must be an absolute path.");
        }

        var full = Path.GetFullPath(path);
        var sessionFolder = Path.GetFullPath(store.Folder);
        var dataDirectory = Path.GetDirectoryName(sessionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var resolved = Path.Combine(
            ConversationExportWriter.ResolveLinks(Path.GetDirectoryName(full) ?? full), Path.GetFileName(full));

        // The file itself can be a link too; what it finally points at is what would be opened.
        try
        {
            if (new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target) resolved = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Invalid("The import source could not be examined.");
        }

        if (ConversationExportWriter.IsUnder(full, sessionFolder)
            || (dataDirectory is not null && ConversationExportWriter.IsUnder(full, dataDirectory))
            || ConversationExportWriter.IsUnder(resolved, ConversationExportWriter.ResolveLinks(sessionFolder))
            || (dataDirectory is not null
                && ConversationExportWriter.IsUnder(resolved, ConversationExportWriter.ResolveLinks(dataDirectory))))
        {
            throw Invalid("The import source must be outside the router's data directory.");
        }

        if (!File.Exists(full)) throw Invalid("The import source does not exist or is not a file.");

        return full;
    }

    /// <summary>Refuses the copy unless the volume has room for it plus the larger of 1 GiB and 10% of the volume.</summary>
    /// <param name="store">The store whose volume receives the copy.</param>
    /// <param name="size">The size of the copy.</param>
    /// <exception cref="ConversationImportException">When the volume is too full.</exception>
    private void EnsureFreeSpace(SessionStore store, long size)
    {
        var (free, total) = _volumeSpace(store.Folder);
        if (free < size + Math.Max(MinimumReserveBytes, total / 10))
        {
            throw new ConversationImportException(
                ConversationImportFailure.InsufficientDiskSpace,
                "The router's data volume does not have enough free space to stage this import.");
        }
    }

    /// <summary>
    /// Copies the source to the staged path while hashing what is written, so the digest is of the copy and not of
    /// a second read of a file that could change. Checks the zip signature before writing anything.
    /// </summary>
    /// <param name="source">The source file.</param>
    /// <param name="destination">The staged path, which must not exist.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The copy's lowercase hex SHA-256 and length.</returns>
    /// <exception cref="ConversationImportException">When the source does not start like a zip.</exception>
    private static (string Sha256Hex, long Length) Copy(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes);
        var signature = new byte[4];
        var read = input.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        input.Position = 0;
        if (read < 4 || signature[0] != (byte)'P' || signature[1] != (byte)'K'
            || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))
        {
            throw Invalid("The import source is not a zip file.");
        }

        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
            total += count;
        }

        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    /// <summary>Deletes a file, ignoring a failure: an undeleted copy is swept by startup recovery.</summary>
    /// <param name="path">The file.</param>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup recovery deletes the leftover.
        }
    }

    /// <summary>Builds the exception for a source that breaks a rule.</summary>
    /// <param name="message">A message that names the rule without echoing the path.</param>
    /// <returns>The exception to throw.</returns>
    private static ConversationImportException Invalid(string message) =>
        new(ConversationImportFailure.InvalidSource, message);
}
