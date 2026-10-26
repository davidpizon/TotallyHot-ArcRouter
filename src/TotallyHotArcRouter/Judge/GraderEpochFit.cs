using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One kind's holdout split and one SDCA fit. The hosted service decides whether to call it and writes
/// the log lines. Nothing here touches <c>accepted.json</c>; a failed gate still returns the candidate
/// so the caller can file it under <c>failed</c>.
/// </summary>
internal static class GraderEpochFit
{
    /// <summary>
    /// Splits eligible labels into the newest holdout and the training pile. Unparseable timestamps
    /// cannot be held out. Session siblings of a holdout row are in neither set and are not added to
    /// the holdout. The holdout is not shrunk to manufacture training rows.
    /// </summary>
    /// <param name="eligible">Labels that already passed the eligibility join.</param>
    /// <param name="holdoutSize">How many newest labels to hold out.</param>
    /// <returns>The split. When fewer than <paramref name="holdoutSize"/> timestamps parse, training is empty.</returns>
    internal static GraderEpochSplit Split(IReadOnlyList<GraderEpochLabel> eligible, int holdoutSize)
    {
        var parseable = eligible.Where(label => label.CreatedAt is not null)
            .OrderByDescending(label => label.CreatedAt)
            .ThenBy(label => label.CorrelationId, StringComparer.Ordinal)
            .ToList();
        if (parseable.Count < holdoutSize)
            return new GraderEpochSplit(parseable, [], SessionExcluded: 0);

        var holdout = parseable.Take(holdoutSize).ToList();
        var sessions = holdout.Select(label => label.SessionKey).ToHashSet(StringComparer.Ordinal);
        var rest = parseable.Skip(holdoutSize);
        var excluded = 0;
        var training = new List<GraderEpochLabel>();
        foreach (var label in rest)
        {
            if (sessions.Contains(label.SessionKey)) excluded++;
            else training.Add(label);
        }

        return new GraderEpochSplit(holdout, training, excluded);
    }

    /// <summary>
    /// Fits <c>Sdca</c> on the training rows, seals the reservoir from those rows only, and scores the
    /// holdout. The candidate replaces a comparable live artifact only when the bootstrap lower end is
    /// greater than 0. Equal to 0 does not replace.
    /// </summary>
    /// <param name="kind">The kind being fit.</param>
    /// <param name="eligibleCount">Eligible labels before the split, recorded on the artifact.</param>
    /// <param name="split">The holdout and the training pile.</param>
    /// <param name="options">The gate bars, the seed, and the reservoir cap.</param>
    /// <param name="teacherModel">Stamped onto the artifact and compared at the next tick.</param>
    /// <param name="promptVersion">The teacher prompt version.</param>
    /// <param name="bgeModelId">The BGE identity the vectors were checked against.</param>
    /// <param name="dimension">The only legal vector length.</param>
    /// <param name="live">The comparable accepted artifact, or null when there is nothing to bootstrap against.</param>
    /// <param name="key">The vector key. Reservoir associated data is not the response-vector label.</param>
    /// <param name="createdAtUtc">When this candidate is stamped.</param>
    /// <returns>The candidate, its reservoir file, and whether the gate said to accept it.</returns>
    internal static GraderEpochFitOutcome Run(
        string kind,
        int eligibleCount,
        GraderEpochSplit split,
        GraderEpochOptions options,
        string teacherModel,
        string promptVersion,
        string bgeModelId,
        int dimension,
        GraderEpochArtifact? live,
        byte[] key,
        DateTimeOffset createdAtUtc)
    {
        var trainingInputs = split.Training.Select(label => label.ToInput(bgeModelId, dimension)).ToList();
        var questionMean = GraderEpochFeatures.QuestionMean(trainingInputs, dimension);
        var analysisMean = GraderEpochFeatures.AnalysisMean(trainingInputs);
        var syntaxMean = GraderEpochFeatures.SyntaxMean(trainingInputs);
        var lengthMean = GraderEpochFeatures.LengthMean(trainingInputs);
        var names = GraderEpochFeatures.Names(
            includeQuestion: questionMean is not null,
            includeAnalysis: analysisMean is not null,
            includeSyntax: syntaxMean is not null,
            includeLength: lengthMean is not null,
            dimension: dimension);
        var dropped = Dropped(questionMean, analysisMean, syntaxMean, lengthMean);
        var layout = new GraderEpochArtifact
        {
            FeatureNames = names,
            DroppedSlots = dropped,
            QuestionMean = questionMean,
            AnalysisMean = analysisMean,
            SyntaxMean = syntaxMean,
            LengthMean = lengthMean,
            EmbeddingDimension = dimension,
            Weights = new float[names.Length]
        };

        var matrix = new float[split.Training.Count][];
        var labels = new double[split.Training.Count];
        for (var i = 0; i < split.Training.Count; i++)
        {
            if (GraderEpochFeatures.Encode(artifact: layout, input: trainingInputs[i]) is not { } encoded)
                throw new InvalidOperationException("A training row could not be encoded on the layout just fit from it.");
            matrix[i] = encoded;
            labels[i] = split.Training[i].JudgeScore;
        }

        var (weights, bias) = GraderEpochTrainer.Fit(features: matrix, labels: labels, seed: options.RandomSeed);
        var artifactId = Guid.NewGuid().ToString("N");
        var sample = ReservoirSample(split.Training, bgeModelId, dimension, options);
        var reservoir = Seal(sample, artifactId, bgeModelId, key);
        var floor = CosineFloor(split.Holdout, sample, bgeModelId, dimension, options.CosineFloorPercentile);
        var lengthCorrelation = LengthCorrelation(split.Training);

        var scored = TryScoreHoldout(layout: layout with { Weights = weights, Bias = bias },
            holdout: split.Holdout, bgeModelId: bgeModelId, dimension: dimension,
            out var predictions, out var teachers);
        double? mae = null;
        double? spearman = null;
        double? bootstrapLow = null;
        double? bootstrapHigh = null;
        var passed = false;
        if (scored)
        {
            mae = RankCorrelation.MeanAbsoluteDifference(predictions, teachers);
            spearman = RankCorrelation.Spearman(predictions, teachers);
            passed = mae is not null && mae <= options.MaxHoldoutMae && spearman is not null &&
                     spearman >= options.MinSpearman;
            if (passed && live is not null)
            {
                if (!TryBootstrap(live: live, holdout: split.Holdout, predictions: predictions, teachers: teachers,
                        bgeModelId: bgeModelId, dimension: dimension, options: options,
                        low: out bootstrapLow, high: out bootstrapHigh))
                    passed = false;
                else
                    passed = bootstrapLow > 0;
            }
        }

        var artifact = layout with
        {
            ArtifactId = artifactId,
            Kind = kind,
            Status = passed ? GraderEpochArtifact.AcceptedShadowStatus : null,
            FeatureSchemaVersion = GraderEpochArtifact.SchemaVersion,
            TeacherModel = teacherModel,
            TeacherPromptVersion = promptVersion,
            BgeModelId = bgeModelId,
            PreviousArtifactId = passed ? live?.ArtifactId : null,
            Trainer = GraderEpochTrainer.SdcaName,
            Weights = weights,
            Bias = bias,
            CosineFloor = floor,
            ReservoirCount = sample.Count,
            ReservoirCapacity = options.ReservoirCapacity,
            RandomSeed = options.RandomSeed,
            EligibleCount = eligibleCount,
            HoldoutCount = split.Holdout.Count,
            TrainingCount = split.Training.Count,
            SessionExcludedCount = split.SessionExcluded,
            HoldoutMae = mae,
            Spearman = spearman,
            BootstrapLow = bootstrapLow,
            BootstrapHigh = bootstrapHigh,
            LengthCorrelation = lengthCorrelation,
            CreatedAtUtc = createdAtUtc
        };
        return new GraderEpochFitOutcome(artifact, reservoir, passed);
    }

    private static List<GraderEpochLabel> ReservoirSample(IReadOnlyList<GraderEpochLabel> training, string bgeModelId,
        int dimension, GraderEpochOptions options)
    {
        var usable = new List<GraderEpochLabel>();
        foreach (var label in training)
        {
            var input = label.ToInput(bgeModelId, dimension);
            if (input is { QuestionMissing: false, QuestionVector: not null }) usable.Add(label);
        }

        usable.Sort(static (left, right) =>
        {
            var time = left.CreatedAt!.Value.CompareTo(right.CreatedAt!.Value);
            return time != 0 ? time : string.CompareOrdinal(left.CorrelationId, right.CorrelationId);
        });
        return GraderEpochReservoir.Sample(usable, options.ReservoirCapacity, options.RandomSeed);
    }

    private static byte[] Seal(IReadOnlyList<GraderEpochLabel> sample, string artifactId, string bgeModelId, byte[] key)
    {
        var records = new List<(string CorrelationId, byte[] Blob)>(sample.Count);
        foreach (var label in sample)
        {
            var vector = label.QuestionVector;
            if (vector is null) continue;
            records.Add((label.CorrelationId,
                ResponseVectorSeal.SealReservoir(key: key, artifactId: artifactId, correlationId: label.CorrelationId,
                    modelId: bgeModelId, vector: vector)));
        }

        return GraderEpochReservoir.Write(records);
    }

    private static double? CosineFloor(IReadOnlyList<GraderEpochLabel> holdout, IReadOnlyList<GraderEpochLabel> sample,
        string bgeModelId, int dimension, int percentile)
    {
        if (sample.Count == 0) return null;

        var cosines = new List<double>();
        foreach (var label in holdout)
        {
            var input = label.ToInput(bgeModelId, dimension);
            if (input.QuestionMissing || input.QuestionVector is null) continue;

            double? best = null;
            foreach (var neighbor in sample)
            {
                if (neighbor.QuestionVector is null) continue;
                var cosine = GraderEpochVectors.Cosine(input.QuestionVector, neighbor.QuestionVector);
                if (cosine is null) continue;
                if (best is null || cosine > best) best = cosine;
            }

            if (best is not null) cosines.Add(best.Value);
        }

        return GraderEpochReservoir.Floor(cosines, percentile);
    }

    private static double? LengthCorrelation(IReadOnlyList<GraderEpochLabel> training)
    {
        var scores = new List<double>();
        var lengths = new List<double>();
        foreach (var label in training)
        {
            if (label.ResponseLengthChars is null) continue;
            scores.Add(label.JudgeScore);
            lengths.Add(label.ResponseLengthChars.Value);
        }

        return scores.Count == 0 ? null : RankCorrelation.Pearson(scores, lengths);
    }

    private static bool TryScoreHoldout(GraderEpochArtifact layout, IReadOnlyList<GraderEpochLabel> holdout,
        string bgeModelId, int dimension, out List<double> predictions, out List<double> teachers)
    {
        predictions = new List<double>(holdout.Count);
        teachers = new List<double>(holdout.Count);
        foreach (var label in holdout)
        {
            var score = layout.Score(label.ToInput(bgeModelId, dimension));
            if (score is null) return false;
            predictions.Add(score.Value);
            teachers.Add(label.JudgeScore);
        }

        return true;
    }

    private static bool TryBootstrap(GraderEpochArtifact live, IReadOnlyList<GraderEpochLabel> holdout,
        IReadOnlyList<double> predictions, IReadOnlyList<double> teachers, string bgeModelId, int dimension,
        GraderEpochOptions options, out double? low, out double? high)
    {
        low = null;
        high = null;
        var deltas = new double[holdout.Count];
        for (var i = 0; i < holdout.Count; i++)
        {
            var previous = live.Score(holdout[i].ToInput(bgeModelId, dimension));
            if (previous is null) return false;
            deltas[i] = Math.Abs(previous.Value - teachers[i]) - Math.Abs(predictions[i] - teachers[i]);
        }

        var random = new Random(options.RandomSeed);
        var means = new double[options.BootstrapSamples];
        for (var sample = 0; sample < means.Length; sample++)
        {
            double sum = 0;
            foreach (var _ in deltas) sum += deltas[random.Next(deltas.Length)];
            means[sample] = sum / deltas.Length;
        }

        Array.Sort(means);
        var tail = (1 - options.BootstrapConfidence) / 2;
        var lowIndex = (int)Math.Floor(tail * (means.Length - 1));
        var highIndex = Math.Min(means.Length - 1, (int)Math.Ceiling((1 - tail) * (means.Length - 1)));
        low = means[lowIndex];
        high = means[highIndex];
        return true;
    }

    private static string[] Dropped(float[]? questionMean, double? analysisMean, double? syntaxMean, double? lengthMean)
    {
        var dropped = new List<string>(4);
        if (questionMean is null) dropped.Add(GraderEpochFeatures.Question);
        if (analysisMean is null) dropped.Add(GraderEpochFeatures.Analysis);
        if (syntaxMean is null) dropped.Add(GraderEpochFeatures.Syntax);
        if (lengthMean is null) dropped.Add(GraderEpochFeatures.Length);
        return dropped.ToArray();
    }
}

/// <summary>The holdout, the training pile, and how many session siblings were left out of both.</summary>
/// <param name="Holdout">The newest labels, or every parseable label when there are not enough of them.</param>
/// <param name="Training">Labels that train. Empty when the holdout could not be filled.</param>
/// <param name="SessionExcluded">Parseable labels dropped because their session already had a holdout row.</param>
internal sealed record GraderEpochSplit(
    List<GraderEpochLabel> Holdout,
    List<GraderEpochLabel> Training,
    int SessionExcluded);

/// <summary>A fit that ran, whether or not the gate accepted it.</summary>
/// <param name="Artifact">The candidate. <see cref="GraderEpochArtifact.Status"/> is set only when <paramref name="Passed"/>.</param>
/// <param name="Reservoir">The sealed reservoir file.</param>
/// <param name="Passed">Whether the caller should replace the accepted pointer.</param>
internal sealed record GraderEpochFitOutcome(GraderEpochArtifact Artifact, byte[] Reservoir, bool Passed);
