using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Analysis;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Parsing;
using TotallyHot.ArcRouter.Quality.Scoring;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers <see cref="QualityGrader"/>: the parse-analyze-score path that replaced the executing verifier.
/// </summary>
public class QualityGraderTests
{
    /// <summary>Builds a grader over the real parser and analyzers, so these tests exercise the shipped pipeline.</summary>
    private static QualityGrader CreateGrader(QualityOptions? options = null)
    {
        var opts = Options.Create(options ?? new QualityOptions());
        var analyzer = new CompositeStaticAnalyzer(
            analyzers:
            [
                new DiagnosticSeverityAnalyzer(),
                new PlaceholderAnalyzer(),
                new TruncationAnalyzer(),
                new ComplexityAnalyzer()
            ],
            logger: NullLogger<CompositeStaticAnalyzer>.Instance);

        return new QualityGrader(
            structuralParser: new StructuralParser(),
            analyzer: analyzer,
            scorer: new QualityScorer(opts),
            logger: NullLogger<QualityGrader>.Instance);
    }

    private static QualityRequest Request(string code, CodeLanguage language, string dimension = "code_generation")
    {
        return new QualityRequest(Code: code, Language: language, Prompt: "the prompt", Dimension: dimension,
            Model: "model-a",
            CorrelationId: "sess-1:1", SessionId: "sess-1");
    }

    [Fact]
    public async Task GradeAsync_RejectsNullRequest()
    {
        var grader = CreateGrader();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            grader.GradeAsync(request: null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GradeAsync_ValidCSharp_IsAuthoritativeAndScoresHigh()
    {
        var grader = CreateGrader();
        var code = "public class Calc { public static int Add(int a, int b) => a + b; }";

        var result = await grader.GradeAsync(request: Request(code: code, language: CodeLanguage.CSharp),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.SyntaxValid);
        Assert.True(result.SyntaxAuthoritative);
        Assert.Null(result.DegradedReason);
        Assert.True(condition: result.UnifiedScore > 0.9,
            userMessage: $"expected a high score, got {result.UnifiedScore}");
    }

    [Fact]
    public async Task GradeAsync_InvalidCSharp_IsInvalidAndScoresLow()
    {
        var grader = CreateGrader();
        var code = "public class Calc { public static int Add(int a, int b) { return a + ";

        var result = await grader.GradeAsync(request: Request(code: code, language: CodeLanguage.CSharp),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.SyntaxValid);
        Assert.True(result.SyntaxAuthoritative);
        Assert.True(condition: result.UnifiedScore < 0.5,
            userMessage: $"expected a low score, got {result.UnifiedScore}");
    }

    // Python has no managed parser, so its verdict is a heuristic. The grade must say so - both on the
    // result, for downstream consumers, and via the degraded reason - rather than presenting a bracket
    // count as though a compiler had spoken.
    [Fact]
    public async Task GradeAsync_Python_IsMarkedNonAuthoritative()
    {
        var grader = CreateGrader();

        var result = await grader.GradeAsync(
            request: Request(code: "def add(a, b):\n    return a + b\n", language: CodeLanguage.Python),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.SyntaxValid);
        Assert.False(result.SyntaxAuthoritative);
        Assert.Equal(expected: "heuristic-syntax-check", actual: result.DegradedReason);
    }

    [Fact]
    public async Task GradeAsync_JudgeAxisIsUnfilled()
    {
        var grader = CreateGrader();

        var result = await grader.GradeAsync(
            request: Request(code: "const x = 1;", language: CodeLanguage.JavaScript),
            cancellationToken: TestContext.Current.CancellationToken);

        // The grader never talks to the judge; the aggregator fills that axis later.
        Assert.Null(result.JudgeScore);
    }

    [Fact]
    public async Task GradeAsync_PlaceholderCode_ScoresBelowCompleteCode()
    {
        var grader = CreateGrader();

        var complete = await grader.GradeAsync(
            request: Request(code: "public class Calc\n{\n    public static int Add(int a, int b) => a + b;\n}",
                language: CodeLanguage.CSharp),
            cancellationToken: TestContext.Current.CancellationToken);

        var stub = await grader.GradeAsync(
            request: Request(
                code:
                "public class Calc\n{\n    public static int Add(int a, int b)\n    {\n        // TODO: implementation goes here\n        throw new NotImplementedException();\n    }\n}",
                language: CodeLanguage.CSharp),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: stub.SyntaxValid,
            userMessage: "the stub is supposed to be syntactically valid - that is the point");
        Assert.True(
            condition: stub.UnifiedScore < complete.UnifiedScore,
            userMessage:
            $"a stub ({stub.UnifiedScore}) must not grade as well as a real implementation ({complete.UnifiedScore})");
    }

    [Fact]
    public async Task GradeAsync_CarriesRoutingAttributionOntoTheResult()
    {
        var grader = CreateGrader();

        var result = await grader.GradeAsync(
            request: Request(code: "const x = 1;", language: CodeLanguage.JavaScript, dimension: "bug_fixing"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "sess-1:1", actual: result.RequestCorrelationId);
        Assert.Equal(expected: "sess-1", actual: result.SessionId);
        Assert.Equal(expected: "bug_fixing", actual: result.Dimension);
        Assert.Equal(expected: "model-a", actual: result.Model);
        Assert.Equal(expected: nameof(CodeLanguage.JavaScript), actual: result.Language);
    }

    [Fact]
    public async Task GradeAsync_HonorsCancellation()
    {
        var grader = CreateGrader();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            grader.GradeAsync(request: Request(code: "const x = 1;", language: CodeLanguage.JavaScript),
                cancellationToken: cts.Token));
    }
}