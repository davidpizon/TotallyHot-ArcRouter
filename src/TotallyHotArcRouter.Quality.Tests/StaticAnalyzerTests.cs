using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Quality.Analysis;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the in-process static analyzers that replaced the execution signal, and the composite that
/// averages them. The behavior that matters most across all of them is the abstention contract: an
/// analyzer with nothing to say returns null, and the composite drops it rather than scoring it zero.
/// </summary>
public class StaticAnalyzerTests
{
    private static CompositeStaticAnalyzer Composite(params IStaticAnalyzer[] analyzers)
    {
        return new CompositeStaticAnalyzer(analyzers: analyzers, logger: NullLogger<CompositeStaticAnalyzer>.Instance);
    }

    // ---- PlaceholderAnalyzer ----

    [Fact]
    public void Placeholder_CompleteCode_ScoresOne()
    {
        var finding = new PlaceholderAnalyzer().Analyze(
            code: "public class C { public int Add(int a, int b) => a + b; }", language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Equal(1.0, actual: finding.Score);
        Assert.Empty(finding.Notes);
    }

    [Theory]
    [InlineData("// TODO: finish this", CodeLanguage.CSharp)]
    [InlineData("throw new NotImplementedException();", CodeLanguage.CSharp)]
    [InlineData("// ... rest of the implementation ...", CodeLanguage.CSharp)]
    [InlineData("# your code here", CodeLanguage.Python)]
    [InlineData("raise NotImplementedError", CodeLanguage.Python)]
    public void Placeholder_StubMarkers_ScoreBelowOne(string code, CodeLanguage language)
    {
        var finding = new PlaceholderAnalyzer().Analyze(code: code, language: language);

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score < 1.0, userMessage: $"expected a penalty for: {code}");
        Assert.NotEmpty(finding.Notes);
    }

    // A bare `pass` is a Python stub, but in other languages the word is unremarkable - "pass" appears in
    // identifiers and strings everywhere. Scoping the rule to Python is what keeps it from firing on prose.
    [Fact]
    public void Placeholder_BarePass_OnlyPenalizedForPython()
    {
        var analyzer = new PlaceholderAnalyzer();
        const string code = "def f():\n    pass\n";

        Assert.True(analyzer.Analyze(code: code, language: CodeLanguage.Python)!.Score < 1.0);
        Assert.Equal(1.0, actual: analyzer.Analyze(code: code, language: CodeLanguage.CSharp)!.Score);
    }

    [Fact]
    public void Placeholder_ManyMarkers_NeverScoresBelowItsFloor()
    {
        var code = string.Join('\n', values: Enumerable.Repeat(element: "// TODO: fix", 50));

        var finding = new PlaceholderAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.InRange(actual: finding!.Score, 0.1, 1.0);
    }

    [Fact]
    public void Placeholder_EmptyCode_Abstains()
    {
        Assert.Null(new PlaceholderAnalyzer().Analyze(code: "   ", language: CodeLanguage.CSharp));
    }

    // ---- TruncationAnalyzer ----

    [Theory]
    [InlineData("var total = a +")]
    [InlineData("var items = new[] { 1, 2,")]
    [InlineData("/* explanation that never closes")]
    [InlineData("var name = \"unterminated")]
    public void Truncation_CutOffSnippets_ScoreZero(string code)
    {
        var finding = new TruncationAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Equal(0.0, actual: finding.Score);
        Assert.NotEmpty(finding.Notes);
    }

    [Fact]
    public void Truncation_CompleteSnippet_ScoresOne()
    {
        var finding = new TruncationAnalyzer().Analyze(code: "var total = a + b;", language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: finding!.Score);
    }

    [Fact]
    public void Truncation_ClosedBlockComment_IsNotTruncation()
    {
        var finding = new TruncationAnalyzer().Analyze(code: "/* fine */\nvar x = 1;", language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: finding!.Score);
    }

    [Fact]
    public void Truncation_EscapedQuote_DoesNotCountAsUnterminated()
    {
        var finding = new TruncationAnalyzer().Analyze(code: "var s = \"a\\\"b\";", language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: finding!.Score);
    }

    [Fact]
    public void Truncation_EmptyCode_Abstains()
    {
        Assert.Null(new TruncationAnalyzer().Analyze(code: string.Empty, language: CodeLanguage.CSharp));
    }

    // ---- ComplexityAnalyzer ----

    [Fact]
    public void Complexity_ShortSnippet_Abstains()
    {
        Assert.Null(new ComplexityAnalyzer().Analyze(code: "var x = 1;\nvar y = 2;", language: CodeLanguage.CSharp));
    }

    [Fact]
    public void Complexity_FlatReadableCode_ScoresOne()
    {
        var code = string.Join('\n', values: Enumerable.Range(0, 10).Select(i => $"var x{i} = {i};"));

        var finding = new ComplexityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: finding!.Score);
    }

    [Fact]
    public void Complexity_DeeplyNestedCode_ScoresBelowOneButNeverBelowItsFloor()
    {
        var lines = Enumerable.Range(0, 10).Select(i => new string(' ', count: i * 4) + $"if (a{i}) {{");
        var code = string.Join('\n', values: lines);

        var finding = new ComplexityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.True(finding!.Score < 1.0);
        Assert.InRange(actual: finding.Score, 0.5, 1.0);
        Assert.NotEmpty(finding.Notes);
    }

    // "if" inside a longer identifier is not a branch. Without the word-boundary check, ordinary code
    // would drift into the penalty band purely for its naming.
    [Fact]
    public void Complexity_BranchKeywordsInsideIdentifiers_AreNotCounted()
    {
        var code = string.Join('\n', values: Enumerable.Repeat(element: "var ifconfigForwarder = whileList;", 10));

        var finding = new ComplexityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: finding!.Score);
    }

    // ---- DiagnosticSeverityAnalyzer ----

    [Fact]
    public void Diagnostics_NonCSharp_Abstains()
    {
        Assert.Null(new DiagnosticSeverityAnalyzer().Analyze(code: "print(1)", language: CodeLanguage.Python));
        Assert.Null(new DiagnosticSeverityAnalyzer().Analyze(code: "const x = 1;", language: CodeLanguage.JavaScript));
    }

    [Fact]
    public void Diagnostics_CleanCSharp_ScoresOne()
    {
        var finding = new DiagnosticSeverityAnalyzer().Analyze(
            code: "public class C { public int Add(int a, int b) => a + b; }", language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Equal(1.0, actual: finding.Score);
    }

    // A snippet that parses but trips warnings is not the same answer as one that parses clean; this is
    // the whole reason the diagnostics axis exists separately from the syntax bit.
    [Fact]
    public void Diagnostics_CSharpWithParseWarnings_ScoresBelowOneAndQuotesThem()
    {
        // A #warning directive is one of the few diagnostics Roslyn raises at *parse* time, without a
        // compilation or resolved references - which is exactly the tier this analyzer works at.
        const string code = """
                            #warning this needs attention
                            public class C { public int M() => 1; }
                            """;

        var finding = new DiagnosticSeverityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score < 1.0, userMessage: $"expected a warning penalty, got {finding.Score}");
        Assert.InRange(actual: finding.Score, 0.2, 1.0);
        Assert.NotEmpty(finding.Notes);
    }

    [Fact]
    public void Diagnostics_ManyWarnings_NeverScoresBelowItsFloorAndBoundsItsNotes()
    {
        var code = string.Join(separator: Environment.NewLine, values: Enumerable.Repeat(element: "#warning noise", 30))
                   + Environment.NewLine
                   + "public class C { }";

        var finding = new DiagnosticSeverityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.InRange(actual: finding.Score, 0.2, 1.0);

        // One summary line plus at most five quoted diagnostics, so telemetry stays bounded.
        Assert.True(condition: finding.Notes.Count <= 6,
            userMessage: $"notes should be capped, got {finding.Notes.Count}");
    }

    [Fact]
    public void Diagnostics_EmptyCode_Abstains()
    {
        Assert.Null(new DiagnosticSeverityAnalyzer().Analyze(code: "   ", language: CodeLanguage.CSharp));
    }

    [Theory]
    [InlineData(CodeLanguage.Shell)]
    [InlineData(CodeLanguage.Unknown)]
    public void Diagnostics_OtherLanguages_Abstain(CodeLanguage language)
    {
        Assert.Null(new DiagnosticSeverityAnalyzer().Analyze(code: "echo hi", language: language));
    }

    [Fact]
    public void Analyzers_RejectNullCode()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PlaceholderAnalyzer().Analyze(code: null!, language: CodeLanguage.CSharp));
        Assert.Throws<ArgumentNullException>(() =>
            new TruncationAnalyzer().Analyze(code: null!, language: CodeLanguage.CSharp));
        Assert.Throws<ArgumentNullException>(() =>
            new ComplexityAnalyzer().Analyze(code: null!, language: CodeLanguage.CSharp));
        Assert.Throws<ArgumentNullException>(() =>
            new DiagnosticSeverityAnalyzer().Analyze(code: null!, language: CodeLanguage.CSharp));
    }

    // ---- CompositeStaticAnalyzer ----

    [Fact]
    public void Composite_AllAnalyzersAbstain_ReportsNullScore()
    {
        var report = Composite(new AlwaysAbstains()).Report(code: "anything", language: CodeLanguage.CSharp);

        Assert.Null(report.Score);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Composite_AveragesApplicableFindingsOnly()
    {
        var report = Composite(new Fixed(1.0), new Fixed(0.0), new AlwaysAbstains())
            .Report(code: "code", language: CodeLanguage.CSharp);

        // The abstention is dropped from the mean, not folded in as a zero.
        Assert.Equal(0.5, actual: report.Score);
    }

    // A defect in one heuristic must not fail the grading: the verifier sits off the routing hot path so
    // that a learning-path failure never looks like a routing failure.
    [Fact]
    public void Composite_AnalyzerThrows_IsSkippedRatherThanFailingTheReport()
    {
        var report = Composite(new Throws(), new Fixed(1.0)).Report(code: "code", language: CodeLanguage.CSharp);

        Assert.Equal(1.0, actual: report.Score);
    }

    [Fact]
    public void Composite_ClampsOutOfRangeFindings()
    {
        var report = Composite(new Fixed(5.0), new Fixed(-5.0)).Report(code: "code", language: CodeLanguage.CSharp);

        Assert.Equal(0.5, actual: report.Score);
    }

    [Fact]
    public void Composite_PrefixesNotesWithTheAnalyzerThatRaisedThem()
    {
        var report = Composite(new Fixed(0.5, note: "something to say"))
            .Report(code: "code", language: CodeLanguage.CSharp);

        Assert.Equal(expected: ["fixed: something to say"], actual: report.Notes);
    }

    /// <summary>An analyzer that never has an opinion, used to exercise the abstention path.</summary>
    private sealed class AlwaysAbstains : IStaticAnalyzer
    {
        public string Name => "abstains";

        public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
        {
            return null;
        }
    }

    /// <summary>An analyzer returning a fixed score, used to make the composite's arithmetic observable.</summary>
    private sealed class Fixed(double score, string? note = null) : IStaticAnalyzer
    {
        public string Name => "fixed";

        public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
        {
            return new StaticAnalysisFinding(Analyzer: Name, Score: score, Notes: note is null ? [] : [note]);
        }
    }

    /// <summary>An analyzer that throws, used to prove the composite contains the failure.</summary>
    private sealed class Throws : IStaticAnalyzer
    {
        public string Name => "throws";

        public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
        {
            throw new InvalidOperationException("boom");
        }
    }
}