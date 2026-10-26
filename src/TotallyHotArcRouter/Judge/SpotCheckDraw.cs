using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The phase 8 spot-check draw (issue #304). A SHA-256 of the correlation id picks the bucket, so the
/// same id always draws the same way. <see cref="GraderEpochOptions.RandomSeed"/> is the reservoir and
/// bootstrap seed and is not an input here.
/// </summary>
internal static class SpotCheckDraw
{
    /// <summary>
    /// Whether <paramref name="correlationId"/> falls in the spot-check sample at <paramref name="rate"/>.
    /// Rate 0 draws nothing. Rate 1 draws every id. The default 0.05 draws buckets 0–499 of 10,000.
    /// </summary>
    /// <param name="correlationId">The grade's correlation id. Hashed as UTF-8.</param>
    /// <param name="rate">The fraction of eligible rows to draw, from 0 through 1.</param>
    /// <returns><see langword="true"/> when this id is in the sample.</returns>
    internal static bool IsDrawn(string correlationId, double rate)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        if (rate <= 0d) return false;
        if (rate >= 1d) return true;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(correlationId));
        var bucket = BinaryPrimitives.ReadUInt32LittleEndian(hash) % 10_000;
        var cutoff = (int)Math.Round(rate * 10_000d, MidpointRounding.AwayFromZero);
        return bucket < (uint)cutoff;
    }
}
