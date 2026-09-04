using System.Text;
using TotallyHot.ArcRouter.Checksums;

namespace TotallyHot.ArcRouter.Tests.Checksums;

/// <summary>
/// Covers <see cref="ContentSha256Hash"/> against the well-known empty-string SHA-256 vector, mirroring
/// <see cref="GitBlobHashTests"/>'s conventions.
/// </summary>
public class ContentSha256HashTests
{
    [Fact]
    public void Compute_EmptyContent_MatchesTheWellKnownEmptyStringSha256()
    {
        // The universally known SHA-256 of zero bytes (e.g. `sha256sum < /dev/null`, or Docker's empty
        // layer digest) - a stable vector with no external dependency.
        var hash = ContentSha256Hash.Compute([]);

        Assert.Equal(expected: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", actual: hash);
    }

    [Fact]
    public void Compute_IsLowercaseHex()
    {
        var hash = ContentSha256Hash.Compute("some content"u8.ToArray());

        Assert.Equal(64, actual: hash.Length);
        Assert.Equal(expected: hash, actual: hash.ToLowerInvariant(), comparer: StringComparer.Ordinal);
    }

    [Fact]
    public void Compute_DifferentContent_ProducesDifferentHashes()
    {
        var a = ContentSha256Hash.Compute("a"u8.ToArray());
        var b = ContentSha256Hash.Compute("b"u8.ToArray());

        Assert.NotEqual(expected: a, actual: b);
    }

    [Fact]
    public void Compute_UnlikeGitBlobHash_HasNoBlobHeader()
    {
        // The whole point of this type: no "blob <len>\0" framing, so it matches a plain external SHA-256
        // of the same bytes (what Hugging Face's lfs.oid actually is) rather than git's blob hash.
        var content = "hello\n"u8.ToArray();

        var hash = ContentSha256Hash.Compute(content);

        Assert.NotEqual(expected: GitBlobHash.Compute(content), actual: hash);
    }

    [Fact]
    public void Compute_Stream_MatchesByteArrayOverload()
    {
        var content = "hello\n"u8.ToArray();
        using var stream = new MemoryStream(content);

        var streamed =
            ContentSha256Hash.Compute(content: stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: ContentSha256Hash.Compute(content), actual: streamed);
    }

    [Fact]
    public void Compute_Stream_LongerThanInternalBufferSize_MatchesByteArrayOverload()
    {
        // Exercises the read loop across more than one internal buffer's worth of data.
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var stream = new MemoryStream(content);

        var streamed =
            ContentSha256Hash.Compute(content: stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: ContentSha256Hash.Compute(content), actual: streamed);
    }

    [Fact]
    public void Compute_Stream_CancelledToken_ThrowsBeforeReadingWholeStream()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var stream = new MemoryStream(content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ContentSha256Hash.Compute(content: stream, cancellationToken: cts.Token));
    }
}