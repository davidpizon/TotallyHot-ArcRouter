using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One eligible teacher label for a phase 7 fit. The judge score is the only <c>y</c>.
/// <c>LocalScore</c> is not on this type, so a fit cannot train on the shadow score.
/// </summary>
internal sealed class GraderEpochLabel
{
    private readonly Dictionary<string, double> _probes = new(StringComparer.Ordinal);

    /// <summary>The receipt's correlation id.</summary>
    internal required string CorrelationId { get; init; }

    /// <summary>The raw <c>created_at_utc</c> text, kept so an unparseable stamp cannot enter the holdout.</summary>
    internal required string CreatedAtUtcRaw { get; init; }

    /// <summary>The parsed stamp, or null when <see cref="CreatedAtUtcRaw"/> is not a round-trip timestamp.</summary>
    internal DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The receipt session id when it was stored non-empty; otherwise null.</summary>
    internal string? SessionId { get; init; }

    /// <summary>The receipt's <c>question_vector_missing</c> flag. Null is a row from before the column.</summary>
    internal bool? QuestionVectorMissing { get; init; }

    /// <summary>The memory-entry question vector, or null when that row is absent or the blob is unusable.</summary>
    internal float[]? QuestionVector { get; init; }

    /// <summary>The memory row's <c>embedding_model</c>, or null when the row or the column is null.</summary>
    internal string? QuestionModel { get; init; }

    /// <summary>How many <c>grader_key = analysis</c> rows this correlation id has.</summary>
    internal int AnalysisCount { get; init; }

    /// <summary>The analysis score when there is exactly one row; otherwise unused.</summary>
    internal double? AnalysisScore { get; init; }

    /// <summary>The stored <c>syntax_score</c>. Null means the parser did not run, not a score of 0.</summary>
    internal double? SyntaxScore { get; init; }

    /// <summary>Whether the reply contained a fenced code block. This column is never missing.</summary>
    internal bool ResponseHasCode { get; init; }

    /// <summary>The stored response length, or null. A null length is not 0.</summary>
    internal int? ResponseLengthChars { get; init; }

    /// <summary>The opened response vector. Eligibility already required it to be usable.</summary>
    internal required float[] ResponseVector { get; init; }

    /// <summary>The single judge <c>grader_scores</c> row. This is the training label.</summary>
    internal required double JudgeScore { get; init; }

    /// <summary>
    /// Session id for the holdout sibling rule: the receipt's session when it is non-empty, otherwise
    /// <see cref="CorrelationIdParser.SessionIdOf"/>.
    /// </summary>
    internal string SessionKey =>
        !string.IsNullOrEmpty(SessionId) ? SessionId : CorrelationIdParser.SessionIdOf(CorrelationId);

    /// <summary>
    /// Records an extra key that schema version 1 must not read. <see cref="ReadColumn"/> throws if the
    /// feature builder asks for it, which is how the tool-count fixture proves those names stay out.
    /// </summary>
    /// <param name="key">The unused key, for example <c>tool_definition_count</c>.</param>
    /// <param name="value">The planted value. It is never written as a coefficient.</param>
    internal void AddProbe(string key, double value) => _probes[key] = value;

    /// <summary>
    /// Reads one schema version 1 scalar. A probe key throws instead of returning a number, and an
    /// unknown name throws instead of being treated as 0.
    /// </summary>
    /// <param name="column">The column the feature builder is asking for.</param>
    /// <returns>The stored value, or null when that column is SQL NULL.</returns>
    internal object? ReadColumn(string column)
    {
        if (_probes.ContainsKey(column))
            throw new InvalidOperationException($"Feature schema version 1 does not read {column}.");

        return column switch
        {
            "question_vector_missing" => QuestionVectorMissing,
            "analysis_score" => AnalysisScore,
            "syntax_score" => SyntaxScore,
            "response_has_code" => ResponseHasCode,
            "response_length_chars" => ResponseLengthChars,
            _ => throw new InvalidOperationException($"Column {column} is not a schema version 1 feature.")
        };
    }

    /// <summary>
    /// The feature-builder input for this label. Scalar columns go through <see cref="ReadColumn"/> so a
    /// probe key cannot be skipped by reading the property instead.
    /// </summary>
    /// <param name="bgeModelId">The current BGE identity.</param>
    /// <param name="dimension">The only legal vector length.</param>
    /// <returns>The input the shared encoder scores.</returns>
    internal GraderEpochFeatureInput ToInput(string bgeModelId, int dimension)
    {
        var flag = ReadColumn("question_vector_missing") as bool?;
        var question = GraderEpochVectors.ResolveQuestion(flag: flag, vector: QuestionVector, modelId: QuestionModel,
            bgeModelId: bgeModelId, dimension: dimension);
        var analysis = ReadColumn("analysis_score") as double?;
        var syntax = ReadColumn("syntax_score") as double?;
        var length = ReadColumn("response_length_chars") as int?;
        return new GraderEpochFeatureInput
        {
            QuestionMissing = question.Missing,
            QuestionVector = question.Vector,
            AnalysisMissing = AnalysisCount != 1 || analysis is null,
            AnalysisScore = AnalysisCount == 1 ? analysis : null,
            SyntaxMissing = syntax is null,
            SyntaxScore = syntax,
            ResponseHasCode = ReadColumn("response_has_code") is true,
            LengthMissing = length is null,
            LengthChars = length,
            ResponseVector = ResponseVector
        };
    }
}

/// <summary>
/// One row's schema version 1 inputs, already resolved to missing-or-value. The encoder imputes from
/// the artifact's training means; it does not build a zero vector for a missing slot.
/// </summary>
internal sealed class GraderEpochFeatureInput
{
    /// <summary>Whether the question vector is the missingness indicator.</summary>
    internal bool QuestionMissing { get; init; }

    /// <summary>The question vector when <see cref="QuestionMissing"/> is false.</summary>
    internal float[]? QuestionVector { get; init; }

    /// <summary>Whether analysis is missing (no row, or more than one row).</summary>
    internal bool AnalysisMissing { get; init; }

    /// <summary>The analysis score when <see cref="AnalysisMissing"/> is false.</summary>
    internal double? AnalysisScore { get; init; }

    /// <summary>Whether syntax was not stored. Null is not a score of 0.</summary>
    internal bool SyntaxMissing { get; init; }

    /// <summary>The syntax score, <c>1.0</c> or <c>0.0</c>, when the parser ran.</summary>
    internal double? SyntaxScore { get; init; }

    /// <summary>Whether the reply contained a fenced code block.</summary>
    internal bool ResponseHasCode { get; init; }

    /// <summary>Whether <c>response_length_chars</c> is null.</summary>
    internal bool LengthMissing { get; init; }

    /// <summary>The response length when <see cref="LengthMissing"/> is false.</summary>
    internal double? LengthChars { get; init; }

    /// <summary>The response vector. Unusable values make the row unscorable.</summary>
    internal float[]? ResponseVector { get; init; }
}
