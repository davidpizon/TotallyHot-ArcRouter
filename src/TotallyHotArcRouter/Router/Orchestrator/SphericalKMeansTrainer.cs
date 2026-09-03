namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Plain-C# spherical k-means over unit-normalized embeddings (docs/router/self-organizing-classification-plan.md
/// Phase T2a/T2b): cosine similarity reduces to a dot product on unit vectors, exactly as
/// <see cref="EmbeddingMemory.CosineSimilarity"/> already exploits, so this needs no external ML package -
/// the same "training and inference both in .NET" precedent <see cref="EmbeddingLogRegTrainer"/> set.
/// Sweeps a configurable range of candidate <c>k</c> values under a fixed seed and picks the one with the
/// highest silhouette score, so a chosen <c>k</c> is always explainable from the artifact's own provenance
/// string rather than arbitrary.
/// </summary>
public static class SphericalKMeansTrainer
{
    /// <summary>
    /// Sweeps <c>k</c> across <c>[minK, maxK]</c> (clamped to the sample count), runs spherical k-means at
    /// each value under the given seed, and returns the clustering with the highest approximate silhouette
    /// score.
    /// </summary>
    /// <param name="embeddings">The embeddings to cluster. Every vector must have the same length.</param>
    /// <param name="weights">
    /// Per-sample weights for centroid averaging, parallel to <paramref name="embeddings"/>. All-1.0 if
    /// omitted.
    /// </param>
    /// <param name="minK">The smallest cluster count to try.</param>
    /// <param name="maxK">The largest cluster count to try.</param>
    /// <param name="seed">The deterministic seed for centroid initialization, so a chosen <c>k</c> is reproducible.</param>
    /// <param name="maxIterations">The maximum number of Lloyd-style refinement passes per <c>k</c>.</param>
    /// <returns>The winning clustering and its selection provenance.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="embeddings"/> is empty, a vector length disagrees with the first,
    /// or <paramref name="minK"/>/<paramref name="maxK"/> are inconsistent.
    /// </exception>
    public static TrainResult Train(
        IReadOnlyList<float[]> embeddings,
        IReadOnlyList<double>? weights = null,
        int minK = 6,
        int maxK = 24,
        int seed = 42,
        int maxIterations = 50)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: minK, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: maxK, other: minK);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: maxIterations, 0);

        if (embeddings.Count == 0)
            throw new ArgumentException(message: "At least one embedding is required.", paramName: nameof(embeddings));

        if (weights is not null && weights.Count != embeddings.Count)
            throw new ArgumentException(
                message: $"weights has {weights.Count} entr(y/ies), expected {embeddings.Count} to match embeddings.",
                paramName: nameof(weights));

        var dimension = embeddings[0].Length;
        for (var i = 0; i < embeddings.Count; i++)
            if (embeddings[i].Length != dimension)
                throw new ArgumentException(
                    message:
                    $"Embedding at index {i} has length {embeddings[i].Length}, expected {dimension} to match the first.",
                    paramName: nameof(embeddings));

        var unitEmbeddings = embeddings.Select(Normalize).ToArray();
        var effectiveWeights = weights ?? Enumerable.Repeat(1.0, count: embeddings.Count).ToArray();

        // A k above the sample count cannot produce distinct non-empty clusters - cap the sweep to what's
        // achievable rather than throwing, since callers already gate on a minimum row count upstream.
        var effectiveMaxK = Math.Min(val1: maxK, val2: embeddings.Count);
        var effectiveMinK = Math.Min(val1: minK, val2: effectiveMaxK);

        TrainResult? best = null;
        var sweepLog = new List<string>();

        for (var k = effectiveMinK; k <= effectiveMaxK; k++)
        {
            var (centroids, assignments) = RunOnce(embeddings: unitEmbeddings, weights: effectiveWeights, k: k,
                seed: seed, maxIterations: maxIterations);
            var silhouette = ComputeApproximateSilhouette(embeddings: unitEmbeddings, centroids: centroids,
                assignments: assignments);
            sweepLog.Add($"k={k}:{silhouette:F4}");

            if (best is null || silhouette > best.SilhouetteScore)
                best = new TrainResult(Centroids: centroids, Assignments: assignments, ChosenK: k,
                    SilhouetteScore: silhouette, KSelectionProvenance: string.Empty);
        }

        var provenance =
            $"Swept k in [{effectiveMinK}, {effectiveMaxK}] under seed {seed}; chose k={best!.ChosenK} " +
            $"(silhouette={best.SilhouetteScore:F4}). Per-k silhouette: {string.Join(separator: ", ", values: sweepLog)}.";

        return best with { KSelectionProvenance = provenance };
    }

    /// <summary>
    /// Runs one spherical k-means fit at a fixed <c>k</c>: deterministic k-means++ initialization, then
    /// Lloyd-style refinement (assign to nearest centroid by cosine similarity, recompute each centroid as
    /// the weighted mean of its members re-normalized to unit length) until assignments stop changing or
    /// <paramref name="maxIterations"/> is reached.
    /// </summary>
    private static (float[][] Centroids, int[] Assignments) RunOnce(
        float[][] embeddings, IReadOnlyList<double> weights, int k, int seed, int maxIterations)
    {
        var random = new Random(seed);
        var centroids = InitializeKMeansPlusPlus(embeddings: embeddings, k: k, random: random);
        var assignments = new int[embeddings.Length];

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var changed = false;
            for (var i = 0; i < embeddings.Length; i++)
            {
                var nearest = NearestCentroid(embedding: embeddings[i], centroids: centroids);
                if (assignments[i] != nearest)
                {
                    assignments[i] = nearest;
                    changed = true;
                }
            }

            var recomputed = RecomputeCentroids(embeddings: embeddings, weights: weights, assignments: assignments,
                previousCentroids: centroids, random: random);
            centroids = recomputed;

            if (!changed && iteration > 0) break;
        }

        return (centroids, assignments);
    }

    /// <summary>
    /// Seeds <paramref name="k"/> centroids by k-means++: the first is a uniformly random sample, each
    /// subsequent one is drawn with probability proportional to its squared cosine distance from the
    /// nearest centroid chosen so far - spreads the initial centroids apart rather than clumping them,
    /// under a fully deterministic <paramref name="random"/> for reproducibility.
    /// </summary>
    private static float[][] InitializeKMeansPlusPlus(float[][] embeddings, int k, Random random)
    {
        var centroids = new List<float[]>(k) { (float[])embeddings[random.Next(embeddings.Length)].Clone() };

        while (centroids.Count < k)
        {
            var distances = new double[embeddings.Length];
            var total = 0.0;
            for (var i = 0; i < embeddings.Length; i++)
            {
                var nearest = NearestCentroid(embedding: embeddings[i], centroids: [.. centroids]);
                var distance = 1.0 - Dot(left: embeddings[i], right: centroids[nearest]);
                distances[i] = distance * distance;
                total += distances[i];
            }

            if (total <= 0)
            {
                // Every remaining point coincides with an existing centroid - fall back to uniform pick
                // rather than dividing by zero.
                centroids.Add((float[])embeddings[random.Next(embeddings.Length)].Clone());
                continue;
            }

            var target = random.NextDouble() * total;
            var cumulative = 0.0;
            var chosen = embeddings.Length - 1;
            for (var i = 0; i < embeddings.Length; i++)
            {
                cumulative += distances[i];
                if (cumulative >= target)
                {
                    chosen = i;
                    break;
                }
            }

            centroids.Add((float[])embeddings[chosen].Clone());
        }

        return [.. centroids];
    }

    /// <summary>
    /// Recomputes each centroid as the weighted mean of its assigned members, re-normalized to unit length.
    /// A centroid with no members (a degenerate assignment) is re-seeded to the point farthest (by cosine
    /// distance) from its own nearest centroid, so an empty cluster from one iteration can recover on the
    /// next rather than sticking at zero membership for the rest of the run.
    /// </summary>
    private static float[][] RecomputeCentroids(
        float[][] embeddings, IReadOnlyList<double> weights, int[] assignments, float[][] previousCentroids,
        Random random)
    {
        var dimension = embeddings[0].Length;
        var k = previousCentroids.Length;
        var sums = new double[k][];
        var totalWeights = new double[k];
        for (var c = 0; c < k; c++) sums[c] = new double[dimension];

        for (var i = 0; i < embeddings.Length; i++)
        {
            var cluster = assignments[i];
            var weight = weights[i];
            totalWeights[cluster] += weight;
            for (var d = 0; d < dimension; d++) sums[cluster][d] += weight * embeddings[i][d];
        }

        var centroids = new float[k][];
        for (var c = 0; c < k; c++)
        {
            if (totalWeights[c] <= 0)
            {
                centroids[c] = (float[])embeddings[random.Next(embeddings.Length)].Clone();
                continue;
            }

            var vector = new float[dimension];
            for (var d = 0; d < dimension; d++) vector[d] = (float)(sums[c][d] / totalWeights[c]);

            centroids[c] = Normalize(vector);
        }

        return centroids;
    }

    /// <summary>
    /// Returns the index of the centroid with the highest cosine similarity (dot product on unit vectors) to
    /// <paramref name="embedding"/>.
    /// </summary>
    private static int NearestCentroid(float[] embedding, float[][] centroids)
    {
        var best = 0;
        var bestSimilarity = double.NegativeInfinity;
        for (var c = 0; c < centroids.Length; c++)
        {
            var similarity = Dot(left: embedding, right: centroids[c]);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// Computes a centroid-based approximation of the silhouette score: for each point,
    /// <c>a</c> is its cosine distance to its own assigned centroid and <c>b</c> is the smallest cosine
    /// distance to any other centroid, and the score is the mean of <c>(b - a) / max(a, b)</c>. This is
    /// O(n * k) rather than the O(n²) exact pairwise silhouette, which the plan permits when exact
    /// silhouette proves too slow at scale (docs/router/self-organizing-classification-plan.md Phase T2b).
    /// </summary>
    private static double ComputeApproximateSilhouette(float[][] embeddings, float[][] centroids, int[] assignments)
    {
        if (centroids.Length < 2)
            // Silhouette is undefined with only one cluster (no "other" cluster to compare against) -
            // report 0 so a single-cluster k never wins the sweep purely by escaping an undefined penalty.
            return 0.0;

        var total = 0.0;
        for (var i = 0; i < embeddings.Length; i++)
        {
            var own = assignments[i];
            var a = 1.0 - Dot(left: embeddings[i], right: centroids[own]);

            var b = double.PositiveInfinity;
            for (var c = 0; c < centroids.Length; c++)
            {
                if (c == own) continue;

                var distance = 1.0 - Dot(left: embeddings[i], right: centroids[c]);
                if (distance < b) b = distance;
            }

            var denominator = Math.Max(val1: a, val2: b);
            total += denominator <= 0 ? 0.0 : (b - a) / denominator;
        }

        return total / embeddings.Length;
    }

    /// <summary>
    /// Computes the dot product of two equal-length vectors, which is the cosine similarity for unit-normalized
    /// inputs.
    /// </summary>
    private static double Dot(float[] left, float[] right)
    {
        double sum = 0;
        for (var i = 0; i < left.Length; i++) sum += (double)left[i] * right[i];

        return sum;
    }

    /// <summary>Returns a unit-length copy of <paramref name="vector"/>, or a copy unchanged if it is already a zero vector.</summary>
    private static float[] Normalize(float[] vector)
    {
        double magnitude = 0;
        foreach (var component in vector) magnitude += (double)component * component;

        magnitude = Math.Sqrt(magnitude);
        if (magnitude <= 0) return (float[])vector.Clone();

        var normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) normalized[i] = (float)(vector[i] / magnitude);

        return normalized;
    }

    /// <summary>The outcome of a k-sweep: the winning clustering plus how it was chosen.</summary>
    /// <param name="Centroids">The winning <c>k</c>'s unit-normalized centroids, one per cluster.</param>
    /// <param name="Assignments">The winning cluster index for each input sample, in input order.</param>
    /// <param name="ChosenK">The number of clusters selected by the sweep.</param>
    /// <param name="SilhouetteScore">The winning clustering's mean approximate silhouette score.</param>
    /// <param name="KSelectionProvenance">
    /// A human-readable record of every <c>k</c> tried and its score, and which won -
    /// stamped onto the trained artifact so the choice is always explainable.
    /// </param>
    public sealed record TrainResult(
        IReadOnlyList<float[]> Centroids,
        IReadOnlyList<int> Assignments,
        int ChosenK,
        double SilhouetteScore,
        string KSelectionProvenance);
}