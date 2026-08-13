using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Covers <see cref="GitBlobHash"/> against the known-good vector documented in
/// docs/router/coderouterbench-sqlite-migration-plan.md's "Checksums" section.
/// </summary>
public class GitBlobHashTests
{
    [Fact]
    public void Compute_EmptyContent_MatchesGitsWellKnownEmptyBlobHash()
    {
        // git hash-object -t blob --stdin < /dev/null => e69de29bb2d1d6434b8b29ae775ad8c2e48c5391, the
        // universally known SHA-1 of an empty git blob - a stable vector with no external dependency.
        var hash = GitBlobHash.Compute([]);

        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", hash);
    }

    [Fact]
    public void Compute_KnownContent_MatchesGitsWellKnownBlobHash()
    {
        // git hash-object -t blob --stdin <<< "hello" (a single "hello\n" line) => a well-known vector
        // used across git tooling documentation.
        var hash = GitBlobHash.Compute(Encoding.UTF8.GetBytes("hello\n"));

        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", hash);
    }

    [Fact]
    public void Compute_IsLowercaseHex()
    {
        var hash = GitBlobHash.Compute(Encoding.UTF8.GetBytes("some content"));

        Assert.Equal(40, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant(), StringComparer.Ordinal);
    }

    [Fact]
    public void Compute_DifferentContent_ProducesDifferentHashes()
    {
        var a = GitBlobHash.Compute(Encoding.UTF8.GetBytes("a"));
        var b = GitBlobHash.Compute(Encoding.UTF8.GetBytes("b"));

        Assert.NotEqual(a, b);
    }
}
