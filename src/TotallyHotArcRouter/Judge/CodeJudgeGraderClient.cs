using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The CodeJudge correctness grader (Tong &amp; Zhang, EMNLP 2024; arXiv:2410.02184), implementing its
/// severity-weighted, test-free, reference-free fault taxonomy verbatim
/// (docs/research/code-quality-metrics-assessment.md §5.1).
/// </summary>
/// <remarks>
/// The backbone is asked to enumerate faults as one <c>FAULT: &lt;severity&gt;</c> line per fault (or a
/// single <c>FAULT: none</c> line when it finds none) rather than to compute the deduction arithmetic
/// itself - a free, possibly small backbone is far more reliable at classifying one fault's severity than at
/// correctly summing and capping several. This client does the arithmetic deterministically from the parsed
/// severities: <c>score = 1 - min(100, Σ deductions) / 100</c>, deductions negligible=0/small=5/major=50/
/// fatal=100. A response containing no recognizable <c>FAULT:</c> line at all - neither a fault nor an
/// explicit "none" - is unparseable, the same abstain-rather-than-fabricate outcome
/// <see cref="PortfolioGraderClientBase.TryParseScore"/> documents.
/// </remarks>
public sealed class CodeJudgeGraderClient : PortfolioGraderClientBase
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientNameConstant = nameof(CodeJudgeGraderClient);

    // ReSharper disable once RedundantVerbatimStringPrefix
    private static readonly Regex FaultLinePattern =
        new(pattern: @"FAULT:\s*(negligible|small|major|fatal|none)", options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="CodeJudgeGraderClient"/> class.</summary>
    public CodeJudgeGraderClient(
        IHttpClientFactory httpClientFactory,
        JudgeModelSelector modelSelector,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<CodeJudgeGraderClient> logger)
        : base(httpClientFactory: httpClientFactory, modelSelector: modelSelector, options: options, logger: logger)
    {
    }

    /// <inheritdoc/>
    public override string GraderKey => GraderKeys.CodeJudge;

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
                You are a strict code reviewer looking only for correctness faults in an AI assistant's
                response.
                {taskSection}
                Response to evaluate:
                ---
                {responseText}
                ---

                Identify every correctness fault, classifying each by severity:
                - fatal: a missing/incomplete implementation, or a declaration error that prevents the code from being usable at all.
                - major: a logic error that produces wrong behavior on a case the task requires.
                - small: an input-handling gap (an edge case not handled).
                - negligible: an alternative/style choice, a missing dependency note, an error-handling gap, or an efficiency concern that does not affect correctness.

                Respond with exactly one line per fault found, in the form:
                FAULT: <severity>

                If you find no faults, respond with exactly one line:
                FAULT: none

                Do not include any other text.
                """;
    }

    /// <inheritdoc/>
    protected override double? TryParseScore(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var matches = FaultLinePattern.Matches(content);
        if (matches.Count == 0) return null;

        var deduction = 0;
        foreach (Match match in matches)
        {
            deduction += match.Groups[1].Value.ToLowerInvariant() switch
            {
                "negligible" => 0,
                "small" => 5,
                "major" => 50,
                "fatal" => 100,
                _ => 0 // "none"
            };
        }

        return 1.0 - Math.Min(100, deduction) / 100.0;
    }
}
