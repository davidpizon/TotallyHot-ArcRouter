using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Projects <c>ModelRouting:Providers</c> into the add-provider template list. The credential header
/// (the entry whose name matches <see cref="ProviderOptions.AuthHeaderName"/>) is not a custom header:
/// the editor records it as the auth header name and only pre-fills the remaining headers. A locked
/// literal value is a secret and is omitted; an environment-variable name on that same header is kept,
/// because the name is not the secret.
/// </summary>
internal static class ProviderTemplateCatalog
{
    /// <summary>Projects <paramref name="options"/> in dictionary order.</summary>
    /// <param name="options">The appsettings-bound routing section. Not the live provider store.</param>
    /// <returns>One template per provider key.</returns>
    internal static IReadOnlyList<ProjectedProviderTemplate> Project(ModelRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var templates = new List<ProjectedProviderTemplate>(options.Providers.Count);
        foreach (var (key, provider) in options.Providers)
        {
            var authHeaderName = string.IsNullOrWhiteSpace(provider.AuthHeaderName)
                ? "Authorization"
                : provider.AuthHeaderName.Trim();

            var headers = new List<ProjectedProviderTemplateHeader>();
            foreach (var header in provider.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name)) continue;
                if (string.Equals(a: header.Name.Trim(), b: authHeaderName, comparisonType: StringComparison.OrdinalIgnoreCase))
                    continue;

                var literal = string.IsNullOrEmpty(header.Value) ? null : header.Value;
                if (header.Locked)
                    literal = null;

                var envVar = string.IsNullOrWhiteSpace(header.ValueEnvVar) ? null : header.ValueEnvVar.Trim();
                if (literal is null && envVar is null)
                    continue;

                headers.Add(new ProjectedProviderTemplateHeader(
                    Name: header.Name.Trim(),
                    Value: literal,
                    ValueEnvVar: envVar));
            }

            templates.Add(new ProjectedProviderTemplate(
                Key: key,
                BaseUrl: provider.BaseUrl,
                AuthHeaderName: authHeaderName,
                IsFree: provider.IsFree,
                Headers: headers));
        }

        return templates;
    }
}

/// <summary>One projected add-provider template.</summary>
/// <param name="Key">The <c>ModelRouting:Providers</c> key.</param>
/// <param name="BaseUrl">The template base URL.</param>
/// <param name="AuthHeaderName">The credential header name.</param>
/// <param name="IsFree">Whether the template provider costs nothing.</param>
/// <param name="Headers">Custom headers other than <paramref name="AuthHeaderName"/>.</param>
internal sealed record ProjectedProviderTemplate(
    string Key,
    string BaseUrl,
    string AuthHeaderName,
    bool IsFree,
    IReadOnlyList<ProjectedProviderTemplateHeader> Headers);

/// <summary>A non-credential header on a projected template.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The literal value, or <see langword="null"/> when the value comes from the environment.</param>
/// <param name="ValueEnvVar">The environment-variable name, or <see langword="null"/> for a literal header.</param>
internal sealed record ProjectedProviderTemplateHeader(string Name, string? Value, string? ValueEnvVar);
