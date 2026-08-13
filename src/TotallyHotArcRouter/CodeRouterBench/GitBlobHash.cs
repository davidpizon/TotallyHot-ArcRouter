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
    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var buffer = new byte[header.Length + content.Length];
        header.CopyTo(buffer, 0);
        content.CopyTo(buffer, header.Length);

        var hash = SHA1.HashData(buffer);
        return Convert.ToHexStringLower(hash);
    }
}
