using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="SphericalKMeansTrainer.Train"/> - the spherical k-means fit plus its silhouette-scored
/// k-sweep (docs/router/self-organizing-classification-plan.md Phase T2a/T2b).
/// </summary>
public class SphericalKMeansTrainerTests
{
    [Fact]
    public void Train_TwoWellSeparatedClusters_RecoversThePlantedStructureDeterministically()
    {
        // Two tight clusters of unit vectors near opposite corners of a 3-dimensional space - far enough
        // apart that any reasonable k=2 fit must separate them.
        var embeddings = new List<float[]>();
        for (var i = 0; i < 20; i++)
        {
            embeddings.Add(Jitter([1, 0, 0], i));
        }
        for (var i = 0; i < 20; i++)
        {
            embeddings.Add(Jitter([0, 1, 0], i));
        }

        var result = SphericalKMeansTrainer.Train(embeddings, minK: 2, maxK: 2, seed: 42);

        Assert.Equal(2, result.ChosenK);
        Assert.Equal(2, result.Centroids.Count);

        // Every member of the first planted group shares an assignment, every member of the second shares
        // a (different) assignment - the fit found the two planted groups, regardless of which cluster
        // index each landed on.
        var firstGroupAssignments = result.Assignments.Take(20).Distinct().ToList();
        var secondGroupAssignments = result.Assignments.Skip(20).Take(20).Distinct().ToList();
        Assert.Single(firstGroupAssignments);
        Assert.Single(secondGroupAssignments);
        Assert.NotEqual(firstGroupAssignments[0], secondGroupAssignments[0]);
    }

    [Fact]
    public void Train_SameInputsAndSeed_ProducesIdenticalAssignments()
    {
        var embeddings = new List<float[]>();
        for (var i = 0; i < 12; i++)
        {
            embeddings.Add(Jitter([1, 0, 0], i));
            embeddings.Add(Jitter([0, 1, 0], i));
            embeddings.Add(Jitter([0, 0, 1], i));
        }

        var first = SphericalKMeansTrainer.Train(embeddings, minK: 3, maxK: 3, seed: 7);
        var second = SphericalKMeansTrainer.Train(embeddings, minK: 3, maxK: 3, seed: 7);

        Assert.Equal(first.Assignments, second.Assignments);
        Assert.Equal(first.KSelectionProvenance, second.KSelectionProvenance);
    }

    [Fact]
    public void Train_KSweepAcrossThreePlantedClusters_SilhouettePrefersThreeOverTwoOrFive()
    {
        var embeddings = new List<float[]>();
        for (var i = 0; i < 15; i++)
        {
            embeddings.Add(Jitter([1, 0, 0], i));
            embeddings.Add(Jitter([0, 1, 0], i));
            embeddings.Add(Jitter([0, 0, 1], i));
        }

        var result = SphericalKMeansTrainer.Train(embeddings, minK: 2, maxK: 5, seed: 42);

        Assert.Equal(3, result.ChosenK);
        Assert.Contains("Swept k in [2, 5]", result.KSelectionProvenance);
    }

    [Fact]
    public void Train_KAboveSampleCount_CapsTheSweepInsteadOfThrowing()
    {
        float[][] embeddings = [[1, 0], [0, 1], [1, 1]];

        var result = SphericalKMeansTrainer.Train(embeddings, minK: 2, maxK: 100, seed: 1);

        Assert.True(result.ChosenK <= embeddings.Length);
    }

    [Fact]
    public void Train_EmptyEmbeddings_Throws()
    {
        Assert.Throws<ArgumentException>(() => SphericalKMeansTrainer.Train(Array.Empty<float[]>()));
    }

    [Fact]
    public void Train_MismatchedEmbeddingLength_Throws()
    {
        float[][] embeddings = [[1, 0, 0], [1, 0]];

        Assert.Throws<ArgumentException>(() => SphericalKMeansTrainer.Train(embeddings, minK: 1, maxK: 1));
    }

    [Fact]
    public void Train_WeightsCountMismatch_Throws()
    {
        float[][] embeddings = [[1, 0], [0, 1]];

        Assert.Throws<ArgumentException>(() => SphericalKMeansTrainer.Train(embeddings, weights: [1.0], minK: 1, maxK: 1));
    }

    [Fact]
    public void Train_HeavilyWeightedGroup_PullsItsCentroidTowardTheHeavyPoints()
    {
        // A tight, heavily-weighted group at [1,0] and a single lightly-weighted outlier point far from it -
        // the weighted mean centroid for that cluster should sit close to the heavy group, not the outlier.
        float[][] embeddings =
        [
            [1, 0], [1, 0], [1, 0],
            [0, 1],
        ];
        double[] weights = [10.0, 10.0, 10.0, 0.01];

        var result = SphericalKMeansTrainer.Train(embeddings, weights, minK: 1, maxK: 1, seed: 1);

        var centroid = result.Centroids[0];
        Assert.True(centroid[0] > centroid[1]);
    }

    /// <summary>Perturbs a unit vector slightly (deterministically, via <paramref name="seedOffset"/>) and re-normalizes, so a "cluster" of jittered copies is tight but not degenerately identical.</summary>
    private static float[] Jitter(float[] baseVector, int seedOffset)
    {
        var random = new Random(1000 + seedOffset);
        var jittered = new float[baseVector.Length];
        for (var i = 0; i < baseVector.Length; i++)
        {
            jittered[i] = baseVector[i] + ((float)(random.NextDouble() - 0.5) * 0.05f);
        }

        var magnitude = MathF.Sqrt(jittered.Sum(v => v * v));
        for (var i = 0; i < jittered.Length; i++)
        {
            jittered[i] /= magnitude;
        }

        return jittered;
    }
}
