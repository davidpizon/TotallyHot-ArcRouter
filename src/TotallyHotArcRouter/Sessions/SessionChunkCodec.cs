using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>Receives one chunk of a body's compressed bytes.</summary>
/// <param name="chunk">The chunk's bytes; valid only for the duration of the call.</param>
/// <param name="final">Whether this is the body's last chunk.</param>
internal delegate void ChunkSink(ReadOnlySpan<byte> chunk, bool final);

/// <summary>Supplies a body's chunks, in order, to a sink; the last call passes <c>final: true</c>.</summary>
/// <param name="sink">Where each chunk goes.</param>
internal delegate void ChunkProducer(ChunkSink sink);

/// <summary>
/// Reads and writes the sealed chunk records that make up a body, in the session file and in a capture spool
/// alike. A record is <c>final(1) | nonce(12) | tag(16) | cipherLen(4) | cipher</c>. Each is sealed with
/// AES-GCM over data that names the body it belongs to, its index within that body, and whether it is the
/// last one, so a dropped, reordered, repeated or relabelled chunk fails authentication and a body that lost
/// its tail is never mistaken for a complete one.
/// </summary>
internal static class SessionChunkCodec
{
    /// <summary>The most compressed bytes one chunk carries before the next chunk starts.</summary>
    internal const int ChunkBytes = 64 * 1024;

    /// <summary>The bytes a record adds around its ciphertext.</summary>
    internal const int RecordOverhead = 1 + SessionKeyMaterial.NonceLengthBytes + SessionKeyMaterial.TagLengthBytes + sizeof(uint);

    /// <summary>The largest ciphertext a reader accepts, so a corrupt length cannot request a huge allocation.</summary>
    private const int MaxCipherBytes = 1024 * 1024;

    /// <summary>
    /// Seals one chunk and writes its record with a single call.
    /// </summary>
    /// <param name="stream">Where the record goes.</param>
    /// <param name="aes">The cipher for the key that seals this body.</param>
    /// <param name="bodyAad">Data identifying the body, authenticated with every chunk of it.</param>
    /// <param name="chunkIndex">Zero-based position of the chunk within its body.</param>
    /// <param name="final">Whether this is the body's last chunk.</param>
    /// <param name="plaintext">The chunk's bytes; empty only for a missing-body marker or an empty body.</param>
    internal static void WriteRecord(
        Stream stream, AesGcm aes, ReadOnlySpan<byte> bodyAad, uint chunkIndex, bool final, ReadOnlySpan<byte> plaintext)
    {
        var record = new byte[RecordOverhead + plaintext.Length];
        var cursor = record.AsSpan();
        cursor[0] = final ? (byte)1 : (byte)0;
        var nonce = cursor.Slice(1, SessionKeyMaterial.NonceLengthBytes);
        RandomNumberGenerator.Fill(nonce);
        var tag = cursor.Slice(1 + SessionKeyMaterial.NonceLengthBytes, SessionKeyMaterial.TagLengthBytes);
        var lengthAt = 1 + SessionKeyMaterial.NonceLengthBytes + SessionKeyMaterial.TagLengthBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(cursor[lengthAt..], (uint)plaintext.Length);
        aes.Encrypt(nonce, plaintext, cursor[(lengthAt + sizeof(uint))..], tag, ChunkAad(bodyAad, chunkIndex, final));
        stream.Write(record);
    }

    /// <summary>
    /// Reads, authenticates and opens the next chunk record.
    /// </summary>
    /// <param name="stream">The source, positioned at a record.</param>
    /// <param name="aes">The cipher for the key that sealed this body.</param>
    /// <param name="bodyAad">The same body data the chunk was sealed with.</param>
    /// <param name="chunkIndex">Zero-based position of the chunk within its body.</param>
    /// <param name="final">Receives whether the record says it is the body's last chunk.</param>
    /// <returns>The chunk's bytes.</returns>
    /// <exception cref="EndOfStreamException">When the stream ends inside the record.</exception>
    /// <exception cref="InvalidDataException">When the record's length is not plausible.</exception>
    /// <exception cref="CryptographicException">When authentication fails.</exception>
    internal static byte[] ReadRecord(
        Stream stream, AesGcm aes, ReadOnlySpan<byte> bodyAad, uint chunkIndex, out bool final)
    {
        Span<byte> prefix = stackalloc byte[RecordOverhead];
        stream.ReadExactly(prefix);
        final = ParseFinal(prefix[0]);
        var nonce = prefix.Slice(1, SessionKeyMaterial.NonceLengthBytes);
        var tag = prefix.Slice(1 + SessionKeyMaterial.NonceLengthBytes, SessionKeyMaterial.TagLengthBytes);
        var cipherLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix[(RecordOverhead - sizeof(uint))..]);
        if (cipherLength > MaxCipherBytes || cipherLength > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Session chunk length is not plausible.");
        }

        var ciphertext = new byte[cipherLength];
        stream.ReadExactly(ciphertext);
        var plaintext = new byte[cipherLength];
        aes.Decrypt(nonce, ciphertext, tag, plaintext, ChunkAad(bodyAad, chunkIndex, final));
        return plaintext;
    }

    /// <summary>
    /// Steps over one record without opening it, for finding where the last complete body ends.
    /// </summary>
    /// <param name="stream">The source, positioned at a record.</param>
    /// <param name="final">Receives whether the record says it is the body's last chunk.</param>
    /// <returns><see langword="false"/> when the record is cut short or implausible, which marks a torn append.</returns>
    internal static bool TrySkipRecord(Stream stream, out bool final)
    {
        final = false;
        Span<byte> prefix = stackalloc byte[RecordOverhead];
        var filled = 0;
        while (filled < prefix.Length)
        {
            var read = stream.Read(prefix[filled..]);
            if (read == 0) return false;
            filled += read;
        }

        if (prefix[0] > 1) return false;
        final = prefix[0] == 1;
        var cipherLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix[(RecordOverhead - sizeof(uint))..]);
        if (cipherLength > MaxCipherBytes || cipherLength > stream.Length - stream.Position) return false;

        stream.Seek(cipherLength, SeekOrigin.Current);
        return true;
    }

    /// <summary>
    /// Builds the data one chunk authenticates: the body data, the chunk's index and its final flag.
    /// </summary>
    private static byte[] ChunkAad(ReadOnlySpan<byte> bodyAad, uint chunkIndex, bool final)
    {
        var aad = new byte[bodyAad.Length + sizeof(uint) + 1];
        bodyAad.CopyTo(aad);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(bodyAad.Length), chunkIndex);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static bool ParseFinal(byte flag) => flag switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Session chunk has an unknown final flag."),
    };
}
