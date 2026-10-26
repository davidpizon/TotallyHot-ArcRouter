using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Fits the phase 7 head with <see cref="SdcaRegressionTrainer"/> and then discards the pipeline. The
/// artifact stores the linear weights and bias, which is why <c>FastTree</c> is not the default: it does
/// not persist that coefficient vector. L1 and L2 stay unset, which is <c>Trainers.Sdca()</c>'s default.
/// Microsoft.ML 5.0.0's parameterless overload also leaves the loss and the iteration count null, and that
/// fit diverges on <c>[0, 1]</c> labels. <see cref="SquaredLoss"/> is the loss that overload's documentation
/// names as its default, and 100 iterations is the cap that keeps the same least-squares problem finite.
/// </summary>
internal static class GraderEpochTrainer
{
    /// <summary>The only trainer name phase 7 fits. Recorded on the artifact.</summary>
    internal const string SdcaName = "Sdca";

    /// <summary>
    /// Fits one linear regression. The training table has one vector column named <c>Features</c> and
    /// one float column named <c>Label</c>, which are <c>Sdca()</c>'s default column names. The loss is
    /// <see cref="SquaredLoss"/> and the iteration cap is 100; L1 and L2 are left at the overload's default.
    /// </summary>
    /// <param name="features">One feature vector per training row, already imputed.</param>
    /// <param name="labels">The judge scores, paired with <paramref name="features"/>.</param>
    /// <param name="seed">The MLContext seed. It is not the reservoir's <see cref="Random"/>.</param>
    /// <returns>The dense weight vector and the bias.</returns>
    internal static (float[] Weights, float Bias) Fit(IReadOnlyList<float[]> features, IReadOnlyList<double> labels,
        int seed)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new InvalidOperationException("The grader epoch trainer needs one label per feature row.");

        var width = features[0].Length;
        var rows = new TrainerRow[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            if (features[i].Length != width)
                throw new InvalidOperationException("The grader epoch trainer was given uneven feature rows.");
            rows[i] = new TrainerRow { Label = (float)labels[i], Features = features[i] };
        }

        var context = new MLContext(seed: seed);
        var schema = SchemaDefinition.Create(typeof(TrainerRow));
        schema[nameof(TrainerRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, width);
        var data = context.Data.LoadFromEnumerable(rows, schema);
        var model = context.Regression.Trainers.Sdca(
            lossFunction: new SquaredLoss(),
            maximumNumberOfIterations: 100).Fit(data);
        var source = model.Model.Weights;
        if (source.Count != width)
            throw new InvalidOperationException("SDCA returned a weight vector whose length is not the feature width.");

        var weights = new float[width];
        for (var i = 0; i < width; i++)
            weights[i] = source[i];
        return (weights, model.Model.Bias);
    }

    /// <summary>One ML.NET training row. The property names are the trainer's default column names.</summary>
    public sealed class TrainerRow
    {
        /// <summary>Gets or sets the judge score.</summary>
        public float Label { get; set; }

        /// <summary>Gets or sets the schema version 1 feature vector.</summary>
        public float[] Features { get; set; } = [];
    }
}
