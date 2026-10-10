using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Obscures secrets in a body held in memory and Brotli-compresses it, the form every stored body takes
/// before it is cut into sealed chunks. Round-trips are byte-exact, including bodies that are not valid
/// UTF-8, apart from <see cref="SecretObscurer"/> replacements (ADR-0019's only permitted mutation of stored
/// text). Bodies too large to hold go through <see cref="StreamingSecretObscurer"/> instead, which produces
/// the same bytes except where its remarks say it redacts more.
/// </summary>
public static class SessionRecordCodec
{
    /// <summary>
    /// Obscures key-shaped strings in the body, then compresses the result. Valid UTF-8 runs are obscured
    /// as text; bytes that are not valid UTF-8 are copied through untouched.
    /// </summary>
    /// <param name="plaintext">Raw body bytes as received or relayed.</param>
    /// <returns>The Brotli stream of the obscured body.</returns>
    public static byte[] ObscureAndCompress(ReadOnlySpan<byte> plaintext) => Compress(ObscureBytes(plaintext));

    /// <summary>
    /// Obscures, hashes and compresses a body in one step. The hash is of the obscured plaintext, which is
    /// the form the body is stored in, so it is what an export reads back and verifies; it is not a hash of
    /// the compressed bytes or of the raw input.
    /// </summary>
    /// <param name="plaintext">Raw body bytes as received or relayed.</param>
    /// <returns>
    /// The Brotli stream, the SHA-256 of the obscured plaintext, the obscured plaintext's length, and whether
    /// obscuring changed any byte.
    /// </returns>
    internal static (byte[] Compressed, byte[] Sha256, long Length, bool Obscured) ObscureCompressAndHash(
        ReadOnlySpan<byte> plaintext)
    {
        var obscured = ObscureBytes(plaintext);
        return (Compress(obscured), SHA256.HashData(obscured), obscured.Length, !obscured.AsSpan().SequenceEqual(plaintext));
    }

    /// <summary>
    /// Brotli-compresses an already obscured body at the fastest level.
    /// </summary>
    /// <param name="obscured">The obscured plaintext.</param>
    /// <returns>The Brotli stream.</returns>
    internal static byte[] Compress(byte[] obscured) => BrotliCompress(obscured);

    /// <summary>
    /// Reverses the compression of <see cref="ObscureAndCompress"/>. Does not reverse secret obscuring.
    /// </summary>
    /// <param name="compressed">A Brotli stream.</param>
    /// <returns>The obscured plaintext bytes.</returns>
    public static byte[] Decompress(byte[] compressed) => BrotliDecompress(compressed);

    /// <summary>
    /// Runs <see cref="SecretObscurer"/> over a body without altering any byte that is not part of a
    /// match. A fully valid UTF-8 body is obscured in one pass. Otherwise each maximal valid run is
    /// obscured on its own and every invalid byte is copied through, so decoding never substitutes
    /// U+FFFD. A secret is pure ASCII, so it can never straddle an invalid byte.
    /// </summary>
    /// <param name="input">Raw body bytes.</param>
    /// <returns>The body with matched secrets replaced and all other bytes unchanged.</returns>
    internal static byte[] ObscureBytes(ReadOnlySpan<byte> input)
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
