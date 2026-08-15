using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The single tokenization rule shared by <see cref="LogRegVoter"/> (inference) and
/// <see cref="CodeRouterBench.LogRegTrainer"/> (training): lowercase, alphanumeric runs of at least two
/// characters. Kept in one place because training and inference must tokenize identically for the
/// checked-in vocabulary's indices to mean the same thing at both ends (PLAN.md Phase L's "training and
/// inference both in .NET").
/// </summary>
public static partial class LogRegTextTokenizer
{
    /// <summary>
    /// Splits <paramref name="text"/> into lowercase alphanumeric tokens of at least two characters,
    /// discarding punctuation and single-character noise.
    /// </summary>
    /// <param name="text">The task text to tokenize.</param>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var matches = TokenPattern().Matches(text.ToLowerInvariant());
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            tokens.Add(match.Value);
        }

        return tokens;
    }

    [GeneratedRegex(@"[a-z0-9]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
