namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The archive ids minted for one captured turn before its transcript row is inserted, so the row and
/// the session file share them (#165 phase 2).
/// </summary>
/// <param name="ArchiveSessionId">The session file's id, from <c>SessionStore.ResolveArchiveSessionId</c>.</param>
/// <param name="ArchiveTurnId">The turn id minted when capture began.</param>
internal readonly record struct ArchiveBinding(Guid ArchiveSessionId, Guid ArchiveTurnId);
