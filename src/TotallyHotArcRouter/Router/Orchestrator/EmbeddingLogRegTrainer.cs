namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Trains an <see cref="EmbeddingLogRegModelArtifact"/> from <see cref="LogRegTrainingSample"/> rows
/// (docs/router/live-feedback-learning-plan.md Phase 4): one per-model linear regression head - "given
/// this embedding, what score do I expect from model m?" - fit by plain, dependency-free batch gradient
/// descent, mirroring <see cref="CodeRouterBench.LogRegTrainer"/>'s "training and inference both in
/// .NET" convention (PLAN.md Phase L).
/// </summary>
/// <remarks>
/// <b>Regression, not classification.</b> Live rows only ever carry the score of the model actually
/// chosen, so a multiclass "which of M won" label is not constructible from them - the plan's own
/// reasoning for the model form. A ridge-regularized linear head per model, trained only from the rows
/// naming that model, sidesteps this: the OOD bootstrap source (full feedback, every model scored on
/// every task) and the live memory source (partial feedback, only the chosen model scored) both reduce to
/// the same <see cref="LogRegTrainingSample"/> shape, and <see cref="LogRegVoter"/> already reads the
/// result via argmax over each head's prediction plus a softmax confidence - unchanged by this trainer's
/// existence.
/// </remarks>
public static class EmbeddingLogRegTrainer
{
    /// <summary>
    /// Trains one ridge-regularized linear regression head per distinct <see cref="LogRegTrainingSample.ModelKey"/>
    /// present in <paramref name="samples"/>, by batch gradient descent over weighted squared error.
    /// </summary>
    /// <param name="samples">The training examples. Every sample must carry an embedding of length <paramref name="embeddingDimension"/>.</param>
    /// <param name="embeddingDimension">The embedding dimension every sample's vector must match.</param>
    /// <param name="trainedFrom">The provenance string to stamp on the resulting artifact - see <see cref="EmbeddingLogRegModelArtifact.TrainedFrom"/>.</param>
    /// <param name="bootstrapTaskCount">The number of OOD bootstrap tasks that contributed to <paramref name="samples"/>, for <see cref="EmbeddingLogRegModelArtifact.BootstrapTaskCount"/>.</param>
    /// <param name="memoryEntryCount">The number of live memory entries that contributed to <paramref name="samples"/>, for <see cref="EmbeddingLogRegModelArtifact.MemoryEntryCount"/>.</param>
    /// <param name="epochs">The number of full gradient-descent passes per model head.</param>
    /// <param name="learningRate">The gradient-descent step size.</param>
    /// <param name="l2Regularization">The L2 penalty applied to every non-bias weight each epoch.</param>
    /// <returns>A trained <see cref="EmbeddingLogRegModelArtifact"/> with one head per distinct model key in <paramref name="samples"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is empty, or a sample's embedding length does not match <paramref name="embeddingDimension"/>.</exception>
    public static EmbeddingLogRegModelArtifact Train(
        IReadOnlyList<LogRegTrainingSample> samples,
        int embeddingDimension,
        string trainedFrom,
        int bootstrapTaskCount,
        int memoryEntryCount,
        int epochs = 200,
        double learningRate = 0.1,
        double l2Regularization = 0.01)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainedFrom);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(embeddingDimension, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(epochs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(learningRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(l2Regularization);
        ArgumentOutOfRangeException.ThrowIfNegative(bootstrapTaskCount);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryEntryCount);

        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one training sample is required.", nameof(samples));
        }

        foreach (var sample in samples)
        {
            if (sample.Embedding.Length != embeddingDimension)
            {
                throw new ArgumentException(
                    $"Sample for model '{sample.ModelKey}' has a {sample.Embedding.Length}-dimensional embedding, " +
                    $"expected {embeddingDimension}.",
                    nameof(samples));
            }
        }

        var classWeights = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var group in samples.GroupBy(s => s.ModelKey, StringComparer.Ordinal))
        {
            classWeights[group.Key] = TrainOneHead(
                [.. group], embeddingDimension, epochs, learningRate, l2Regularization);
        }

        return new EmbeddingLogRegModelArtifact(
            embeddingDimension,
            classWeights,
            trainedFrom,
            bootstrapTaskCount,
            memoryEntryCount);
    }

    /// <summary>
    /// Trains one model's regression head by weighted batch gradient descent on squared error, L2-regularized
    /// on every weight except the bias (index 0).
    /// </summary>
    private static double[] TrainOneHead(
        IReadOnlyList<LogRegTrainingSample> samples,
        int embeddingDimension,
        int epochs,
        double learningRate,
        double l2Regularization)
    {
        var weights = new double[embeddingDimension + 1];
        var totalWeight = samples.Sum(s => s.Weight);
        if (totalWeight <= 0)
        {
            // Every sample for this head carries non-positive weight (e.g. all-zero live weighting with
            // no bootstrap rows) - gradient descent below would divide by zero. An all-zero head still
            // participates in EmbeddingLogRegModelArtifactSerializer.Validate (finite values) and simply
            // predicts a flat 0 score, which argmax treats like any other tied-low candidate.
            return weights;
        }

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var gradient = new double[embeddingDimension + 1];

            foreach (var sample in samples)
            {
                var prediction = weights[0];
                for (var i = 0; i < embeddingDimension; i++)
                {
                    prediction += weights[i + 1] * sample.Embedding[i];
                }

                var error = (prediction - sample.Score) * sample.Weight;
                gradient[0] += error;
                for (var i = 0; i < embeddingDimension; i++)
                {
                    gradient[i + 1] += error * sample.Embedding[i];
                }
            }

            weights[0] -= learningRate * gradient[0] / totalWeight;
            for (var w = 1; w < weights.Length; w++)
            {
                weights[w] -= learningRate * ((gradient[w] / totalWeight) + (l2Regularization * weights[w]));
            }
        }

        return weights;
    }
}
