using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>What migration does with one regular file from the old, unprotected tree.</summary>
public enum MigrationFileDecision
{
    /// <summary>Copy it into the new protected tree at the same relative path.</summary>
    Adopt,

    /// <summary>Adopt it only if its SHA-256 matches a pinned value; otherwise discard it.</summary>
    AdoptIfHashMatches,

    /// <summary>
    /// Delete it without keeping a copy. Used only for files the router downloads again on its own, so
    /// nothing of the operator's is lost and a planted copy is never loaded.
    /// </summary>
    Discard,

    /// <summary>Keep it out of the live tree, in the quarantine folder, for an administrator to review.</summary>
    Quarantine
}

/// <summary>
/// The names and per-file rules every platform's migration shares (ADR-0024 rule 3; plan §3.1).
/// Migration cannot tell a file the operator's own account wrote from one an application running as that
/// account planted, so these rules decide what is safe to carry into the protected tree:
/// <list type="bullet">
/// <item><description>
/// <c>appsettings.local.json</c> is never adopted. It is quarantined, and the router ignores it until an
/// administrator reviews it and copies it back (plan "Config overlay").
/// </description></item>
/// <item><description>
/// The BGE embedding files are adopted only when their SHA-256 matches the pinned value in
/// <see cref="EmbeddingOptions"/>.
/// </description></item>
/// <item><description>
/// Every other file under <c>models/</c> is discarded. The router downloads model files again on first
/// use (and <c>llm_router</c> sync verifies them against published checksums), so dropping them costs
/// bandwidth, not data.
/// </description></item>
/// <item><description>Everything else is adopted.</description></item>
/// </list>
/// </summary>
public static class DataDirectoryMigrationRules
{
    /// <summary>The folder, inside the protected root, that holds the files a migration kept out of the live tree.</summary>
    public const string QuarantineDirectoryName = "quarantine";

    /// <summary>The suffix of a file migration is still writing; a leftover one is deleted when migration resumes.</summary>
    public const string PartialCopySuffix = ".migrating";

    /// <summary>The operator configuration overlay, which migration never adopts.</summary>
    public const string OverlayFileName = "appsettings.local.json";

    /// <summary>
    /// The marker a Linux, macOS or container migration writes into a tree it has made owner-only. A
    /// <c>0700</c> mode alone does not prove a tree was migrated: systemd's <c>StateDirectoryMode=0700</c>
    /// sets that mode on every start, even on a tree whose files were never copied out from under
    /// descriptors other accounts opened earlier. Only the service account and root can write into a
    /// <c>0700</c> directory, so the marker cannot be planted.
    /// </summary>
    public const string ProtectedMarkerFileName = ".protected-data-directory";

    private const string StagingInfix = ".migrating-";
    private const string RetiredInfix = ".retired-";
    private const string SquattedInfix = ".squatted-";
    private const string LockedInfix = ".quarantine-";
    private const string ModelsPrefix = "models/";

    private static readonly Dictionary<string, string> PinnedModelHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["models/bge-large-en-v1.5/model.onnx"] = EmbeddingOptions.DefaultModelSha256,
        ["models/bge-large-en-v1.5/tokenizer.json"] = EmbeddingOptions.DefaultTokenizerJsonSha256
    };

    /// <summary>
    /// Decides what happens to the regular file at <paramref name="relativePath"/>, written with forward
    /// slashes relative to the old root. Returns the pinned hash for
    /// <see cref="MigrationFileDecision.AdoptIfHashMatches"/>.
    /// </summary>
    public static (MigrationFileDecision Decision, string? PinnedSha256) Decide(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = relativePath.Replace('\\', '/');

        if (string.Equals(normalized, OverlayFileName, StringComparison.OrdinalIgnoreCase))
            return (MigrationFileDecision.Quarantine, null);

        if (PinnedModelHashes.TryGetValue(normalized, out var pinned))
            return (MigrationFileDecision.AdoptIfHashMatches, pinned);

        if (normalized.StartsWith(ModelsPrefix, StringComparison.OrdinalIgnoreCase))
            return (MigrationFileDecision.Discard, null);

        return (MigrationFileDecision.Adopt, null);
    }

    /// <summary>A fresh, randomly named sibling of <paramref name="root"/> to build the new protected tree in.</summary>
    public static string NewStagingPath(string root)
    {
        return TrimmedRoot(root) + StagingInfix + RandomSuffix();
    }

    /// <summary>A fresh name to rename the emptied old tree to, just before the new tree takes its place.</summary>
    public static string NewRetiredPath(string root)
    {
        return TrimmedRoot(root) + RetiredInfix + RandomSuffix();
    }

    /// <summary>
    /// A fresh name for the old tree on Linux and macOS once its accepted files have been copied out. It
    /// stays root-owned and <c>0700</c>, holding only the entries migration rejected.
    /// </summary>
    public static string NewLockedQuarantinePath(string root)
    {
        return TrimmedRoot(root) + LockedInfix + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A fresh name to set a squatted tree aside under, untouched.</summary>
    public static string NewSquattedPath(string root)
    {
        return TrimmedRoot(root) + SquattedInfix + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every staging tree a previous, interrupted migration of <paramref name="root"/> left behind.</summary>
    public static IReadOnlyList<string> FindStaging(string root)
    {
        return FindSiblings(root, StagingInfix);
    }

    /// <summary>Every retired tree a previous migration of <paramref name="root"/> did not finish removing.</summary>
    public static IReadOnlyList<string> FindRetired(string root)
    {
        return FindSiblings(root, RetiredInfix);
    }

    /// <summary>The quarantine folder for one migration run, inside <paramref name="protectedRoot"/>.</summary>
    public static string NewQuarantinePath(string protectedRoot)
    {
        return Path.Combine(protectedRoot, QuarantineDirectoryName,
            "migration-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<string> FindSiblings(string root, string infix)
    {
        var trimmed = TrimmedRoot(root);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return [];

        try
        {
            return
            [
                .. Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(trimmed) + infix + "*")
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string TrimmedRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string RandomSuffix()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }
}
