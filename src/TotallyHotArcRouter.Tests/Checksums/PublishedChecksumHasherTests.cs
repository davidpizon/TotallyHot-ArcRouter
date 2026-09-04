using System.Text;
using TotallyHot.ArcRouter.Checksums;

namespace TotallyHot.ArcRouter.Tests.Checksums;

/// <summary>Covers <see cref="PublishedChecksumHasher"/>'s dispatch between the two hash algorithms.</summary>
public class PublishedChecksumHasherTests
{
    [Fact]
    public void Compute_GitBlobSha1_MatchesGitBlobHash()
    {
        var content = "hello\n"u8.ToArray();
        using var stream = new MemoryStream(content);

        var hash = PublishedChecksumHasher.Compute(
            content: stream, length: content.LongLength, algorithm: PublishedChecksumAlgorithm.GitBlobSha1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: GitBlobHash.Compute(content), actual: hash);
    }

    [Fact]
    public void Compute_LfsSha256_MatchesContentSha256Hash()
    {
        var content = "hello\n"u8.ToArray();
        using var stream = new MemoryStream(content);

        var hash = PublishedChecksumHasher.Compute(
            content: stream, length: content.LongLength, algorithm: PublishedChecksumAlgorithm.LfsSha256,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: ContentSha256Hash.Compute(content), actual: hash);
    }

    [Fact]
    public void Compute_TheTwoAlgorithms_ProduceDifferentHashesForTheSameContent()
    {
        // The bug this type exists to prevent: an LFS-tracked file's real content hash must never be
        // compared against a git blob SHA-1 (or vice versa) - the two must diverge for the same bytes.
        var content = "hello\n"u8.ToArray();

        var gitBlobSha1 = PublishedChecksumHasher.Compute(
            content: new MemoryStream(content), length: content.LongLength,
            algorithm: PublishedChecksumAlgorithm.GitBlobSha1,
            cancellationToken: TestContext.Current.CancellationToken);
        var lfsSha256 = PublishedChecksumHasher.Compute(
            content: new MemoryStream(content), length: content.LongLength,
            algorithm: PublishedChecksumAlgorithm.LfsSha256, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(expected: gitBlobSha1, actual: lfsSha256);
    }
}