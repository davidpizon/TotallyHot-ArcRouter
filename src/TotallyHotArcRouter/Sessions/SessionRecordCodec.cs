using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Obscures secrets, Brotli-compresses, and AES-GCM-encrypts a single body so session files never
/// hold plaintext. Round-trips are byte-exact, including bodies that are not valid UTF-8, apart from
/// <see cref="SecretObscurer"/> replacements (ADR-0019's only permitted mutation of stored text).
/// </summary>
public static class SessionRecordCodec
{
    /// <summary>
    /// Obscures key-shaped strings in the body, then compresses and encrypts the result. Valid UTF-8
    /// runs are obscured as text; bytes that are not valid UTF-8 are copied through untouched.
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

        var obscuredBytes = ObscureBytes(plaintext);
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

    /// <summary>
    /// Runs <see cref="SecretObscurer"/> over a body without altering any byte that is not part of a
    /// match. A fully valid UTF-8 body is obscured in one pass. Otherwise each maximal valid run is
    /// obscured on its own and every invalid byte is copied through, so decoding never substitutes
    /// U+FFFD. A secret is pure ASCII, so it can never straddle an invalid byte.
    /// </summary>
    /// <param name="input">Raw body bytes.</param>
    /// <returns>The body with matched secrets replaced and all other bytes unchanged.</returns>
    private static byte[] ObscureBytes(ReadOnlySpan<byte> input)
    {
        if (Utf8.IsValid(input))
        {
            return ObscureValidRun(input);
        }

        using var output = new MemoryStream(input.Length);
        var runStart = 0;
        var position = 0;
        while (position < input.Length)
        {
            var status = Rune.DecodeFromUtf8(input[position..], out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                position += consumed;
                continue;
            }

            output.Write(ObscureValidRun(input[runStart..position]));
            var invalidLength = Math.Max(consumed, 1);
            output.Write(input.Slice(position, invalidLength));
            position += invalidLength;
            runStart = position;
        }

        output.Write(ObscureValidRun(input[runStart..]));
        return output.ToArray();
    }

    /// <summary>
    /// Obscures a span already known to be valid UTF-8 and re-encodes it.
    /// </summary>
    /// <param name="run">A valid UTF-8 byte run (possibly empty).</param>
    /// <returns>The obscured run as UTF-8 bytes.</returns>
    private static byte[] ObscureValidRun(ReadOnlySpan<byte> run) =>
        run.IsEmpty ? [] : Encoding.UTF8.GetBytes(SecretObscurer.Obscure(Encoding.UTF8.GetString(run)));

    /// <summary>
    /// Brotli-compresses a buffer at the fastest level.
    /// </summary>
    /// <param name="input">Bytes to compress.</param>
    /// <returns>The compressed bytes.</returns>
    private static byte[] BrotliCompress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(input);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reverses <see cref="BrotliCompress"/>.
    /// </summary>
    /// <param name="input">Brotli-compressed bytes.</param>
    /// <returns>The decompressed bytes.</returns>
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
