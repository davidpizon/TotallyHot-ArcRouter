using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Replaces key-shaped strings in text before it is written to a body-excerpt log (and later, to
/// ADR-0019 session storage). Matching is deliberately pattern-based rather than secret-store-aware: the
/// body may contain credentials the router never held, and ADR-0019's privacy driver is "no secret ever
/// written to disk" for anything that looks like one. False positives on fixtures and example keys are
/// accepted (#184 plan §3.3; ADR-0019).
/// </summary>
public static partial class SecretObscurer
{
    /// <summary>The token substituted for each matched secret-shaped span.</summary>
    public const string RedactedToken = "[REDACTED]";

    /// <summary>
    /// Obscures every key-shaped span in <paramref name="text"/>. Null or empty input is returned
    /// unchanged. Safe to call on already-truncated body excerpts; the patterns do not need a look-back
    /// window for whole-line log text (ADR-0019's streaming pipeline can reuse the same patterns with a
    /// bounded window later).
    /// </summary>
    /// <param name="text">Text that may contain credentials or key-shaped strings.</param>
    /// <returns>The same text with matches replaced by <see cref="RedactedToken"/>.</returns>
    public static string Obscure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        return KeyShapedPattern().Replace(text, RedactedToken);
    }

    /// <summary>
    /// Common API-key and token shapes: OpenAI-style <c>sk-</c>, Slack <c>xox*</c>, GitHub <c>gh*</c>,
    /// Google <c>AIza</c>, <c>Bearer</c> tokens, and long high-entropy base64-ish runs.
    /// </summary>
    [GeneratedRegex(
        """
        (?xi)
        \bsk-[A-Za-z0-9_-]{16,}
        | \bxox[baprs]-[A-Za-z0-9-]{10,}
        | \bgh[pousr]_[A-Za-z0-9_]{20,}
        | \bAIza[0-9A-Za-z_-]{20,}
        | \bBearer\s+[A-Za-z0-9._\-+/=]{20,}
        | (?<![A-Za-z0-9+/])[A-Za-z0-9+/]{40,}={0,2}(?![A-Za-z0-9+/=])
        """,
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyShapedPattern();
}
