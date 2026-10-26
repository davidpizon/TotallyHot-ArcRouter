namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One phase 7 grader-epoch artifact. The JSON is not an ML.NET pipeline and not an ONNX file. Scoring
/// is the stored linear weights dotted with the schema version 1 feature vector, plus the bias, then
/// clamped to <c>[0, 1]</c>. The only status phase 7 writes on an accepted file is
/// <see cref="AcceptedShadowStatus"/>.
/// </summary>
internal sealed record GraderEpochArtifact
{
    /// <summary>Feature schema version 1. A different stored version retires the artifact.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The only status phase 7 writes when a candidate clears the gate.</summary>
    internal const string AcceptedShadowStatus = "accepted, shadow";

    /// <summary>Gets the artifact id, <c>Guid.NewGuid().ToString("N")</c>.</summary>
    public string ArtifactId { get; init; } = string.Empty;

    /// <summary>Gets <c>code</c> or <c>non-code</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets <see cref="AcceptedShadowStatus"/> on an accepted file, or null on a failed candidate.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the feature schema version this artifact was fit with.</summary>
    public int FeatureSchemaVersion { get; init; } = SchemaVersion;

    /// <summary>Gets the teacher model the labels were graded by.</summary>
    public string TeacherModel { get; init; } = string.Empty;

    /// <summary>Gets the teacher prompt version the labels were graded by.</summary>
    public string TeacherPromptVersion { get; init; } = string.Empty;

    /// <summary>Gets the BGE model id the vectors were embedded with.</summary>
    public string BgeModelId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the previous accepted artifact id, or null for a first artifact and for the first artifact
    /// after a retirement. Rollback does not promote this id.
    /// </summary>
    public string? PreviousArtifactId { get; init; }

    /// <summary>Gets the trainer name. Phase 7 records <c>Sdca</c>.</summary>
    public string Trainer { get; init; } = GraderEpochTrainer.SdcaName;

    /// <summary>Gets the linear weights in <see cref="FeatureNames"/> order.</summary>
    public float[] Weights { get; init; } = [];

    /// <summary>Gets the linear bias added after the dot product.</summary>
    public float Bias { get; init; }

    /// <summary>Gets the feature names, one per weight. Tool-count names are absent.</summary>
    public string[] FeatureNames { get; init; } = [];

    /// <summary>Gets the slots dropped because no training row observed them.</summary>
    public string[] DroppedSlots { get; init; } = [];

    /// <summary>Gets the question-vector imputation mean, or null when that slot was dropped.</summary>
    public float[]? QuestionMean { get; init; }

    /// <summary>Gets the analysis imputation mean, or null when that slot was dropped.</summary>
    public double? AnalysisMean { get; init; }

    /// <summary>Gets the syntax imputation mean, or null when that slot was dropped.</summary>
    public double? SyntaxMean { get; init; }

    /// <summary>Gets the length imputation mean, or null when that slot was dropped.</summary>
    public double? LengthMean { get; init; }

    /// <summary>Gets the embedding length the vectors and the means were built at.</summary>
    public int EmbeddingDimension { get; init; }

    /// <summary>Gets the cosine floor, or null when the reservoir or the holdout contributed no cosine.</summary>
    public double? CosineFloor { get; init; }

    /// <summary>Gets how many question vectors were sealed.</summary>
    public int ReservoirCount { get; init; }

    /// <summary>Gets the configured reservoir cap this fit used.</summary>
    public int ReservoirCapacity { get; init; }

    /// <summary>Gets the seed the reservoir and the bootstrap each started from.</summary>
    public int RandomSeed { get; init; }

    /// <summary>Gets how many eligible labels the fit saw.</summary>
    public int EligibleCount { get; init; }

    /// <summary>Gets the holdout size actually scored.</summary>
    public int HoldoutCount { get; init; }

    /// <summary>Gets the training row count after session exclusion.</summary>
    public int TrainingCount { get; init; }

    /// <summary>Gets how many eligible labels were dropped for sharing a holdout session.</summary>
    public int SessionExcludedCount { get; init; }

    /// <summary>Gets the holdout mean absolute error of the clamped predictions.</summary>
    public double? HoldoutMae { get; init; }

    /// <summary>Gets the holdout Spearman, or null when a sample had zero variance.</summary>
    public double? Spearman { get; init; }

    /// <summary>Gets the bootstrap interval's lower end, or null when the bootstrap did not run.</summary>
    public double? BootstrapLow { get; init; }

    /// <summary>Gets the bootstrap interval's upper end, or null when the bootstrap did not run.</summary>
    public double? BootstrapHigh { get; init; }

    /// <summary>Gets the training-set length correlation, or null when it is undefined.</summary>
    public double? LengthCorrelation { get; init; }

    /// <summary>Gets when this candidate was fit.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Whether this artifact was trained for the current teacher model, prompt version, and BGE id.
    /// Feature schema is a separate check: a mismatch retires the artifact on the tick, and the shadow
    /// stamp uses this method only.
    /// </summary>
    /// <param name="teacherModel">The current <c>JudgeOptions.ModelName</c>.</param>
    /// <param name="promptVersion">The current <c>JudgeOptions.PromptVersion</c>.</param>
    /// <param name="bgeModelId">The current BGE identity.</param>
    /// <returns><see langword="true"/> when all three match, ordinal.</returns>
    internal bool MatchesTeacher(string teacherModel, string promptVersion, string bgeModelId) =>
        string.Equals(TeacherModel, teacherModel, StringComparison.Ordinal)
        && string.Equals(TeacherPromptVersion, promptVersion, StringComparison.Ordinal)
        && string.Equals(BgeModelId, bgeModelId, StringComparison.Ordinal);

    /// <summary>The clamped prediction, or null when the row cannot be scored on this layout.</summary>
    /// <param name="input">The resolved feature input.</param>
    /// <returns>The score in <c>[0, 1]</c>, or null.</returns>
    internal double? Score(GraderEpochFeatureInput input)
    {
        var features = GraderEpochFeatures.Encode(artifact: this, input: input);
        return features is null ? null : GraderEpochFeatures.Score(artifact: this, features: features);
    }
}
