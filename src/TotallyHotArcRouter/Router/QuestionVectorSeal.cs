using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Seals one question vector with the session master key (ADR-0029). A new blob is
/// <c>THQE || nonce (12) || tag (16) || ciphertext</c>. Associated data is the table name, the row id,
/// and the embedding model, length-prefixed, so a blob cannot be moved to another row or relabeled.
/// A blob that does not start with <c>THQE</c> is a raw little-endian float32 vector written before
/// this format, and it still loads.
/// </summary>
internal static class QuestionVectorSeal
{
    /// <summary>The <c>memory_entries</c> table name bound into associated data.</summary>
    private const string TableName = "memory_entries";

    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static ReadOnlySpan<byte> Magic => "THQE"u8;

    /// <summary>A blob that is not a vector. An insert uses it until the row id is known and the real seal is written.</summary>
    internal static byte[] Placeholder => "THQE"u8.ToArray();

    /// <summary>Whether <paramref name="blob"/> uses this seal rather than raw float32.</summary>
    /// <param name="blob">The <c>embedding</c> column.</param>
    /// <returns><see langword="true"/> when the blob starts with the format marker.</returns>
    internal static bool IsSealed(ReadOnlySpan<byte> blob) =>
        blob.Length >= Magic.Length && blob.StartsWith(Magic);

    /// <summary>
    /// Seals <paramref name="vector"/> under <paramref name="key"/>. Each call uses a fresh nonce.
    /// </summary>
    /// <param name="key">The 32-byte session master key.</param>
    /// <param name="id">The <c>memory_entries</c> row id, bound into associated data.</param>
    /// <param name="embeddingModel">The BGE model id stored on the row, or null when the row has none.</param>
    /// <param name="vector">The plaintext vector. It is not written.</param>
    /// <returns>The sealed blob.</returns>
    internal static byte[] Seal(byte[] key, long id, string? embeddingModel, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(vector);
        var plain = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(src: vector, srcOffset: 0, dst: plain, dstOffset: 0, count: plain.Length);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            var cipher = new byte[plain.Length];
            using var aes = new AesGcm(key: key, tagSizeInBytes: TagLength);
            aes.Encrypt(nonce: nonce, plaintext: plain, ciphertext: cipher, tag: tag,
                associatedData: AssociatedData(id: id, embeddingModel: embeddingModel));

            var blob = new byte[Magic.Length + NonceLength + TagLength + cipher.Length];
            Magic.CopyTo(blob);
            nonce.CopyTo(blob, Magic.Length);
            tag.CopyTo(blob, Magic.Length + NonceLength);
            cipher.CopyTo(blob, Magic.Length + NonceLength + TagLength);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Opens a blob sealed for this row. A copied blob, or a blob sealed under another key, fails
    /// authentication and does not become a float vector.
    /// </summary>
    /// <param name="key">The 32-byte session master key.</param>
    /// <param name="id">The row id bound into associated data.</param>
    /// <param name="embeddingModel">The model id bound into associated data.</param>
    /// <param name="blob">The sealed blob.</param>
    /// <param name="vector">The opened vector when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when authentication succeeds.</returns>
    internal static bool TryOpen(byte[] key, long id, string? embeddingModel, ReadOnlySpan<byte> blob,
        out float[] vector)
    {
        vector = [];
        if (key.Length == 0 || !IsSealed(blob)) return false;
        var body = blob[Magic.Length..];
        if (body.Length < NonceLength + TagLength) return false;
        if ((body.Length - NonceLength - TagLength) % sizeof(float) != 0) return false;

        try
        {
            var cipherLength = body.Length - NonceLength - TagLength;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key: key, tagSizeInBytes: TagLength);
            aes.Decrypt(
                nonce: body[..NonceLength],
                ciphertext: body.Slice(start: NonceLength + TagLength, length: cipherLength),
                tag: body.Slice(start: NonceLength, length: TagLength),
                plaintext: plain,
                associatedData: AssociatedData(id: id, embeddingModel: embeddingModel));
            vector = new float[plain.Length / sizeof(float)];
            Buffer.BlockCopy(src: plain, srcOffset: 0, dst: vector, dstOffset: 0, count: plain.Length);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            vector = [];
            return false;
        }
        catch (CryptographicException)
        {
            vector = [];
            return false;
        }
    }

    /// <summary>
    /// Reads a question vector. A sealed blob opens only with <paramref name="key"/>. A raw float32
    /// blob is returned as-is, so a database from before this format keeps its neighbors. A sealed
    /// blob that does not open is not interpreted as floats.
    /// </summary>
    /// <param name="blob">The <c>embedding</c> column, or null.</param>
    /// <param name="key">The session master key, or null when none exists.</param>
    /// <param name="id">The row id.</param>
    /// <param name="embeddingModel">The model id stored on the row.</param>
    /// <returns>The plaintext vector, or null when the blob is missing or will not open.</returns>
    internal static float[]? Read(byte[]? blob, byte[]? key, long id, string? embeddingModel)
    {
        if (blob is null || blob.Length == 0) return null;
        if (IsSealed(blob))
        {
            if (key is null || !TryOpen(key: key, id: id, embeddingModel: embeddingModel, blob: blob,
                    vector: out var opened))
                return null;
            return opened;
        }

        if (blob.Length % sizeof(float) != 0) return null;
        var plain = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(src: blob, srcOffset: 0, dst: plain, dstOffset: 0, count: blob.Length);
        return plain;
    }

    /// <summary>Length-prefixed UTF-8 of the table name, the row id, and the model id.</summary>
    private static byte[] AssociatedData(long id, string? embeddingModel)
    {
        var table = Encoding.UTF8.GetBytes(TableName);
        var row = Encoding.UTF8.GetBytes(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var model = Encoding.UTF8.GetBytes(embeddingModel ?? string.Empty);
        var aad = new byte[4 + table.Length + 4 + row.Length + 4 + model.Length];
        var offset = WriteSegment(destination: aad, offset: 0, segment: table);
        offset = WriteSegment(destination: aad, offset: offset, segment: row);
        WriteSegment(destination: aad, offset: offset, segment: model);
        return aad;
    }

    /// <summary>Writes a little-endian length and then <paramref name="segment"/>.</summary>
    private static int WriteSegment(byte[] destination, int offset, ReadOnlySpan<byte> segment)
    {
        destination[offset] = (byte)segment.Length;
        destination[offset + 1] = (byte)(segment.Length >> 8);
        destination[offset + 2] = (byte)(segment.Length >> 16);
        destination[offset + 3] = (byte)(segment.Length >> 24);
        segment.CopyTo(destination.AsSpan(offset + 4));
        return offset + 4 + segment.Length;
    }
}
