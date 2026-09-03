namespace TotallyHot.ArcRouter.Checksums;

/// <summary>
/// Which hash a published Hugging Face tree entry's oid represents, so a downloaded file is verified
/// with the matching algorithm rather than always assuming a git blob SHA-1.
/// </summary>
public enum PublishedChecksumAlgorithm
{
    /// <summary>
    /// <c>SHA1("blob " + length + "\0" + bytes)</c> - git's blob hash, published for a file the tree API
    /// did not resolve through Git LFS. See <see cref="GitBlobHash"/>.
    /// </summary>
    GitBlobSha1,

    /// <summary>
    /// Plain SHA-256 of the file's raw bytes - what Hugging Face publishes as a tree entry's <c>lfs.oid</c>
    /// for a Git LFS-tracked file. Such an entry's top-level <c>oid</c> is instead the git blob SHA-1 of
    /// the small LFS *pointer* text file, not of the real content, and must never be compared against a
    /// hash of the downloaded (LFS-resolved) bytes - doing so fails every LFS-tracked file's checksum on
    /// every sync, regardless of whether the download is actually intact. See <see cref="ContentSha256Hash"/>.
    /// </summary>
    LfsSha256
}