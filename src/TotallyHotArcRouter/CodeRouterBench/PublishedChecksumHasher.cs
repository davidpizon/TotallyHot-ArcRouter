namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Dispatches to <see cref="GitBlobHash"/> or <see cref="ContentSha256Hash"/> depending on a published
/// file's <see cref="PublishedChecksumAlgorithm"/>, so every caller that verifies a downloaded or cached
/// file against a Hugging Face tree entry hashes it the same way regardless of whether that entry was
/// Git LFS-tracked.
/// </summary>
public static class PublishedChecksumHasher
{
    /// <summary>
    /// Computes <paramref name="content"/>'s hash using whichever algorithm <paramref name="algorithm"/>
    /// specifies.
    /// </summary>
    /// <param name="content">A stream positioned at the start of the content to hash.</param>
    /// <param name="length">
    /// The exact byte length of <paramref name="content"/>. Only used for <see cref="PublishedChecksumAlgorithm.GitBlobSha1"/>,
    /// which needs it for git's blob header; ignored for <see cref="PublishedChecksumAlgorithm.LfsSha256"/>.
    /// </param>
    /// <param name="algorithm">Which hash <paramref name="content"/> is being verified against.</param>
    /// <param name="cancellationToken">Checked between chunks so hashing a large artifact can be cancelled promptly.</param>
    public static string Compute(Stream content, long length, PublishedChecksumAlgorithm algorithm, CancellationToken cancellationToken = default) =>
        algorithm switch
        {
            PublishedChecksumAlgorithm.LfsSha256 => ContentSha256Hash.Compute(content, cancellationToken),
            _ => GitBlobHash.Compute(content, length, cancellationToken),
        };
}
