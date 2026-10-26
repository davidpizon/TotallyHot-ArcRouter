namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Vector checks for the phase 7 grader epoch. <see cref="Router.EmbeddingMemory.CosineSimilarity"/> is
/// not used: it returns 0 when either magnitude is 0, and this job must not treat that 0 as a cosine or
/// as a floor sample.
/// </summary>
internal static class GraderEpochVectors
{
    /// <summary>
    /// Whether <paramref name="vector"/> can be a feature or a fingerprint. A null, a wrong length, or a
    /// zero magnitude is unusable and is never passed on as a zero-filled vector of embedding length.
    /// </summary>
    /// <param name="vector">The candidate vector.</param>
    /// <param name="dimension">The only legal length, <c>EmbeddingOptions.EmbeddingDimension</c>.</param>
    /// <returns><see langword="true"/> when the vector may enter a feature slot or a cosine.</returns>
    internal static bool IsUsable(float[]? vector, int dimension)
    {
        if (vector is null || dimension <= 0 || vector.Length != dimension) return false;

        double magnitude = 0;
        foreach (var component in vector) magnitude += component * (double)component;
        return magnitude > 0;
    }

    /// <summary>
    /// Cosine of two usable vectors, or null when the lengths differ or either magnitude is 0.
    /// </summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The cosine in <c>[-1, 1]</c>, or null when it is not defined for these inputs.</returns>
    internal static double? Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return null;

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * (double)right[i];
            leftMagnitude += left[i] * (double)left[i];
            rightMagnitude += right[i] * (double)right[i];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0) return null;
        var cosine = dot / Math.Sqrt(leftMagnitude * rightMagnitude);
        return double.IsFinite(cosine) ? cosine : null;
    }

    /// <summary>
    /// Schema version 1's question-vector indicator. Indicator 1 when the receipt flag is 1, when no
    /// usable memory vector exists, or when that vector's model id is null or not the current BGE id.
    /// A NULL flag does not by itself set the indicator: NULL plus a current-model vector is indicator 0.
    /// A flag of 0 with no memory row is still indicator 1. This does not call
    /// <c>MemoryEntry.MatchesEmbeddingModel</c>, which treats a null model id as a match.
    /// </summary>
    /// <param name="flag">The receipt's <c>question_vector_missing</c>, or the live result's flag.</param>
    /// <param name="vector">The memory-entry vector, or null when the row is absent.</param>
    /// <param name="modelId">The memory row's <c>embedding_model</c>.</param>
    /// <param name="bgeModelId">The current BGE identity.</param>
    /// <param name="dimension">The only legal vector length.</param>
    /// <returns>The indicator and, when it is 0, the vector to use. A missing vector is not a zero array.</returns>
    internal static (bool Missing, float[]? Vector) ResolveQuestion(bool? flag, float[]? vector, string? modelId,
        string bgeModelId, int dimension)
    {
        var usable = vector is not null
                     && string.Equals(modelId, bgeModelId, StringComparison.Ordinal)
                     && IsUsable(vector, dimension);
        if (flag == true || !usable)
            return (Missing: true, Vector: null);

        return (Missing: false, Vector: vector);
    }
}
