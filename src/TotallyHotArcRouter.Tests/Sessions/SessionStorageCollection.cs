namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Serializes the test classes that build a <c>transcripts.db</c> and clean up with
/// <c>SqliteConnection.ClearAllPools</c>, which is process-wide: run in parallel, one class would close
/// another's pooled connections and delete its WAL files mid-test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SessionStorageCollection
{
    /// <summary>The collection name the session storage test classes share.</summary>
    public const string Name = "Session storage";
}
