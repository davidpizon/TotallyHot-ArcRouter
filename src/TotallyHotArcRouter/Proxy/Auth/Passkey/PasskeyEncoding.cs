using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Base64url and grouped-base32 helpers shared by enrollment codes, bearer tokens, and credential JSON.
/// Centralizing encoding avoids subtle padding or alphabet mistakes that would break WebAuthn or
/// constant-time comparisons.
/// </summary>
internal static class PasskeyEncoding
{
    /// <summary>Encodes <paramref name="data"/> as unpadded base64url.</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Decodes a base64url string into bytes.</summary>
    public static byte[] FromBase64Url(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Formats 128 bits of entropy as grouped RFC 4648 base32 (no padding) for enrollment codes shown to
    /// operators.
    /// </summary>
    public static string FormatEnrollmentCode(ReadOnlySpan<byte> entropy128Bits)
    {
        if (entropy128Bits.Length != 16)
            throw new ArgumentException("Enrollment codes require exactly 128 bits (16 bytes).", nameof(entropy128Bits));

        var base32 = ToBase32(entropy128Bits);
        return Group(base32, groupSize: 4, separator: '-');
    }

    /// <summary>Normalizes a user-entered enrollment code by removing separators and uppercasing.</summary>
    public static string NormalizeEnrollmentCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var sb = new StringBuilder(code.Length);
        foreach (var ch in code)
        {
            if (ch is '-' or ' ') continue;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>Hashes <paramref name="normalizedCode"/> with SHA-256 for storage.</summary>
    public static byte[] HashEnrollmentCode(string normalizedCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCode);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
    }

    private static string ToBase32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        var bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                var index = (buffer >> bitsLeft) & 0x1F;
                sb.Append(alphabet[index]);
            }
        }

        if (bitsLeft > 0)
        {
            var index = (buffer << (5 - bitsLeft)) & 0x1F;
            sb.Append(alphabet[index]);
        }

        return sb.ToString();
    }

    private static string Group(string value, int groupSize, char separator)
    {
        if (groupSize <= 0) return value;
        var sb = new StringBuilder(value.Length + value.Length / groupSize);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && i % groupSize == 0) sb.Append(separator);
            sb.Append(value[i]);
        }

        return sb.ToString();
    }
}
