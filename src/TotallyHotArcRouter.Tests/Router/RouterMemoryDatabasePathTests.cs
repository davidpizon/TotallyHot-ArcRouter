using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers where <see cref="RouterMemoryDatabase"/> puts its file, which is the whole of this type's
/// behaviour that a caller cannot see from its other tests: every existing test hands it an absolute temp
/// path, so none of them exercises - or would notice a regression in - how a <em>relative</em> configured
/// path is resolved.
/// </summary>
/// <remarks>
/// A relative path used to resolve against <see cref="AppContext.BaseDirectory"/>, the install directory.
/// That made the router administrator-only to run (an ordinary account cannot write under
/// <c>%ProgramFiles%</c>, so startup died with <c>SQLite Error 14: 'unable to open database file'</c>),
/// put learned memory in a directory the MSI re-lays on every upgrade, and contradicted ADR-0014's
/// machine-wide state model. It was masked in practice because the installed service runs as
/// <c>LocalSystem</c>, which <em>can</em> write there - so the only configuration anyone routinely tested
/// was the one configuration that worked.
/// </remarks>
public class RouterMemoryDatabasePathTests
{
    [Fact]
    public void RelativePath_ResolvesUnderTheMachineSharedDirectory_NotTheInstallDirectory()
    {
        var database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = "router_embedding_memory.db" }));

        Assert.Equal(
            expected: Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(),
                path2: "router_embedding_memory.db"),
            actual: database.DatabasePath);
    }

    /// <summary>
    /// The install directory is the specific wrong answer this guards against, stated separately from the
    /// assertion above so a failure says which property broke. Skipped in the unusual case that the
    /// machine-shared directory resolves inside the base directory anyway - <see cref="AppDataPaths"/>'
    /// last-resort fallback when no machine-wide or per-user location is writable - where the two are
    /// legitimately the same place and the distinction is meaningless.
    /// </summary>
    [Fact]
    public void RelativePath_DoesNotResolveIntoTheApplicationBaseDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd('/', '\\');
        if (AppDataPaths.ResolveMachineSharedDirectory()
            .StartsWith(value: baseDirectory, comparisonType: StringComparison.OrdinalIgnoreCase))
            return;

        var database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = "router_embedding_memory.db" }));

        Assert.DoesNotContain(expectedSubstring: baseDirectory, actualString: database.DatabasePath,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An absolute configured path is still honoured unchanged - the relocation must not quietly relocate
    /// an operator who already pinned a location, and every other test in this suite depends on it.
    /// </summary>
    [Fact]
    public void AbsolutePath_IsHonouredUnchanged()
    {
        var pinned = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid().ToString("N"),
            path3: "pinned.db");

        var database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = pinned }));

        Assert.Equal(expected: pinned, actual: database.DatabasePath);
    }
}
