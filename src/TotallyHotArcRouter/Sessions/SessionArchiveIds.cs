namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Mints the stable cross-machine identities ADR-0019 and the #165 plan use for session files and
/// export turns. Version-7 UUIDs keep a time prefix so ids also sort by mint time without a separate
/// timestamp column for identity ordering.
/// </summary>
public static class SessionArchiveIds
{
    /// <summary>
    /// Creates a new <c>archive_session_id</c> when a session file is first created.
    /// </summary>
    /// <returns>A version-7 UUID unique across machines.</returns>
    public static Guid NewArchiveSessionId() => Guid.CreateVersion7();

    /// <summary>
    /// Creates a new <c>archive_turn_id</c> when a turn is captured (or imported).
    /// </summary>
    /// <returns>A version-7 UUID unique across machines.</returns>
    public static Guid NewArchiveTurnId() => Guid.CreateVersion7();
}
