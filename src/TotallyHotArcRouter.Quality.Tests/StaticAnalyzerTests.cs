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

    // ---- RelevanceAnalyzer ----

    [Fact]
    public void Relevance_NoPrompt_Abstains()
    {
        var finding = new RelevanceAnalyzer().Analyze(code: "public int Add(int a, int b) => a + b;",
            language: CodeLanguage.CSharp);

        Assert.Null(finding);
    }

    [Fact]
    public void Relevance_PromptTooShortForSignal_Abstains()
    {
        var finding = new RelevanceAnalyzer().Analyze(code: "public int Add(int a, int b) => a + b;",
            language: CodeLanguage.CSharp, prompt: "fix this");

        Assert.Null(finding);
    }

    [Fact]
    public void Relevance_CodeAddressesThePrompt_ScoresHigh()
    {
        // The tokenizer matches whole words, not camelCase sub-words, so the prompt's salient terms are
        // deliberately echoed as standalone words (in a comment) rather than relying on them appearing
        // split out of an identifier like CalculateInvoiceTotal.
        var finding = new RelevanceAnalyzer().Analyze(
            code: """
                  // Sums the invoice total across every line item.
                  public double CalculateInvoiceTotal(List<LineItem> lineItems) => lineItems.Sum(i => i.Price);
                  """,
            language: CodeLanguage.CSharp,
            prompt: "Write a function that calculates the invoice total from a list of line items.");

        Assert.NotNull(finding);
        Assert.Equal(1.0, actual: finding.Score);
    }

    [Fact]
    public void Relevance_CodeAnswersADifferentQuestion_ScoresBelowFullMarks()
    {
        var finding = new RelevanceAnalyzer().Analyze(
            code: "public void PrintGreeting() { Console.WriteLine(\"hello world\"); }",
            language: CodeLanguage.CSharp,
            prompt: "Write a function that calculates the invoice total from a list of line items.");

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score < 1.0, userMessage: "an unrelated snippet must not score full relevance");
    }

    [Fact]
    public void Relevance_NeverScoresBelowItsFloor()
    {
        var finding = new RelevanceAnalyzer().Analyze(
            code: "public void PrintGreeting() { Console.WriteLine(\"hello world\"); }",
            language: CodeLanguage.CSharp,
            prompt: "quantum blockchain neural astrophysics distributed microservice orchestration cryptography");

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score >= 0.3, userMessage: "relevance alone must never be able to zero a snippet");
    }

    // ---- SmellDensityAnalyzer ----

    [Fact]
    public void SmellDensity_TooFewLines_Abstains()
    {
        var finding = new SmellDensityAnalyzer().Analyze(code: "int x = 42;", language: CodeLanguage.CSharp);

        Assert.Null(finding);
    }

    [Fact]
    public void SmellDensity_CleanCode_ScoresOne()
    {
        const string code = """
                             public int Add(int a, int b)
                             {
                                 var sum = a + b;
                                 return sum;
                             }
                             """;

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Equal(1.0, actual: finding.Score);
        Assert.Empty(finding.Notes);
    }

    [Fact]
    public void SmellDensity_MagicNumbers_LowersScore()
    {
        const string code = """
                             public double ApplyDiscount(double price)
                             {
                                 var discounted = price * 0.8371;
                                 var withFee = discounted + 4.99;
                                 return withFee - 1.234;
                             }
                             """;

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score < 1.0);
        Assert.Contains(collection: finding.Notes, filter: n => n.Contains("magic number"));
    }

    [Fact]
    public void SmellDensity_EmptyCatchBlock_IsDetected()
    {
        const string code = """
                             public void Risky()
                             {
                                 try
                                 {
                                     DoWork();
                                 }
                                 catch (Exception)
                                 {
                                 }
                             }
                             """;

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Contains(collection: finding.Notes, filter: n => n.Contains("empty catch/except"));
    }

    [Fact]
    public void SmellDensity_EmptyExceptBlock_IsDetectedForPython()
    {
        const string code = """
                             def risky():
                                 try:
                                     do_work()
                                     do_more_work()
                                 except Exception:
                                     pass
                             """;

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.Python);

        Assert.NotNull(finding);
        Assert.Contains(collection: finding.Notes, filter: n => n.Contains("empty catch/except"));
    }

    [Fact]
    public void SmellDensity_LongParameterList_IsDetected()
    {
        const string code = """
                             public void Configure(int a, int b, int c, int d, int e, int f)
                             {
                                 Apply(a, b, c, d, e, f);
                             }
                             """;

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.Contains(collection: finding.Notes, filter: n => n.Contains("parameter/argument list"));
    }

    [Fact]
    public void SmellDensity_NeverScoresBelowItsFloor()
    {
        var lines = Enumerable.Range(0, 5).Select(i => $"var v{i} = {1000 + i} * {2000 + i} / {3000 + i};");
        var code = string.Join(separator: '\n', values: lines);

        var finding = new SmellDensityAnalyzer().Analyze(code: code, language: CodeLanguage.CSharp);

        Assert.NotNull(finding);
        Assert.True(condition: finding.Score >= 0.3, userMessage: "smell density alone must never be able to zero a snippet");
    }

    // ---- Composite prompt threading ----

    // The relevance analyzer needs the prompt but the other analyzers do not; the composite must pass it
    // through to whichever one asks for it without every analyzer needing to accept it.
    [Fact]
    public void Composite_PassesPromptThroughToAnalyzersThatWantIt()
    {
        var report = Composite(new RelevanceAnalyzer())
            .Report(code: "public int CalculateTotal() => 0;", language: CodeLanguage.CSharp,
                prompt: "calculate total invoice amount");

        Assert.NotNull(report.Score);
    }

    [Fact]
    public void Composite_NoPromptSupplied_AnalyzerNeedingOneAbstains()
    {
        var report = Composite(new RelevanceAnalyzer()).Report(code: "public int CalculateTotal() => 0;",
            language: CodeLanguage.CSharp);

        Assert.Null(report.Score);
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