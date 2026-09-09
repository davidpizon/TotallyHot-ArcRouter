using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The RACE readability/maintainability grader (Zheng et al.; arXiv:2407.11470), taking its rubric
/// vocabulary - naming, structure, cohesion, and comments used only where they earn their place
/// (docs/research/code-quality-metrics-assessment.md §5.1) - as a single overall rating rather than RACE's
/// full multi-dimension protocol, the same "adopt the vocabulary, not the whole harness" scoping
/// <see cref="CodeJudgeGraderClient"/> and <see cref="IceScoreGraderClient"/> take for their own sources.
/// </summary>
public sealed class RaceGraderClient : PortfolioGraderClientBase
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientNameConstant = nameof(RaceGraderClient);

    private const int MaxScore = 5;
    private const int MinScore = 1;

    // ReSharper disable once RedundantVerbatimStringPrefix
    private static readonly Regex ScoreDigitPattern = new(pattern: @"[1-5]", options: RegexOptions.Compiled);

    /// <summary>Initializes a new instance of the <see cref="RaceGraderClient"/> class.</summary>
    public RaceGraderClient(
        IHttpClientFactory httpClientFactory,
        JudgeModelSelector modelSelector,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<RaceGraderClient> logger)
        : base(httpClientFactory: httpClientFactory, modelSelector: modelSelector, options: options, logger: logger)
    {
    }

    /// <inheritdoc/>
    public override string GraderKey => GraderKeys.Race;

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
                You are an expert code reviewer rating the readability and maintainability of an AI
                assistant's coding response on a scale of 1 (worst) to 5 (best).
                {taskSection}
                Judge readability and maintainability specifically: clear and consistent naming, sensible
                structure and cohesion, appropriate use of comments (present where the code's intent is
                non-obvious, absent where the code already speaks for itself), and freedom from needless
                complexity. Do not judge correctness.

                Response to evaluate:
                ---
                {responseText}
                ---

                Respond with only a single digit from 1 to 5 and nothing else.
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
