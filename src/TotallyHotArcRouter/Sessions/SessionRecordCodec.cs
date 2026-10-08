using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Obscures secrets, Brotli-compresses, and AES-GCM-encrypts a single body so session files never
/// hold plaintext. Round-trips are byte-exact for valid UTF-8 apart from <see cref="SecretObscurer"/>
/// replacements (ADR-0019's only permitted mutation of stored text); bodies that are not valid UTF-8
/// are not yet byte-exact — see <see cref="Seal"/>.
/// </summary>
public static class SessionRecordCodec
{
    /// <summary>
    /// Obscures key-shaped strings in UTF-8 body text, then compresses and encrypts the result.
    /// Invalid UTF-8 is replaced before obscuring; call sites that need strict invalid-UTF-8 fidelity
    /// must use a streaming byte obscurer (left to a later phase-1 slice).
    /// </summary>
    /// <param name="sessionKey">The 32-byte per-session AES key.</param>
    /// <param name="plaintext">Raw body bytes as received or relayed.</param>
    /// <param name="associatedData">Data authenticated with the ciphertext but not stored in it; <see cref="Open"/>
    /// must be given the same bytes.</param>
    /// <returns>Nonce, tag, and ciphertext for the framed record.</returns>
    public static EncryptedSessionPayload Seal(
        ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        SessionKeyMaterial.ValidateKeyLength(sessionKey);

        var text = Encoding.UTF8.GetString(plaintext);
        var obscured = SecretObscurer.Obscure(text);
        var obscuredBytes = Encoding.UTF8.GetBytes(obscured);
        var compressed = BrotliCompress(obscuredBytes);

        var nonce = new byte[SessionKeyMaterial.NonceLengthBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[compressed.Length];
        var tag = new byte[SessionKeyMaterial.TagLengthBytes];

        using var aes = new AesGcm(sessionKey, SessionKeyMaterial.TagLengthBytes);
        aes.Encrypt(nonce, compressed, ciphertext, tag, associatedData);

        return new EncryptedSessionPayload(nonce, tag, ciphertext);
    }

    /// <summary>
    /// Decrypts and decompresses a sealed payload. Does not reverse secret obscuring.
    /// </summary>
    /// <param name="sessionKey">The 32-byte per-session AES key.</param>
    /// <param name="payload">Nonce, tag, and ciphertext from <see cref="Seal"/>.</param>
    /// <param name="associatedData">The same associated data that was passed to <see cref="Seal"/>.</param>
    /// <returns>The obscured plaintext bytes.</returns>
    /// <exception cref="CryptographicException">When authentication fails.</exception>
    public static byte[] Open(
        ReadOnlySpan<byte> sessionKey, EncryptedSessionPayload payload, ReadOnlySpan<byte> associatedData = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        SessionKeyMaterial.ValidateKeyLength(sessionKey);

        var compressed = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(sessionKey, SessionKeyMaterial.TagLengthBytes);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, compressed, associatedData);
        return BrotliDecompress(compressed);
    }

    private static byte[] BrotliCompress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] BrotliDecompress(byte[] input)
    {
        using var inputStream = new MemoryStream(input);
        using var brotli = new BrotliStream(inputStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// The AES-GCM pieces of one sealed session-file record.
/// </summary>
/// <param name="Nonce">12-byte nonce.</param>
/// <param name="Tag">16-byte authentication tag.</param>
/// <param name="Ciphertext">Brotli ciphertext of the obscured body.</param>
public sealed record EncryptedSessionPayload(byte[] Nonce, byte[] Tag, byte[] Ciphertext);
