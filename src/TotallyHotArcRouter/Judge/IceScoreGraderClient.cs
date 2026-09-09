using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The ICE-Score usefulness grader (Zhuo, Findings of EACL 2024; arXiv:2304.14317), taking only its
/// <c>"usefulness"</c> aspect (docs/research/code-quality-metrics-assessment.md §5.1) - its
/// <c>"functional correctness"</c> aspect requires reference tests and comparison code that do not exist for
/// live traffic, and <see cref="CodeJudgeGraderClient"/> already covers correctness from a different
/// construct.
/// </summary>
/// <remarks>
/// ICE-Score's rubric is a 0-4 rating; the backbone is asked for a single digit in that range rather than
/// prose, mirroring <see cref="GEvalJudgeClient"/>'s form-filling cue for its own 1-5 scale.
/// </remarks>
public sealed class IceScoreGraderClient : PortfolioGraderClientBase
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientNameConstant = nameof(IceScoreGraderClient);

    private const int MaxScore = 4;
    private const int MinScore = 0;

    // ReSharper disable once RedundantVerbatimStringPrefix
    private static readonly Regex ScoreDigitPattern = new(pattern: @"[0-4]", options: RegexOptions.Compiled);

    /// <summary>Initializes a new instance of the <see cref="IceScoreGraderClient"/> class.</summary>
    public IceScoreGraderClient(
        IHttpClientFactory httpClientFactory,
        JudgeModelSelector modelSelector,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<IceScoreGraderClient> logger)
        : base(httpClientFactory: httpClientFactory, modelSelector: modelSelector, options: options, logger: logger)
    {
    }

    /// <inheritdoc/>
    public override string GraderKey => GraderKeys.IceScore;

    /// <inheritdoc/>
    protected override string HttpClientName => HttpClientNameConstant;

    /// <inheritdoc/>
    protected override string BuildPrompt(string dimension, string responseText, string taskPrompt)
    {
        var taskSection = string.IsNullOrWhiteSpace(taskPrompt)
            ? string.Empty
            : $"""

               Task the response was written for:
               ---
               {taskPrompt}
               ---

               """;

        return $"""
                You are an expert evaluator rating the usefulness of an AI assistant's coding response on a
                scale of 0 (not useful at all) to 4 (extremely useful).
                {taskSection}
                Usefulness means: would this response, as given, meaningfully help someone trying to
                accomplish the stated task - considering whether it is complete enough to apply directly,
                whether it addresses the actual requirement, and whether a developer would need to do
                significant extra work to make it usable.

                Response to evaluate:
                ---
                {responseText}
                ---

                Respond with only a single digit from 0 to 4 and nothing else.
                """;
    }

    /// <inheritdoc/>
    protected override double? TryParseScore(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var match = ScoreDigitPattern.Match(content);
        if (!match.Success) return null;

        var digit = int.Parse(s: match.Value, provider: CultureInfo.InvariantCulture);
        return (double)(digit - MinScore) / (MaxScore - MinScore);
    }
}
