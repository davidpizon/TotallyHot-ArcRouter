using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Reduces a <c>User-Agent</c> header to a harness token that is safe to store: the leading product token and
/// its version when the product is on a fixed allowlist, otherwise <c>other:</c> plus the header's length. The
/// raw header is never stored, because it can carry a hostname, a user name or a proxy's fingerprint. A pure
/// function, so the capture path and the later structural census (tracked-todos #8) apply one rule without
/// sharing a writer.
/// </summary>
public static partial class HarnessToken
{
    /// <summary>The products that are recorded by name; compared case-insensitively.</summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-cli",
        "claude-code",
        "codex_cli_rs",
    };

    /// <summary>
    /// Normalizes a <c>User-Agent</c> value.
    /// </summary>
    /// <param name="userAgent">The raw header, or <see langword="null"/> when absent.</param>
    /// <returns>
    /// <c>product/version</c> (lower-case product) for an allowlisted product, <c>product</c> alone when the
    /// version is missing or not a plain version string, or <c>other:&lt;length&gt;</c> for anything else.
    /// </returns>
    public static string Normalize(string? userAgent)
    {
        var length = userAgent?.Length ?? 0;
        if (userAgent is null) return $"other:{length}";

        var match = LeadingProduct().Match(userAgent);
        if (!match.Success || !Allowlist.Contains(match.Groups["product"].Value))
        {
            return $"other:{length}";
        }

        var product = match.Groups["product"].Value.ToLowerInvariant();
        return match.Groups["version"].Success ? $"{product}/{match.Groups["version"].Value}" : product;
    }

    /// <summary>
    /// The first product token of a <c>User-Agent</c>, optionally followed by <c>/version</c> where the version
    /// is digits, letters, dots, dashes and plus signs only (no spaces, so no comment text can ride along).
    /// </summary>
    [GeneratedRegex(@"^\s*(?<product>[A-Za-z0-9_\-]+)(?:/(?<version>[0-9A-Za-z.+\-]{1,32}))?(?=[\s;(/]|$)")]
    private static partial Regex LeadingProduct();
}
