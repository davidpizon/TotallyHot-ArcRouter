using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Runs every registered <see cref="IStaticAnalyzer"/> over a snippet and averages the ones that had an
/// opinion into a single <see cref="StaticAnalysisReport"/>.
/// </summary>
/// <remarks>
/// An analyzer that throws is logged and skipped rather than failing the grading. The verifier sits off
/// the routing hot path precisely so a defect in a heuristic cannot become a routing failure, and a
/// half-formed snippet is exactly the kind of input that finds a parser's edge cases.
/// </remarks>
public sealed class CompositeStaticAnalyzer : IStaticAnalyzer
{
    private readonly IReadOnlyList<IStaticAnalyzer> _analyzers;
    private readonly ILogger<CompositeStaticAnalyzer> _logger;

    /// <summary>Initializes a new instance of the <see cref="CompositeStaticAnalyzer"/> class.</summary>
    /// <param name="analyzers">The analyzers to compose, in registration order.</param>
    /// <param name="logger">The logger.</param>
    public CompositeStaticAnalyzer(IEnumerable<IStaticAnalyzer> analyzers, ILogger<CompositeStaticAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        ArgumentNullException.ThrowIfNull(logger);

        _analyzers = [.. analyzers.Where(a => a is not CompositeStaticAnalyzer)];
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "composite";

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        return Analyze(code: code, language: language, prompt: string.Empty);
    }

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language, string prompt)
    {
        var report = Report(code: code, language: language, prompt: prompt);
        return report.Score is { } score
            ? new StaticAnalysisFinding(Analyzer: Name, Score: score, Notes: report.Notes)
            : null;
    }

    /// <summary>Runs every analyzer and composes their findings into one report.</summary>
    /// <param name="code">The source code to analyze.</param>
    /// <param name="language">The detected language of <paramref name="code"/>.</param>
    /// <param name="prompt">
    /// The user prompt <paramref name="code"/> was produced in answer to, passed to each analyzer's
    /// prompt-aware overload; empty when unavailable, which every analyzer that ignores it treats as
    /// equivalent to not having it at all.
    /// </param>
    /// <returns>The composed report; its score is null when every analyzer abstained.</returns>
    public StaticAnalysisReport Report(string code, CodeLanguage language, string prompt = "")
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(prompt);

        var sum = 0.0;
        var count = 0;
        var notes = new List<string>();

        foreach (var analyzer in _analyzers)
        {
            StaticAnalysisFinding? finding;
            try
            {
                finding = analyzer.Analyze(code: code, language: language, prompt: prompt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    exception: ex,
                    message: "Static analyzer {Analyzer} threw on a {Language} snippet; skipping its contribution.",
                    analyzer.Name,
                    language);
                continue;
            }

            if (finding is null) continue;

            sum += Math.Clamp(value: finding.Score, 0.0, 1.0);
            count++;
            notes.AddRange(finding.Notes.Select(n => $"{finding.Analyzer}: {n}"));
        }

        return count == 0
            ? new StaticAnalysisReport(null, Notes: [])
            : new StaticAnalysisReport(Score: sum / count, Notes: notes);
    }
}