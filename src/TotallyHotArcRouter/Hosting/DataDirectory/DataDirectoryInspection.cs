namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// What a look at a candidate data directory found, before anything is read from or written into it
/// (ADR-0024). The router only uses a machine-wide directory in the <see cref="Protected"/> state; every
/// other state either falls back to the per-user directory or stops the service, depending on who is
/// asking - see <see cref="DataDirectoryBootstrap"/>.
/// </summary>
public enum DataDirectoryState
{
    /// <summary>Nothing exists at the path yet.</summary>
    Missing,

    /// <summary>
    /// A real directory (not a link), owned by a trusted account, whose access list grants nothing beyond
    /// the trusted accounts: on Windows a protected DACL granting only <c>SYSTEM</c> and
    /// <c>Administrators</c>; on Linux and macOS no group or other mode bits.
    /// </summary>
    Protected,

    /// <summary>
    /// Something exists at the path but fails at least one protection check - the pre-ADR-0024 layout, a
    /// directory planted by another account, a link, or a file. <see cref="DataDirectoryInspection.Reason"/>
    /// says which check failed.
    /// </summary>
    Unprotected,

    /// <summary>
    /// The directory exists but this process may not even read its security information - the normal
    /// result for an unelevated process looking at a protected Windows root.
    /// </summary>
    Inaccessible
}

/// <summary>
/// The outcome of inspecting one candidate data directory, with enough detail to log why a directory was
/// rejected and to tell a legacy layout (owned by a trusted account, so migratable) from a squat.
/// </summary>
/// <param name="State">The directory's classification.</param>
/// <param name="Owner">The directory's owner, as a display string (an account name, SID or uid), when it could be read.</param>
/// <param name="OwnerTrusted">
/// Whether <paramref name="Owner"/> is one of the accounts the caller's policy trusts. Meaningful only when
/// <paramref name="Owner"/> is not <see langword="null"/>.
/// </param>
/// <param name="Reason">For <see cref="DataDirectoryState.Unprotected"/>, the first check that failed, in words.</param>
public sealed record DataDirectoryInspection(
    DataDirectoryState State,
    string? Owner = null,
    bool OwnerTrusted = false,
    string? Reason = null)
{
    /// <summary>The inspection result for a path where nothing exists.</summary>
    public static DataDirectoryInspection Missing { get; } = new(DataDirectoryState.Missing);

    /// <summary>The inspection result for a directory whose security information this process may not read.</summary>
    public static DataDirectoryInspection Inaccessible { get; } = new(DataDirectoryState.Inaccessible);
}
