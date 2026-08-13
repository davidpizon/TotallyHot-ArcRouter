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
#pragma warning restore CA5350
}
