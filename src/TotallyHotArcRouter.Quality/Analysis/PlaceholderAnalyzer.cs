using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Detects code that gestures at an implementation instead of providing one: <c>TODO</c> markers, elision
/// comments, bare <c>pass</c> bodies, and <c>NotImplementedException</c> throws.
/// </summary>
/// <remarks>
/// This is the single most useful non-syntactic signal available without running anything. A response
/// that hands back a correct-looking skeleton whose body reads <c>// ... rest of the implementation ...</c>
/// parses perfectly, and under a syntax-only score it would grade identically to a complete answer. It is
/// also the failure mode that most distinguishes weaker models from stronger ones on the dimensions that
/// matter here, so leaving it unmeasured would blind the router to a real quality difference.
/// <para>
/// Scored as a graded penalty rather than a pass/fail: one <c>TODO</c> in an otherwise complete answer is
/// a note, while a body made entirely of stubs is close to worthless. The floor is 0.1 rather than 0 so
/// this axis alone cannot zero a snippet that is otherwise valid and complete.
/// </para>
/// </remarks>
public sealed partial class PlaceholderAnalyzer : IStaticAnalyzer
{
    /// <summary>Penalty deducted per distinct placeholder occurrence found.</summary>
    private const double PenaltyPerHit = 0.25;

    /// <summary>The lowest score this analyzer will report, so a placeholder alone cannot zero a snippet.</summary>
    private const double Floor = 0.1;

    /// <inheritdoc />
    public string Name => "placeholder";

    /// <inheritdoc />
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var notes = new List<string>();

        CountInto(notes, TodoMarker(), code, "TODO/FIXME marker");
        CountInto(notes, ElisionComment(), code, "elision comment standing in for code");
        CountInto(notes, NotImplemented(), code, "explicit not-implemented throw");

        if (language is CodeLanguage.Python)
        {
            CountInto(notes, BarePass(), code, "bare 'pass' body");
        }

        if (notes.Count == 0)
        {
            return new StaticAnalysisFinding(Name, 1.0, []);
        }

        var score = StaticAnalyzerScoring.ClampScore(Floor, PenaltyPerHit * notes.Count);
        return new StaticAnalysisFinding(Name, score, notes);
    }

    /// <summary>Adds one note per pattern that matched, recording how many times it did.</summary>
    private static void CountInto(List<string> notes, Regex pattern, string code, string description)
    {
        var count = pattern.Matches(code).Count;
        if (count > 0)
        {
            notes.Add($"{count} x {description}");
        }
    }

    [GeneratedRegex(@"\b(TODO|FIXME|XXX)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TodoMarker();

    // Matches a comment whose entire content is an ellipsis or an "implementation goes here" phrase - the
    // shapes a model reaches for when it declines to write the body. A bare "..." outside a comment is
    // deliberately not matched: it is legal Python (Ellipsis) and legal TypeScript (rest/spread).
    [GeneratedRegex(
        @"(//|#)\s*(\.\.\.|(\.\.\.\s*)?(rest of|remainder of|implementation|rest is|your code|code here|fill in|omitted|unchanged|same as)\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ElisionComment();

    [GeneratedRegex(
        @"\b(NotImplementedException|NotImplementedError|raise\s+NotImplemented|throw\s+new\s+Error\(\s*['""]not implemented)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotImplemented();

    // A 'pass' on its own line, which in a generated answer almost always marks an unwritten body.
    [GeneratedRegex(@"^\s*pass\s*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BarePass();
}
