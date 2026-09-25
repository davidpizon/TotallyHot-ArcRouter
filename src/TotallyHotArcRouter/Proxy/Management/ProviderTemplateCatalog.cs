using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Projects <c>ModelRouting:Providers</c> into the add-provider template list. A template no longer names
/// a "credential header": authentication is just another header, and a header that is a secret says so with
/// its <see cref="ProviderHeader.Locked"/> flag (docs/adr/0016-remove-authheadername-and-mark-secrets-per-header.md).
/// A locked header is projected as its name and flag with no literal value - the value is a secret and is
/// omitted - so the editor shows an empty locked row for the operator to fill in; its environment-variable
/// name, which is not a secret, is kept and becomes the row's starting source. An unlocked literal is kept
/// as is. A template with no credential (an unauthenticated local runtime such as Ollama, or an SDK-signed
/// provider such as Bedrock) simply declares no such header, so it projects none. The key
/// <see cref="ModelRoutingOptions.ReservedBlankProviderKey"/> is rejected: the dialog uses that spelling for
/// the blank choice, so a configured entry with the same key could never be selected.
/// </summary>
internal static class ProviderTemplateCatalog
{
    /// <summary>Projects <paramref name="options"/> in dictionary order.</summary>
    /// <param name="options">The appsettings-bound routing section. Not the live provider store.</param>
    /// <returns>One template per provider key.</returns>
    internal static IReadOnlyList<ProjectedProviderTemplate> Project(ModelRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var key in options.Providers.Keys)
            if (ModelRoutingOptions.IsReservedBlankProviderKey(key))
                throw new InvalidOperationException(
                    $"Provider key '{key}' is reserved for the blank add-provider template and cannot be configured.");

        var templates = new List<ProjectedProviderTemplate>(options.Providers.Count);
        foreach (var (key, provider) in options.Providers)
        {
            var headers = new List<ProjectedProviderTemplateHeader>();
            foreach (var header in provider.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name)) continue;

                // A locked literal is a secret: its value is never projected, only the fact that this
                // header is a secret once the operator sets one.
                var literal = string.IsNullOrEmpty(header.Value) || header.Locked ? null : header.Value;
                var envVar = string.IsNullOrWhiteSpace(header.ValueEnvVar) ? null : header.ValueEnvVar.Trim();

                // A locked header is worth projecting even with nothing to prefill: the empty locked row is
                // the template telling the editor "a secret goes here".
                if (literal is null && envVar is null && !header.Locked)
                    continue;

                headers.Add(new ProjectedProviderTemplateHeader(
                    Name: header.Name.Trim(),
                    Value: literal,
                    ValueEnvVar: envVar,
                    Locked: header.Locked));
            }

            templates.Add(new ProjectedProviderTemplate(
                Key: key,
                BaseUrl: provider.BaseUrl,
                IsFree: provider.IsFree,
                Headers: headers));
        }

        return templates;
    }
}

/// <summary>One projected add-provider template.</summary>
/// <param name="Key">The <c>ModelRouting:Providers</c> key.</param>
/// <param name="BaseUrl">The template base URL.</param>
/// <param name="IsFree">Whether the template provider costs nothing.</param>
/// <param name="Headers">The headers the editor inserts, including any empty locked (secret) rows.</param>
internal sealed record ProjectedProviderTemplate(
    string Key,
    string BaseUrl,
    bool IsFree,
    IReadOnlyList<ProjectedProviderTemplateHeader> Headers);

/// <summary>A header on a projected template.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The literal value, or <see langword="null"/> when the value comes from the environment or is a secret.</param>
/// <param name="ValueEnvVar">The environment-variable name, or <see langword="null"/> for a literal header.</param>
/// <param name="Locked">Whether the value is a secret to be stored write-only once the operator sets one.</param>
internal sealed record ProjectedProviderTemplateHeader(string Name, string? Value, string? ValueEnvVar, bool Locked);
