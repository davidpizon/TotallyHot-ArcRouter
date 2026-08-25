using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Quality.Analysis;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Grades a single request end to end: parse the snippet for a syntax verdict, run the static analyzers
/// over it, and score the two into a unified value. Everything here is derived by reading the code.
/// </summary>
/// <remarks>
/// This type replaced an executor that ran the snippet in a jail or a microVM and scored what happened.
/// Nothing in this assembly can run model-generated code any more - there is no runtime, no process
/// launch, and no host-capability probe to gate one on - so the grade is whatever parsing and analysis can
/// prove, plus whatever the judge adds downstream through <see cref="IQualityScoreAggregator"/>.
/// </remarks>
public sealed class QualityGrader : IQualityGrader
{
    private readonly IStructuralParser _structuralParser;
    private readonly CompositeStaticAnalyzer _analyzer;
    private readonly IQualityScorer _scorer;
    private readonly ILogger<QualityGrader> _logger;

    /// <summary>Initializes a new instance of the <see cref="QualityGrader"/> class.</summary>
    /// <param name="structuralParser">The structural parser producing the syntax verdict.</param>
    /// <param name="analyzer">The composed static analyzers.</param>
    /// <param name="scorer">The quality scorer.</param>
    /// <param name="logger">The logger.</param>
    public QualityGrader(
        IStructuralParser structuralParser,
        CompositeStaticAnalyzer analyzer,
        IQualityScorer scorer,
        ILogger<QualityGrader> logger)
    {
        ArgumentNullException.ThrowIfNull(structuralParser);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(logger);

        _structuralParser = structuralParser;
        _analyzer = analyzer;
        _scorer = scorer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<QualityResult> GradeAsync(QualityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var syntax = _structuralParser.Check(request.Code, request.Language);
        var analysis = _analyzer.Report(request.Code, request.Language);

        var result = new QualityResult
        {
            RequestCorrelationId = request.CorrelationId,
            SessionId = request.SessionId,
            Dimension = request.Dimension,
            Model = request.Model,
            Language = request.Language.ToString(),
            SyntaxValid = syntax.IsValid,
            SyntaxAuthoritative = syntax.IsAuthoritative,
            AnalysisScore = analysis.Score,
            AnalysisFindings = analysis.Notes,
            DegradedReason = syntax.IsAuthoritative ? null : "heuristic-syntax-check",
        };

        result = result with { UnifiedScore = _scorer.Score(result, request.Dimension) };

        _logger.LogInformation(
            "Graded {Language} dim {Dimension} syntax={SyntaxValid} authoritative={Authoritative} -> u={Score:F3} (correlation {CorrelationId}).",
            request.Language,
            request.Dimension,
            result.SyntaxValid,
            result.SyntaxAuthoritative,
            result.UnifiedScore,
            request.CorrelationId);

        return Task.FromResult(result);
    }
}
