namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Provides pre-configured templates for known provider types. When a user selects a provider type,
/// these templates pre-fill common configuration values like Base URL, auth headers, and credential mode.
/// </summary>
public static class ProviderTemplates
{
    /// <summary>
    /// A template for a provider type, containing default values for Base URL, auth headers, and credential mode.
    /// </summary>
    public sealed class ProviderTemplate
    {
        /// <summary>
        /// The provider's default base URL (e.g., <c>https://api.anthropic.com</c>). May be empty for the "Other" template.
        /// </summary>
        public required string BaseUrl { get; set; }

        /// <summary>
        /// The header name carrying the credential (e.g., <c>Authorization</c>, <c>x-api-key</c>).
        /// </summary>
        public required string AuthHeaderName { get; set; }

        /// <summary>
        /// The scheme prefixed to the credential (e.g., <c>Bearer</c>). Empty for a raw key.
        /// </summary>
        public required string AuthHeaderScheme { get; set; }

        /// <summary>
        /// The default credential mode for this provider (one of <see cref="ProviderCredentialModes"/>'s constants).
        /// </summary>
        public required string DefaultCredentialMode { get; set; }
    }

    /// <summary>
    /// A read-only dictionary of provider type to template mappings. Used to look up pre-fill values when a user selects a provider type.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProviderType, ProviderTemplate> Templates =
        new Dictionary<ProviderType, ProviderTemplate>
        {
            [ProviderType.Anthropic] = new ProviderTemplate
            {
                BaseUrl = "https://api.anthropic.com",
                AuthHeaderName = "x-api-key",
                AuthHeaderScheme = string.Empty,
                DefaultCredentialMode = ProviderCredentialModes.Literal
            },

            [ProviderType.OpenAI] = new ProviderTemplate
            {
                BaseUrl = "https://api.openai.com/v1",
                AuthHeaderName = "Authorization",
                AuthHeaderScheme = "Bearer",
                DefaultCredentialMode = ProviderCredentialModes.Literal
            },

            [ProviderType.Other] = new ProviderTemplate
            {
                BaseUrl = string.Empty,
                AuthHeaderName = "Authorization",
                AuthHeaderScheme = "Bearer",
                DefaultCredentialMode = ProviderCredentialModes.None
            }
        };
}
