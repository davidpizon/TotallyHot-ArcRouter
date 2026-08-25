namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Detects a snippet that stops mid-thought - the signature of a response that hit a token ceiling.
/// </summary>
/// <remarks>
/// Truncation is worth measuring separately from syntax even though a truncated snippet usually fails to
/// parse too. The distinction is diagnostic: "this model wrote invalid code" and "this model ran out of
/// room" are different failures with different fixes, and only the first should count against the model's
/// competence. Reporting them as one number would let a low output-token ceiling look like a bad model.
/// <para>
/// The checks are deliberately shallow - unterminated block comment, a final line ending mid-expression,
/// an unbalanced quote on that line. Anything deeper would be re-implementing the parser that already ran.
/// </para>
/// </remarks>
public sealed class TruncationAnalyzer : IStaticAnalyzer
{
    /// <summary>Characters that, ending the final non-empty line, indicate the statement was still going.</summary>
    private static readonly char[] ContinuationEnders =
        ['+', '-', '*', '/', '%', '=', '<', '>', '&', '|', '^', ',', '.', ':'];

    /// <inheritdoc />
    public string Name => "truncation";

    /// <inheritdoc />
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var notes = new List<string>();

        if (HasUnterminatedBlockComment(code))
        {
            notes.Add("unterminated block comment");
        }

        var lastLine = code
            .Split('\n')
            .Select(l => l.TrimEnd('\r').TrimEnd())
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (lastLine is { Length: > 0 })
        {
            var last = lastLine[^1];

            // A colon ending the last line is how a Python block header legitimately looks, so it only
            // reads as truncation when the body that must follow it is missing - which it is, here, since
            // this is the final line of the snippet.
            if (ContinuationEnders.Contains(last))
            {
                notes.Add($"final line ends on '{last}', mid-expression");
            }

            if (CountUnescaped(lastLine, '"') % 2 != 0 || CountUnescaped(lastLine, '\'') % 2 != 0)
            {
                notes.Add("final line has an unterminated string literal");
            }
        }

        // Nothing suspicious is a positive finding, not an abstention: "this does not look truncated" is
        // real evidence, and withholding it would drop the axis for every healthy snippet.
        return notes.Count == 0
            ? new StaticAnalysisFinding(Name, 1.0, [])
            : new StaticAnalysisFinding(Name, 0.0, notes);
    }

    /// <summary>Determines whether a C-style block comment was opened and never closed.</summary>
    private static bool HasUnterminatedBlockComment(string code)
    {
        var open = code.LastIndexOf("/*", StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        return code.IndexOf("*/", open, StringComparison.Ordinal) < 0;
    }

    /// <summary>Counts occurrences of a quote character that are not backslash-escaped.</summary>
    private static int CountUnescaped(string line, char quote)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != quote)
            {
                continue;
            }

            var backslashes = 0;
            for (var j = i - 1; j >= 0 && line[j] == '\\'; j--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                count++;
            }
        }

        return count;
    }
}
