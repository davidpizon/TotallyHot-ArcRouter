using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Sandbox.Redaction;

/// <summary>
/// Scrubs likely secrets from captured stdout/stderr before they are stored on a result or logged. Guest
/// output can echo prompt content, which may include tokens/keys; this is a defense-in-depth scrub, not a
/// guarantee. Patterns use a bounded match timeout to avoid pathological backtracking.
/// </summary>
public static class OutputRedactor
{
    private const string Placeholder = "[REDACTED]";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex[] Patterns =
    [
        // PEM private key blocks.
        new Regex("-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----.*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----",
            RegexOptions.Singleline | RegexOptions.CultureInvariant, MatchTimeout),
        // Bearer/authorization tokens.
        new Regex("(?i)bearer\\s+[A-Za-z0-9._\\-]+", RegexOptions.CultureInvariant, MatchTimeout),
        // OpenAI-style secret keys.
        new Regex("sk-[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, MatchTimeout),
        // AWS access key ids.
        new Regex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant, MatchTimeout),
        // key/secret/token/password assignments.
        new Regex("(?i)(?:api[_-]?key|apikey|secret|token|password)\\s*[:=]\\s*\\S+",
            RegexOptions.CultureInvariant, MatchTimeout),
    ];

    /// <summary>Returns <paramref name="text"/> with any recognized secrets replaced by a placeholder.</summary>
    /// <param name="text">The captured output to scrub.</param>
    /// <returns>The scrubbed text (empty when the input is null/empty).</returns>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var pattern in Patterns)
        {
            try
            {
                result = pattern.Replace(result, Placeholder);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological input timed out one pattern; keep scrubbing with the rest.
            }
        }

        return result;
    }
}

