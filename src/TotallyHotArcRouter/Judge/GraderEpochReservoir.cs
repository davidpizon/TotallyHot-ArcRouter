using System.Text;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The sealed fingerprint reservoir. Question vectors only, from training rows, never from holdout
/// rows. The file is little-endian: 4 bytes magic <c>THGR</c>, a <c>uint32</c> count, then that many
/// records of a <c>uint32</c> byte length, the UTF-8 correlation id, a <c>uint32</c> byte length, and
/// the sealed blob. No plaintext floats.
/// </summary>
internal static class GraderEpochReservoir
{
    /// <summary>The file magic. A reader that sees anything else drops the reservoir.</summary>
    internal static ReadOnlySpan<byte> Magic => "THGR"u8;

    /// <summary>
    /// Vitter's Algorithm R. When <paramref name="items"/> is within <paramref name="capacity"/>, the
    /// result is that list in its existing order. Otherwise a uniform sample is drawn with a new
    /// <see cref="Random"/> seeded by <paramref name="seed"/>, created for this reservoir only.
    /// </summary>
    /// <typeparam name="T">The item type. The epoch passes training labels.</typeparam>
    /// <param name="items">The ordered training rows that have a usable question vector.</param>
    /// <param name="capacity">The cap. A non-positive cap keeps nothing.</param>
    /// <param name="seed">The configured <c>RandomSeed</c>.</param>
    /// <returns>The reservoir, in sample order.</returns>
    internal static List<T> Sample<T>(IReadOnlyList<T> items, int capacity, int seed)
    {
        if (capacity <= 0 || items.Count == 0) return [];
        if (items.Count <= capacity) return items.ToList();

        var random = new Random(seed);
        var reservoir = new List<T>(capacity);
        for (var i = 0; i < capacity; i++) reservoir.Add(items[i]);
        for (var i = capacity; i < items.Count; i++)
        {
            var draw = random.Next(i + 1);
            if (draw < capacity) reservoir[draw] = items[i];
        }

        return reservoir;
    }

    /// <summary>Writes the reservoir file. Correlation ids are the record headers; the floats are sealed.</summary>
    /// <param name="records">Sample-order records. Each blob is already sealed.</param>
    /// <returns>The file bytes.</returns>
    internal static byte[] Write(IReadOnlyList<(string CorrelationId, byte[] Blob)> records)
    {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteUInt32(stream, (uint)records.Count);
        foreach (var (correlationId, blob) in records)
        {
            var id = Encoding.UTF8.GetBytes(correlationId);
            WriteUInt32(stream, (uint)id.Length);
            stream.Write(id);
            WriteUInt32(stream, (uint)blob.Length);
            stream.Write(blob);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Reads record headers and sealed blobs. A bad magic returns no records. A truncated record drops
    /// that entry and everything after it, because the framing cannot be resynced. Nothing is replaced
    /// with a zero vector.
    /// </summary>
    /// <param name="file">The reservoir file.</param>
    /// <returns>The records that were complete.</returns>
    internal static List<(string CorrelationId, byte[] Blob)> Read(ReadOnlySpan<byte> file)
    {
        var records = new List<(string, byte[])>();
        if (file.Length < 8 || !file[..4].SequenceEqual(Magic)) return records;

        var count = BitConverter.ToUInt32(file[4..8]);
        var offset = 8;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadRecord(file, ref offset, out var correlationId, out var blob))
                break;
            records.Add((correlationId, blob));
        }

        return records;
    }

    /// <summary>
    /// The cosine floor: the element at <c>floor(percentile/100 * (n - 1))</c> of the ascending cosines.
    /// An empty sample is null, not 0. The percentile comes from configuration (default 20), not from a
    /// literal in the caller.
    /// </summary>
    /// <param name="cosines">One cosine per holdout row that had a usable question vector.</param>
    /// <param name="percentile">0–100. Values outside that range are clamped.</param>
    /// <returns>The floor, or null when there is nothing to take a percentile of.</returns>
    internal static double? Floor(IReadOnlyList<double> cosines, int percentile)
    {
        if (cosines.Count == 0) return null;

        var ordered = cosines.ToArray();
        Array.Sort(ordered);
        var fraction = Math.Clamp(percentile, 0, 100) / 100.0;
        var index = (int)Math.Floor(fraction * (ordered.Length - 1));
        return ordered[index];
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> file, ref int offset, out string correlationId,
        out byte[] blob)
    {
        correlationId = string.Empty;
        blob = [];
        if (offset + 4 > file.Length) return false;
        var idLength = BitConverter.ToUInt32(file.Slice(offset, 4));
        offset += 4;
        if (idLength > 1024 || offset + idLength + 4 > file.Length) return false;
        correlationId = Encoding.UTF8.GetString(file.Slice(offset, (int)idLength));
        offset += (int)idLength;
        var blobLength = BitConverter.ToUInt32(file.Slice(offset, 4));
        offset += 4;
        if (blobLength > 8 * 1024 * 1024 || offset + blobLength > file.Length) return false;
        blob = file.Slice(offset, (int)blobLength).ToArray();
        offset += (int)blobLength;
        return true;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
    }
}
