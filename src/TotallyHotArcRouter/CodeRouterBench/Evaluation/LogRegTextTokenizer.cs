using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The single tokenization rule shared by <see cref="LogRegTrainer"/> (training) and this phase's
/// <c>LogReg</c> comparison baseline (inference), and also by
/// <see cref="Router.Orchestrator.ClusterTermExtractor"/> for cluster-naming TF-IDF: lowercase,
/// alphanumeric runs of at least two characters. Kept in one place because training and inference must
/// tokenize identically for a fixed vocabulary's indices to mean the same thing at both ends (PLAN.md
/// Phase L's "training and inference both in .NET"). Relocated from <c>Router.Orchestrator</c>
/// (docs/router/regret-evaluation-harness-plan.md's "Namespace and layout") once the live <c>logreg</c>
/// voter moved to <see cref="Router.Orchestrator.EmbeddingLogRegModelArtifact"/> and this TF-IDF design
/// became Phase N's static comparison baseline rather than router-voter infrastructure.
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

    /// <summary>The compiled pattern for a token: a run of at least two lowercase letters or digits.</summary>
    /// <returns>The generated, culture-invariant <see cref="Regex"/> instance.</returns>
    [GeneratedRegex(@"[a-z0-9]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
