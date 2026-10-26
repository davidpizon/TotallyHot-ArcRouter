namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Schema version 1's feature order. Tool-count names from the parent plan are not in this list: they
/// are not zero-filled and they are not an always-missing column. A slot whose value is missing on every
/// training row is dropped, indicator included, because an indicator that is 1 on every row is only an
/// intercept.
/// </summary>
internal static class GraderEpochFeatures
{
    /// <summary>Dropped-slot name for the question vector and its indicator.</summary>
    internal const string Question = "question";

    /// <summary>Dropped-slot name for the analysis score and its indicator.</summary>
    internal const string Analysis = "analysis";

    /// <summary>Dropped-slot name for the syntax score and its indicator.</summary>
    internal const string Syntax = "syntax";

    /// <summary>Dropped-slot name for the response length and its indicator.</summary>
    internal const string Length = "length";

    /// <summary>
    /// Names in encode order for the slots this fit kept. The coefficient vector's length is this
    /// list's width.
    /// </summary>
    /// <param name="includeQuestion">Whether any training row had a usable question vector.</param>
    /// <param name="includeAnalysis">Whether any training row had exactly one analysis score.</param>
    /// <param name="includeSyntax">Whether any training row had a non-null syntax score.</param>
    /// <param name="includeLength">Whether any training row had a non-null length.</param>
    /// <param name="dimension">The embedding length. Question and response vectors both use it.</param>
    /// <returns>One name per coefficient.</returns>
    internal static string[] Names(bool includeQuestion, bool includeAnalysis, bool includeSyntax,
        bool includeLength, int dimension)
    {
        var names = new List<string>();
        if (includeQuestion)
        {
            names.Add("question_vector_missing");
            for (var i = 0; i < dimension; i++) names.Add($"question_vector[{i}]");
        }

        if (includeAnalysis)
        {
            names.Add("analysis_missing");
            names.Add("analysis_score");
        }

        if (includeSyntax)
        {
            names.Add("syntax_missing");
            names.Add("syntax_score");
        }

        names.Add("response_has_code");
        if (includeLength)
        {
            names.Add("length_missing");
            names.Add("response_length_chars");
        }

        for (var i = 0; i < dimension; i++) names.Add($"response_vector[{i}]");
        return names.ToArray();
    }

    /// <summary>
    /// Encodes one row with the artifact's stored means and dropped slots. A missing slot uses the
    /// training mean, never 0. An unusable response vector returns null so the caller fails the gate
    /// or leaves <c>LocalScore</c> null instead of scoring a partial vector.
    /// </summary>
    /// <param name="artifact">The artifact whose layout and means are reused at score time.</param>
    /// <param name="input">The resolved row.</param>
    /// <returns>The feature vector, or null when this row cannot be scored.</returns>
    internal static float[]? Encode(GraderEpochArtifact artifact, GraderEpochFeatureInput input)
    {
        if (!GraderEpochVectors.IsUsable(input.ResponseVector, artifact.EmbeddingDimension)) return null;

        var dropped = artifact.DroppedSlots;
        var values = new List<float>(artifact.FeatureNames.Length);
        if (!Contains(dropped, Question))
        {
            var present = !input.QuestionMissing &&
                          GraderEpochVectors.IsUsable(input.QuestionVector, artifact.EmbeddingDimension);
            if (!present && artifact.QuestionMean is null) return null;
            values.Add(present ? 0f : 1f);
            AddVector(values, present ? input.QuestionVector! : artifact.QuestionMean!);
        }

        if (!Contains(dropped, Analysis))
        {
            if (artifact.AnalysisMean is null) return null;
            if (input is { AnalysisMissing: false, AnalysisScore: { } analysis })
            {
                values.Add(0f);
                values.Add((float)analysis);
            }
            else
            {
                values.Add(1f);
                values.Add((float)artifact.AnalysisMean.Value);
            }
        }

        if (!Contains(dropped, Syntax))
        {
            if (artifact.SyntaxMean is null) return null;
            if (input is { SyntaxMissing: false, SyntaxScore: { } syntax })
            {
                values.Add(0f);
                values.Add((float)syntax);
            }
            else
            {
                values.Add(1f);
                values.Add((float)artifact.SyntaxMean.Value);
            }
        }

        values.Add(input.ResponseHasCode ? 1f : 0f);
        if (!Contains(dropped, Length))
        {
            if (artifact.LengthMean is null) return null;
            if (input is { LengthMissing: false, LengthChars: { } length })
            {
                values.Add(0f);
                values.Add((float)length);
            }
            else
            {
                values.Add(1f);
                values.Add((float)artifact.LengthMean.Value);
            }
        }

        AddVector(values, input.ResponseVector!);
        return values.Count == artifact.Weights.Length ? values.ToArray() : null;
    }

    /// <summary>
    /// Dot product of the artifact's weights with <paramref name="features"/>, plus the bias, clamped
    /// to <c>[0, 1]</c>. That clamped number is what the gate and the shadow receipt store.
    /// </summary>
    /// <param name="artifact">The coefficient payload. An ML.NET pipeline is not stored.</param>
    /// <param name="features">The vector from <see cref="Encode"/>.</param>
    /// <returns>The clamped prediction, or null when the dot product is not finite.</returns>
    internal static double? Score(GraderEpochArtifact artifact, float[] features)
    {
        if (features.Length != artifact.Weights.Length) return null;

        double sum = artifact.Bias;
        for (var i = 0; i < features.Length; i++) sum += artifact.Weights[i] * (double)features[i];
        if (!double.IsFinite(sum)) return null;
        if (sum < 0) return 0;
        if (sum > 1) return 1;
        return sum;
    }

    /// <summary>The per-dimension mean of the training rows that have a usable question vector.</summary>
    /// <param name="rows">Training inputs.</param>
    /// <param name="dimension">The only legal vector length.</param>
    /// <returns>The mean, or null when no training row had a usable question vector.</returns>
    internal static float[]? QuestionMean(IReadOnlyList<GraderEpochFeatureInput> rows, int dimension)
    {
        var present = rows.Where(row =>
            !row.QuestionMissing && GraderEpochVectors.IsUsable(row.QuestionVector, dimension)).ToList();
        if (present.Count == 0) return null;

        var mean = new float[dimension];
        foreach (var row in present)
        {
            for (var i = 0; i < dimension; i++) mean[i] += row.QuestionVector![i];
        }

        for (var i = 0; i < dimension; i++) mean[i] /= present.Count;
        return mean;
    }

    /// <summary>The mean of training rows that have exactly one analysis score.</summary>
    /// <param name="rows">Training inputs.</param>
    /// <returns>The mean, or null when every row is missing analysis.</returns>
    internal static double? AnalysisMean(IReadOnlyList<GraderEpochFeatureInput> rows)
    {
        return Mean(rows, static row => row is { AnalysisMissing: false, AnalysisScore: { } score } ? score : null);
    }

    /// <summary>The mean of training rows whose syntax score was stored.</summary>
    /// <param name="rows">Training inputs.</param>
    /// <returns>The mean, or null when every row left syntax null.</returns>
    internal static double? SyntaxMean(IReadOnlyList<GraderEpochFeatureInput> rows)
    {
        return Mean(rows, static row => row is { SyntaxMissing: false, SyntaxScore: { } score } ? score : null);
    }

    /// <summary>The mean of training rows whose length was stored. A null length does not enter as 0.</summary>
    /// <param name="rows">Training inputs.</param>
    /// <returns>The mean, or null when every length is null.</returns>
    internal static double? LengthMean(IReadOnlyList<GraderEpochFeatureInput> rows)
    {
        return Mean(rows, static row => row is { LengthMissing: false, LengthChars: { } length } ? length : null);
    }

    /// <summary>The mean of the values <paramref name="value"/> returns, ignoring nulls.</summary>
    /// <param name="rows">Training inputs.</param>
    /// <param name="value">The observed number, or null when that row does not contribute.</param>
    /// <returns>The mean, or null when every row was missing.</returns>
    private static double? Mean(IReadOnlyList<GraderEpochFeatureInput> rows, Func<GraderEpochFeatureInput, double?> value)
    {
        double sum = 0;
        var count = 0;
        foreach (var row in rows)
        {
            if (value(row) is not { } observed) continue;
            sum += observed;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    private static bool Contains(IReadOnlyList<string> slots, string name)
    {
        foreach (var slot in slots)
        {
            if (string.Equals(slot, name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static void AddVector(List<float> values, float[] vector)
    {
        foreach (var component in vector) values.Add(component);
    }
}
