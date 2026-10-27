namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Serializes the test classes that build a scratch <c>transcripts.db</c>. Teardown clears only that
/// class's own SQLite pool.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SessionStorageCollection
{
    /// <summary>The collection name the session storage test classes share.</summary>
    public const string Name = "Session storage";
}
