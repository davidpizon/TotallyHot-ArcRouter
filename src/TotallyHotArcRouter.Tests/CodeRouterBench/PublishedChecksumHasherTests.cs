using System.Text;
using TotallyHot.ArcRouter.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>Covers <see cref="PublishedChecksumHasher"/>'s dispatch between the two hash algorithms.</summary>
public class PublishedChecksumHasherTests
{
    [Fact]
    public void Compute_GitBlobSha1_MatchesGitBlobHash()
    {
        var content = Encoding.UTF8.GetBytes("hello\n");
        using var stream = new MemoryStream(content);

        var hash = PublishedChecksumHasher.Compute(
            stream, content.LongLength, PublishedChecksumAlgorithm.GitBlobSha1, TestContext.Current.CancellationToken);

        Assert.Equal(GitBlobHash.Compute(content), hash);
    }

    [Fact]
    public void Compute_LfsSha256_MatchesContentSha256Hash()
    {
        var content = Encoding.UTF8.GetBytes("hello\n");
        using var stream = new MemoryStream(content);

        var hash = PublishedChecksumHasher.Compute(
            stream, content.LongLength, PublishedChecksumAlgorithm.LfsSha256, TestContext.Current.CancellationToken);

        Assert.Equal(ContentSha256Hash.Compute(content), hash);
    }

    [Fact]
    public void Compute_TheTwoAlgorithms_ProduceDifferentHashesForTheSameContent()
    {
        // The bug this type exists to prevent: an LFS-tracked file's real content hash must never be
        // compared against a git blob SHA-1 (or vice versa) - the two must diverge for the same bytes.
        var content = Encoding.UTF8.GetBytes("hello\n");

        var gitBlobSha1 = PublishedChecksumHasher.Compute(
            new MemoryStream(content), content.LongLength, PublishedChecksumAlgorithm.GitBlobSha1, TestContext.Current.CancellationToken);
        var lfsSha256 = PublishedChecksumHasher.Compute(
            new MemoryStream(content), content.LongLength, PublishedChecksumAlgorithm.LfsSha256, TestContext.Current.CancellationToken);

        Assert.NotEqual(gitBlobSha1, lfsSha256);
    }
}
