using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Computes a plain SHA-256 of raw file bytes - what Hugging Face publishes as a Git LFS-tracked tree
/// entry's <c>lfs.oid</c>, unlike <see cref="GitBlobHash"/>'s git-specific <c>"blob &lt;len&gt;\0"</c>
/// framing, which only applies to a non-LFS entry.
/// </summary>
public static class ContentSha256Hash
{
    /// <summary>Computes the lowercase hex SHA-256 of <paramref name="content"/>.</summary>
    /// <param name="content">The raw file bytes to hash.</param>
    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of <paramref name="content"/> without buffering it into memory -
    /// the streaming counterpart to <see cref="Compute(byte[])"/> for a downloaded model artifact that can
    /// run hundreds of MB.
    /// </summary>
    /// <param name="content">A stream positioned at the start of the content to hash.</param>
    /// <param name="cancellationToken">
    /// Checked between chunks so a caller hashing a large (hundreds-of-MB) artifact can be cancelled
    /// promptly instead of only after the whole file has been read.
    /// </param>
    public static string Compute(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }
}
