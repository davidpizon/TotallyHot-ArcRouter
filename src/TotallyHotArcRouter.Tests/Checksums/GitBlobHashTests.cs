using System.Text;
using TotallyHot.ArcRouter.Checksums;

namespace TotallyHot.ArcRouter.Tests.Checksums;

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

    [Fact]
    public void Compute_Stream_MatchesByteArrayOverload()
    {
        // The streaming overload exists purely so a large download doesn't have to be buffered into
        // memory first - it must produce byte-for-byte the same digest as the in-memory overload.
        var content = Encoding.UTF8.GetBytes("hello\n");
        using var stream = new MemoryStream(content);

        var streamed = GitBlobHash.Compute(stream, content.LongLength, TestContext.Current.CancellationToken);

        Assert.Equal(GitBlobHash.Compute(content), streamed);
    }

    [Fact]
    public void Compute_Stream_LongerThanInternalBufferSize_MatchesByteArrayOverload()
    {
        // Exercises the read loop across more than one internal buffer's worth of data.
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var stream = new MemoryStream(content);

        var streamed = GitBlobHash.Compute(stream, content.LongLength, TestContext.Current.CancellationToken);

        Assert.Equal(GitBlobHash.Compute(content), streamed);
    }

    [Fact]
    public void Compute_Stream_CancelledToken_ThrowsBeforeReadingWholeStream()
    {
        // A large artifact's hashing must observe cancellation between chunks rather than only after the
        // whole stream has been read.
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var stream = new MemoryStream(content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => GitBlobHash.Compute(stream, content.LongLength, cts.Token));
    }
}
