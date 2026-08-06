using System.Diagnostics.CodeAnalysis;

namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Parses and composes the provider editor's credential <em>value template</em> - the single text box that
/// replaced the separate "Scheme" and "Env Var Name" fields.
/// <para>
/// A template is an optional literal prefix followed by one environment-variable reference, written either
/// as a bare name (<c>ANTHROPIC_API_KEY</c>) or braced (<c>Bearer {env:OPENAI_API_KEY}</c>). That one form
/// expresses every credential shape the known LLM APIs use: a raw key in a custom header, or a scheme-
/// prefixed bearer token in <c>Authorization</c>.
/// </para>
/// <para>
/// This is deliberately a <em>presentation</em> concern, not a storage format: <see cref="TryParse"/>
/// decomposes it into a scheme prefix and an environment-variable name, and <see cref="Compose"/> rebuilds
/// the template for display from that pair.
/// </para>
/// </summary>
/// <remarks>
/// Unused since the provider editor's authentication UI was replaced by an ordinary custom-header row (see
/// <c>ProviderEditDialog</c>) - kept only alongside its own tests pending a decision on whether to delete it.
/// </remarks>
public static class AuthValueTemplate
{
    private const string EnvOpen = "{env:";
    private const char EnvClose = '}';

    /// <summary>
    /// Decomposes a credential value template into the scheme prefix and the environment-variable name the
    /// proxy stores separately.
    /// </summary>
    /// <param name="template">
    /// The template as typed, e.g. <c>ANTHROPIC_API_KEY</c>, <c>Bearer {env:OPENAI_API_KEY}</c>, or
    /// <c>{env:AZURE_OPENAI_API_KEY}</c>.
    /// </param>
    /// <param name="scheme">
    /// On success, the literal prefix with its trailing whitespace removed (<c>Bearer</c>), or the empty
    /// string when the template is a bare reference.
    /// </param>
    /// <param name="envVarName">On success, the environment-variable name to read at request time.</param>
    /// <param name="error">On failure, a message naming what is wrong, suitable for display in the editor.</param>
    /// <returns>
    /// <see langword="true"/> when the template decomposes cleanly. <see langword="false"/> - rather than a
    /// best-effort guess - when it references no variable, references more than one, or puts literal text
    /// <em>after</em> the reference, because the storage shape cannot represent those and silently dropping
    /// the parts that don't fit would produce a provider that fails to authenticate with no explanation.
    /// </returns>
    public static bool TryParse(
        string? template,
        out string scheme,
        out string envVarName,
        [NotNullWhen(false)] out string? error)
    {
        scheme = string.Empty;
        envVarName = string.Empty;
        error = null;

        var trimmed = template?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Enter an environment variable name (e.g. ANTHROPIC_API_KEY) or a template such as Bearer {env:OPENAI_API_KEY}.";
            return false;
        }

        var open = trimmed.IndexOf(EnvOpen, StringComparison.Ordinal);
        if (open < 0)
        {
            // No braces: the whole string is the variable name. Reject embedded whitespace rather than
            // guessing, because "Bearer OPENAI_API_KEY" almost certainly means the braced form was intended
            // and storing "Bearer OPENAI_API_KEY" as a variable name would look correct while never
            // resolving.
            if (trimmed.Any(char.IsWhiteSpace))
            {
                error = "A bare value must be a single environment variable name. To add a prefix, write it as Bearer {env:VAR_NAME}.";
                return false;
            }

            envVarName = trimmed;
            return true;
        }

        var close = trimmed.IndexOf(EnvClose, open);
        if (close < 0)
        {
            error = "Unclosed {env:...} reference - add the closing brace.";
            return false;
        }

        if (trimmed.IndexOf(EnvOpen, close, StringComparison.Ordinal) >= 0)
        {
            error = "Only one {env:...} reference is supported per credential. Add a second credential row instead.";
            return false;
        }

        if (close != trimmed.Length - 1)
        {
            error = "Text after the {env:...} reference is not supported - the variable must come last.";
            return false;
        }

        var name = trimmed[(open + EnvOpen.Length)..close].Trim();
        if (name.Length == 0)
        {
            error = "The {env:...} reference is missing a variable name.";
            return false;
        }

        var prefix = trimmed[..open].TrimEnd();
        if (prefix.Any(char.IsWhiteSpace))
        {
            error = "The prefix before {env:...} must be a single word (e.g. Bearer).";
            return false;
        }

        scheme = prefix;
        envVarName = name;
        return true;
    }

    /// <summary>
    /// Rebuilds the display template from the stored scheme and environment-variable name - the inverse of
    /// <see cref="TryParse"/>, used to seed the editor when an existing provider is opened.
    /// </summary>
    /// <param name="scheme">The stored auth-header scheme; empty or null for a raw key.</param>
    /// <param name="envVarName">The stored environment-variable name.</param>
    /// <returns>
    /// A bare name when there is no scheme (<c>ANTHROPIC_API_KEY</c>), otherwise the braced form
    /// (<c>Bearer {env:OPENAI_API_KEY}</c>). The empty string when no variable is configured, so an
    /// unconfigured credential shows its placeholder rather than a stray <c>Bearer</c>.
    /// </returns>
    public static string Compose(string? scheme, string? envVarName)
    {
        if (string.IsNullOrWhiteSpace(envVarName))
        {
            return string.Empty;
        }

        var name = envVarName.Trim();
        return string.IsNullOrWhiteSpace(scheme) ? name : $"{scheme.Trim()} {EnvOpen}{name}{EnvClose}";
    }
}
