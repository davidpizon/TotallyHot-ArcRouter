using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Computes a git blob SHA-1 - the content hash git (and, for these files, the Hugging Face tree API)
/// publishes for a blob, as <c>SHA1("blob " + length + "\0" + bytes)</c>. Never MD5 - see the
/// "Checksums" section of docs/router/coderouterbench-sqlite-migration-plan.md for why MD5 is not an
/// option here.
/// </summary>
public static class GitBlobHash
{
    /// <summary>
    /// Computes the lowercase hex git blob SHA-1 of <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The raw file bytes to hash.</param>
#pragma warning disable CA5350 // SHA-1 is required here to match git/Hugging Face's blob OID algorithm, not for security.
    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");

        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, outputBuffer: null, outputOffset: 0);
        sha1.TransformFinalBlock(content, 0, content.Length);

        return Convert.ToHexStringLower(sha1.Hash!);
    }

    /// <summary>
    /// Computes the lowercase hex git blob SHA-1 of <paramref name="content"/> without buffering it into
    /// memory - the streaming counterpart to <see cref="Compute(byte[])"/> for callers (a downloaded
    /// model artifact that can run hundreds of MB) where reading the whole file into a byte array first
    /// would defeat the point.
    /// </summary>
    /// <param name="content">A stream positioned at the start of the content to hash.</param>
    /// <param name="length">The exact byte length of <paramref name="content"/>, matching git's blob header.</param>
    /// <param name="cancellationToken">
    /// Checked between chunks so a caller hashing a large (hundreds-of-MB) artifact can be cancelled
    /// promptly instead of only after the whole file has been read.
    /// </param>
    public static string Compute(Stream content, long length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var header = Encoding.ASCII.GetBytes($"blob {length}\0");

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(header);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha1.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }
#pragma warning restore CA5350
}
