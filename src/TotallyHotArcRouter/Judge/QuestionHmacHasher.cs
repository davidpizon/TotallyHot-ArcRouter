using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Computes the install-local HMAC-SHA-256 over a whitespace-normalized user question (issue #293 plan D4).
/// The keyed hash avoids dictionary guessing against short prompts stored in SQLite.
/// </summary>
public sealed partial class QuestionHmacHasher
{
    /// <summary>Secret-store entry holding the 256-bit HMAC key.</summary>
    public const string SecretKeyName = "grading.question-hmac-key";

    private readonly ProtectedSecretStore _store;

    /// <summary>Initializes a new instance of the <see cref="QuestionHmacHasher"/> class.</summary>
    /// <param name="store">The protected secret store that holds the install-local key.</param>
    public QuestionHmacHasher(ProtectedSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Returns the lowercase hex HMAC when <paramref name="newestUserMessage"/> is present; otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="newestUserMessage">The newest user message before budget cutting, or obscured form.</param>
    /// <returns>A 64-character hex digest, or <see langword="null"/> when the question is missing.</returns>
    public string? TryCompute(string? newestUserMessage)
    {
        if (!GraderQuestionText.IsPresent(newestUserMessage)) return null;

        var normalized = NormalizeWhitespace(newestUserMessage!.Trim());
        var key = GetOrCreateKey();
        var payload = Encoding.UTF8.GetBytes(normalized);
        var digest = HMACSHA256.HashData(key: key, source: payload);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Collapses all whitespace runs to a single ASCII space.</summary>
    internal static string NormalizeWhitespace(string text) =>
        WhitespaceCollapse().Replace(input: text, replacement: " ");

    private byte[] GetOrCreateKey()
    {
        var encoded = _store.GetOrAdd(name: SecretKeyName,
            valueFactory: static () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return Convert.FromBase64String(encoded);
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceCollapse();
}
