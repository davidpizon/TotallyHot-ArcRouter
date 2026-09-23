namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Add-provider template helpers. The dropdown entries themselves come from
/// <c>ModelRouting:Providers</c> (one option per configured key). This type keeps the blank
/// <see cref="Other"/> choice and the mapping from a provider stored before those keys existed.
/// </summary>
public static class ProviderTemplates
{
    /// <summary>
    /// The dropdown value for a provider the operator fills in without a template. The same spelling is
    /// reserved in the router as <c>ModelRoutingOptions.ReservedBlankProviderKey</c>, so a configured
    /// provider cannot use it.
    /// </summary>
    public const string OtherKey = "Other";

    /// <summary>
    /// The blank template: no base URL, no custom headers, and <c>Authorization</c> as the credential
    /// header name. Selecting it while the form is still untouched clears whatever the previous template
    /// filled in.
    /// </summary>
    public static ProviderEditorTemplate Other { get; } = new(
        Key: OtherKey,
        BaseUrl: string.Empty,
        AuthHeaderName: "Authorization",
        IsFree: false,
        Headers: []);

    /// <summary>
    /// Picks the dropdown key for a stored <c>ProviderType</c>. A key that is already in
    /// <paramref name="templateKeys"/> wins, case-insensitively. Legacy family names that identify one
    /// appsettings entry map onto that key when it is present: <c>Anthropic</c>, <c>OpenAI</c>, and
    /// <c>GoogleGemini</c>. <c>LocalRuntime</c>, <c>Bedrock</c>, <c>AzureOpenAI</c>, and <c>Cohere</c>
    /// do not identify a single entry, so they reopen as <see cref="OtherKey"/> and keep the saved fields.
    /// </summary>
    /// <param name="stored">The value persisted on the provider, or empty when none was stored.</param>
    /// <param name="templateKeys">The appsettings provider keys currently offered as templates.</param>
    /// <returns>A key from <paramref name="templateKeys"/>, or <see cref="OtherKey"/>.</returns>
    public static string ResolveStoredType(string? stored, IEnumerable<string> templateKeys)
    {
        ArgumentNullException.ThrowIfNull(templateKeys);

        if (string.IsNullOrWhiteSpace(stored)) return OtherKey;

        // Materialize once. Both lookups below walk the same key set, and the caller often passes a
        // deferred projection; enumerating that twice is wasted work and a Qodana warning.
        var keys = templateKeys.ToArray();
        var trimmed = stored.Trim();
        var direct = MatchKey(keys: keys, candidate: trimmed);
        if (direct is not null) return direct;

        var legacy = trimmed switch
        {
            nameof(ProviderType.Anthropic) => "anthropic",
            nameof(ProviderType.OpenAI) => "openai",
            nameof(ProviderType.GoogleGemini) => "gemini",
            _ => null
        };

        if (legacy is not null)
        {
            var mapped = MatchKey(keys: keys, candidate: legacy);
            if (mapped is not null) return mapped;
        }

        return OtherKey;
    }

    /// <summary>
    /// Returns the catalog key that matches <paramref name="candidate"/>, ignoring case, so the dropdown
    /// shows the configured spelling rather than the stored one.
    /// </summary>
    /// <param name="keys">The appsettings provider keys currently offered as templates.</param>
    /// <param name="candidate">The stored type or a legacy family name to look up.</param>
    /// <returns>The matching key, or <see langword="null"/> when none matches.</returns>
    private static string? MatchKey(IEnumerable<string> keys, string candidate)
    {
        foreach (var key in keys)
            if (string.Equals(a: key, b: candidate, comparisonType: StringComparison.OrdinalIgnoreCase))
                return key;

        return null;
    }

    /// <summary>
    /// A static HTTP header a template adds to the editor's custom-header rows. The credential header
    /// is not one of these: it is identified by <see cref="ProviderEditorTemplate.AuthHeaderName"/> and
    /// is not pre-filled as a row.
    /// </summary>
    /// <param name="Name">The header name (e.g. <c>anthropic-version</c>).</param>
    /// <param name="Value">The literal value, when the header is not read from the environment.</param>
    /// <param name="ValueEnvVar">The environment-variable name, when <paramref name="Value"/> is empty.</param>
    public sealed record ProviderTemplateHeader(string Name, string? Value, string? ValueEnvVar = null);

    /// <summary>
    /// One add-provider template: the defaults applied when its key is selected in the editor.
    /// </summary>
    /// <param name="Key">The <c>ModelRouting:Providers</c> key, or <see cref="OtherKey"/>.</param>
    /// <param name="BaseUrl">The provider base URL. Empty for <see cref="Other"/>.</param>
    /// <param name="AuthHeaderName">The header name that carries the credential.</param>
    /// <param name="IsFree">Whether requests to this provider cost nothing.</param>
    /// <param name="Headers">Custom headers other than <paramref name="AuthHeaderName"/>.</param>
    public sealed record ProviderEditorTemplate(
        string Key,
        string BaseUrl,
        string AuthHeaderName,
        bool IsFree,
        IReadOnlyList<ProviderTemplateHeader> Headers);
}
