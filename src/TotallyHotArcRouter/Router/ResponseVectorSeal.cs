using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Seals one response vector with AES-256-GCM. The stored blob is
/// <c>nonce (12) || tag (16) || ciphertext</c>. Associated data is the table name, the correlation id,
/// and the BGE model id, length-prefixed so one field cannot be slid into another.
/// </summary>
internal static class ResponseVectorSeal
{
    /// <summary>The <c>response_vectors</c> table name bound into associated data.</summary>
    private const string TableName = "response_vectors";

    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>The name of the vector key in <c>secrets.dat</c>. It is not a session master key.</summary>
    internal const string SecretName = "response-vector-key";

    /// <summary>
    /// Seals <paramref name="vector"/> under <paramref name="key"/>. Each call uses a fresh nonce, so two
    /// seals of the same vector differ.
    /// </summary>
    /// <param name="key">The 256-bit vector key.</param>
    /// <param name="correlationId">The row's correlation id, bound into associated data.</param>
    /// <param name="modelId">The BGE model id, bound into associated data.</param>
    /// <param name="vector">The little-endian plaintext vector. It is not written.</param>
    /// <returns>The sealed blob.</returns>
    internal static byte[] Seal(byte[] key, string correlationId, string modelId, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(vector);
        return SealWith(key: key, vector: vector,
            associatedData: AssociatedData(correlationId: correlationId, modelId: modelId));
    }

    /// <summary>
    /// Opens a blob sealed for this correlation id and model id. A copied blob, or a relabeled model id,
    /// fails authentication.
    /// </summary>
    /// <param name="key">The 256-bit vector key.</param>
    /// <param name="correlationId">The row's correlation id.</param>
    /// <param name="modelId">The BGE model id stored on the row.</param>
    /// <param name="blob">The sealed blob.</param>
    /// <param name="vector">The opened vector when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when authentication succeeds.</returns>
    internal static bool TryOpen(byte[] key, string correlationId, string modelId, ReadOnlySpan<byte> blob,
        out float[] vector)
    {
        return TryOpenWith(key: key, blob: blob, vector: out vector,
            associatedData: AssociatedData(correlationId: correlationId, modelId: modelId));
    }

    /// <summary>
    /// Seals a fingerprint-reservoir question vector. The blob layout matches <see cref="Seal"/>, but the
    /// associated data starts with <c>grader_fingerprint_reservoir</c> so a <c>response_vectors</c> blob
    /// cannot authenticate as a reservoir entry, and one artifact's blob cannot authenticate as another's.
    /// </summary>
    /// <param name="key">The 256-bit vector key from <see cref="ResponseVectorKeyHolder"/>.</param>
    /// <param name="artifactId">The grader-epoch artifact this reservoir belongs to.</param>
    /// <param name="correlationId">The training row the vector was copied from.</param>
    /// <param name="modelId">The BGE model id.</param>
    /// <param name="vector">The plaintext question vector. It is not written.</param>
    /// <returns>The sealed blob.</returns>
    internal static byte[] SealReservoir(byte[] key, string artifactId, string correlationId, string modelId,
        float[] vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(vector);
        return SealWith(key: key, vector: vector,
            associatedData: ReservoirAssociatedData(artifactId: artifactId, correlationId: correlationId,
                modelId: modelId));
    }

    /// <summary>
    /// Opens a blob sealed by <see cref="SealReservoir"/>. A copied response-vector blob, or a blob sealed
    /// for another artifact, fails authentication and does not become a zero vector.
    /// </summary>
    /// <param name="key">The 256-bit vector key.</param>
    /// <param name="artifactId">The artifact id bound into associated data.</param>
    /// <param name="correlationId">The source correlation id bound into associated data.</param>
    /// <param name="modelId">The BGE model id bound into associated data.</param>
    /// <param name="blob">The sealed blob.</param>
    /// <param name="vector">The opened vector when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when authentication succeeds.</returns>
    internal static bool TryOpenReservoir(byte[] key, string artifactId, string correlationId, string modelId,
        ReadOnlySpan<byte> blob, out float[] vector)
    {
        vector = [];
        if (string.IsNullOrWhiteSpace(artifactId) || string.IsNullOrWhiteSpace(correlationId) ||
            string.IsNullOrWhiteSpace(modelId))
            return false;

        return TryOpenWith(key: key, blob: blob, vector: out vector,
            associatedData: ReservoirAssociatedData(artifactId: artifactId, correlationId: correlationId,
                modelId: modelId));
    }

    /// <summary>Seals <paramref name="vector"/> under <paramref name="associatedData"/>.</summary>
    private static byte[] SealWith(byte[] key, float[] vector, byte[] associatedData)
    {
        var plain = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(src: vector, srcOffset: 0, dst: plain, dstOffset: 0, count: plain.Length);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key: key, tagSizeInBytes: TagLength);
        aes.Encrypt(nonce: nonce, plaintext: plain, ciphertext: cipher, tag: tag, associatedData: associatedData);

        var blob = new byte[NonceLength + TagLength + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceLength);
        cipher.CopyTo(blob, NonceLength + TagLength);
        return blob;
    }

    /// <summary>Opens a blob, or returns false without a zero-filled vector when authentication fails.</summary>
    private static bool TryOpenWith(byte[] key, ReadOnlySpan<byte> blob, byte[] associatedData, out float[] vector)
    {
        vector = [];
        if (key.Length == 0) return false;
        if (blob.Length < NonceLength + TagLength) return false;
        if ((blob.Length - NonceLength - TagLength) % sizeof(float) != 0) return false;

        try
        {
            var cipherLength = blob.Length - NonceLength - TagLength;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key: key, tagSizeInBytes: TagLength);
            aes.Decrypt(
                nonce: blob[..NonceLength],
                ciphertext: blob.Slice(start: NonceLength + TagLength, length: cipherLength),
                tag: blob.Slice(start: NonceLength, length: TagLength),
                plaintext: plain,
                associatedData: associatedData);
            vector = new float[plain.Length / sizeof(float)];
            Buffer.BlockCopy(src: plain, srcOffset: 0, dst: vector, dstOffset: 0, count: plain.Length);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            vector = [];
            return false;
        }
    }

    /// <summary>
    /// Length-prefixed UTF-8 of the reservoir label, artifact id, correlation id, and model id.
    /// </summary>
    private static byte[] ReservoirAssociatedData(string artifactId, string correlationId, string modelId)
    {
        ReadOnlySpan<byte> label = "grader_fingerprint_reservoir"u8;
        var artifact = Encoding.UTF8.GetBytes(artifactId);
        var correlation = Encoding.UTF8.GetBytes(correlationId);
        var model = Encoding.UTF8.GetBytes(modelId);
        var aad = new byte[4 + label.Length + 4 + artifact.Length + 4 + correlation.Length + 4 + model.Length];
        var offset = WriteSegment(destination: aad, offset: 0, segment: label);
        offset = WriteSegment(destination: aad, offset: offset, segment: artifact);
        offset = WriteSegment(destination: aad, offset: offset, segment: correlation);
        WriteSegment(destination: aad, offset: offset, segment: model);
        return aad;
    }

    /// <summary>Length-prefixed UTF-8 of the table name, correlation id, and model id.</summary>
    private static byte[] AssociatedData(string correlationId, string modelId)
    {
        var table = Encoding.UTF8.GetBytes(TableName);
        var correlation = Encoding.UTF8.GetBytes(correlationId);
        var model = Encoding.UTF8.GetBytes(modelId);
        var aad = new byte[4 + table.Length + 4 + correlation.Length + 4 + model.Length];
        var offset = WriteSegment(destination: aad, offset: 0, segment: table);
        offset = WriteSegment(destination: aad, offset: offset, segment: correlation);
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
