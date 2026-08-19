using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="EmbeddingLogRegTrainer.Train"/>'s per-model regression-head fit
/// (docs/router/live-feedback-learning-plan.md Phase 4) against small, synthetic, clearly-separable
/// samples - not the real corpus (see <see cref="Tests.CodeRouterBench.LogRegTrainerReconciliationTests"/>-style
/// integration coverage for that, once wired).
/// </summary>
public class EmbeddingLogRegTrainerTests
{
    [Fact]
    public void Train_TwoSeparableModelClusters_EachHeadPredictsHighestForItsOwnCluster()
    {
        var samples = new List<LogRegTrainingSample>();
        for (var i = 0; i < 15; i++)
        {
            samples.Add(new LogRegTrainingSample(UnitVector(1, 0), "model-a", 1.0, Weight: 1.0));
            samples.Add(new LogRegTrainingSample(UnitVector(1, 0), "model-b", 0.0, Weight: 1.0));
            samples.Add(new LogRegTrainingSample(UnitVector(0, 1), "model-a", 0.0, Weight: 1.0));
            samples.Add(new LogRegTrainingSample(UnitVector(0, 1), "model-b", 1.0, Weight: 1.0));
        }

        var artifact = EmbeddingLogRegTrainer.Train(samples, embeddingDimension: 2, trainedFrom: "test", bootstrapTaskCount: 30, memoryEntryCount: 0);

        Assert.Equal(2, artifact.EmbeddingDimension);
        Assert.Contains("model-a", artifact.ClassWeights.Keys);
        Assert.Contains("model-b", artifact.ClassWeights.Keys);

        Assert.True(Predict(artifact, "model-a", UnitVector(1, 0)) > Predict(artifact, "model-b", UnitVector(1, 0)));
        Assert.True(Predict(artifact, "model-b", UnitVector(0, 1)) > Predict(artifact, "model-a", UnitVector(0, 1)));
    }

    [Fact]
    public void Train_WeightedSamples_HeavierWeightDominatesTheFit()
    {
        // A single heavily-weighted low-score sample should pull the head's prediction down despite many
        // more unweighted high-score samples, proving Weight actually scales each sample's gradient
        // contribution - the mechanism docs/router/live-feedback-learning-plan.md Phase 4b's live-vs-
        // bootstrap blend relies on.
        var samples = new List<LogRegTrainingSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new LogRegTrainingSample(UnitVector(1, 0), "model-a", 1.0, Weight: 1.0));
        }
        samples.Add(new LogRegTrainingSample(UnitVector(1, 0), "model-a", 0.0, Weight: 100.0));

        var artifact = EmbeddingLogRegTrainer.Train(samples, embeddingDimension: 2, trainedFrom: "test", bootstrapTaskCount: 21, memoryEntryCount: 0, epochs: 300, learningRate: 0.2);

        Assert.True(Predict(artifact, "model-a", UnitVector(1, 0)) < 0.5);
    }

    [Fact]
    public void Train_EmptySamples_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            EmbeddingLogRegTrainer.Train([], embeddingDimension: 2, trainedFrom: "test", bootstrapTaskCount: 0, memoryEntryCount: 0));
    }

    [Fact]
    public void Train_MismatchedEmbeddingLength_Throws()
    {
        var samples = new List<LogRegTrainingSample>
        {
            new(UnitVector(1, 0, 0), "model-a", 1.0, Weight: 1.0),
        };

        Assert.Throws<ArgumentException>(() =>
            EmbeddingLogRegTrainer.Train(samples, embeddingDimension: 2, trainedFrom: "test", bootstrapTaskCount: 0, memoryEntryCount: 0));
    }

    [Fact]
    public void Train_ProducesArtifactValidatorAccepts()
    {
        var samples = new List<LogRegTrainingSample>
        {
            new(UnitVector(1, 0), "model-a", 0.8, Weight: 1.0),
            new(UnitVector(0, 1), "model-b", 0.6, Weight: 1.0),
        };

        var artifact = EmbeddingLogRegTrainer.Train(samples, embeddingDimension: 2, trainedFrom: "test", bootstrapTaskCount: 2, memoryEntryCount: 0);

        // Must not throw - every finite weight of the expected length is a validity requirement
        // EmbeddingLogRegModelArtifactSerializer.Validate enforces before LogRegVoter would ever load it.
        EmbeddingLogRegModelArtifactSerializer.Validate(artifact);
    }

    private static double Predict(EmbeddingLogRegModelArtifact artifact, string model, float[] embedding)
    {
        var weights = artifact.ClassWeights[model];
        var score = weights[0];
        for (var i = 0; i < embedding.Length; i++)
        {
            score += weights[i + 1] * embedding[i];
        }

        return score;
    }

    private static float[] UnitVector(float x, float y)
    {
        var length = MathF.Sqrt((x * x) + (y * y));
        return [x / length, y / length];
    }

    private static float[] UnitVector(float x, float y, float z)
    {
        var length = MathF.Sqrt((x * x) + (y * y) + (z * z));
        return [x / length, y / length, z / length];
    }
}
