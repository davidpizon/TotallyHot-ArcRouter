namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Nearest-neighbor cosine for the phase 8 confidence floor. <see cref="GraderEpochVectors.Cosine"/>
/// stays the pairwise function; a null cosine is not a neighbor and is not a floor.
/// </summary>
internal static class GraderEpochConfidence
{
    /// <summary>
    /// The largest finite cosine from <paramref name="question"/> to <paramref name="neighbors"/>,
    /// or null when every neighbor cosine is null.
    /// </summary>
    /// <param name="question">The row's question vector. The caller already rejected a missing vector.</param>
    /// <param name="neighbors">The artifact's sealed reservoir, opened.</param>
    /// <returns>The nearest cosine, or null when none is finite.</returns>
    internal static double? Nearest(float[] question, IEnumerable<float[]> neighbors)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(neighbors);

        double? best = null;
        foreach (var neighbor in neighbors)
        {
            var cosine = GraderEpochVectors.Cosine(question, neighbor);
            if (cosine is null) continue;
            if (best is null || cosine > best) best = cosine;
        }

        return best;
    }
}
