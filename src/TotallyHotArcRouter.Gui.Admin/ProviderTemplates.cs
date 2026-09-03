namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Provides pre-configured templates for known provider types. When a user selects a provider type,
/// these templates pre-fill the base URL, the authentication shape, and any headers the provider's API
/// requires, so that the common case is a two-field edit (name and credential) rather than a research
/// exercise into the vendor's documentation.
/// </summary>
public static class ProviderTemplates
{
    /// <summary>
    /// A read-only dictionary of provider type to template mappings, used to look up pre-fill values when a
    /// user selects a provider type. Every <see cref="ProviderType"/> member has an entry, so a lookup
    /// never needs a fallback.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProviderType, ProviderTemplate> Templates =
        new Dictionary<ProviderType, ProviderTemplate>
        {
            [ProviderType.Anthropic] = new()
            {
                DisplayName = "Anthropic",
                BaseUrl = "https://api.anthropic.com",
                RequiresAuth = true,
                AuthHeaderName = "x-api-key",
                SuggestedEnvValue = "ANTHROPIC_API_KEY",
                DefaultsToFree = false,
                DefaultHeaders = [new ProviderTemplateHeader(Name: "anthropic-version", Value: "2023-06-01")]
            },

            [ProviderType.OpenAI] = new()
            {
                DisplayName = "OpenAI / Groq / DeepSeek",
                BaseUrl = "https://api.openai.com/v1",
                RequiresAuth = true,
                AuthHeaderName = "Authorization",
                SuggestedEnvValue = "Bearer {env:OPENAI_API_KEY}",
                DefaultsToFree = false,
                DefaultHeaders = []
            },

            [ProviderType.GoogleGemini] = new()
            {
                DisplayName = "Google Gemini",
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
                RequiresAuth = true,
                AuthHeaderName = "Authorization",
                SuggestedEnvValue = "Bearer {env:GEMINI_API_KEY}",
                DefaultsToFree = false,
                DefaultHeaders = []
            },

            [ProviderType.AzureOpenAI] = new()
            {
                DisplayName = "Azure OpenAI",
                // Per-resource (https://<resource>.openai.azure.com/openai/v1), so there is no default to
                // suggest - a wrong one is worse than an empty box the operator must fill.
                BaseUrl = string.Empty,
                RequiresAuth = true,
                AuthHeaderName = "api-key",
                SuggestedEnvValue = "AZURE_OPENAI_API_KEY",
                DefaultsToFree = false,
                DefaultHeaders = []
            },

            [ProviderType.Cohere] = new()
            {
                DisplayName = "Cohere",
                BaseUrl = "https://api.cohere.ai/compatibility/v1",
                RequiresAuth = true,
                AuthHeaderName = "Authorization",
                SuggestedEnvValue = "Bearer {env:COHERE_API_KEY}",
                DefaultsToFree = false,
                DefaultHeaders = []
            },

            [ProviderType.LocalRuntime] = new()
            {
                DisplayName = "Ollama / LM Studio / llama.cpp",
                // Ollama's OpenAI-compatible mount. LM Studio (1234) and llama.cpp (8080) differ only in
                // port, which is a one-token edit.
                BaseUrl = "http://localhost:11434/v1",
                RequiresAuth = false,
                AuthHeaderName = string.Empty,
                SuggestedEnvValue = string.Empty,
                DefaultsToFree = true,
                DefaultHeaders = [],
                AuthHint =
                    "Local runtimes accept unauthenticated requests by default. Tick this only if you have put the runtime behind a proxy that requires a token."
            },

            [ProviderType.Bedrock] = new()
            {
                DisplayName = "AWS Bedrock",
                // Informational only: the AWS SDK computes the real endpoint and signs each request itself.
                // Present because provider validation requires an absolute BaseUrl.
                BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
                RequiresAuth = false,
                AuthHeaderName = string.Empty,
                SuggestedEnvValue = string.Empty,
                DefaultsToFree = false,
                DefaultHeaders = [],
                AuthHint =
                    "Bedrock requests are signed by the AWS SDK (SigV4) using the provider's AWS region and credential environment variables, not an HTTP header."
            },

            [ProviderType.Other] = new()
            {
                DisplayName = "Other",
                BaseUrl = string.Empty,
                // "Other" now means an unknown *remote* API: every unauthenticated case (local runtimes,
                // Bedrock) has its own type, so requiring a credential is the better default here.
                RequiresAuth = true,
                AuthHeaderName = "Authorization",
                SuggestedEnvValue = string.Empty,
                DefaultsToFree = false,
                DefaultHeaders = []
            }
        };

    /// <summary>
    /// Every provider type in the order the editor's dropdown lists them: the named vendors first, then the
    /// local/self-signed types, with <see cref="ProviderType.Other"/> last as the fallback. Rendering from
    /// this list rather than hardcoded markup keeps the option list and the templates from drifting apart.
    /// </summary>
    public static IReadOnlyList<ProviderType> Ordered { get; } =
    [
        ProviderType.Anthropic,
        ProviderType.OpenAI,
        ProviderType.GoogleGemini,
        ProviderType.AzureOpenAI,
        ProviderType.Cohere,
        ProviderType.LocalRuntime,
        ProviderType.Bedrock,
        ProviderType.Other
    ];

    /// <summary>
    /// The dropdown label for a provider type, falling back to the member's own name for a type with no
    /// registered template (which <see cref="Templates"/>' full coverage makes unreachable today, but keeps
    /// this total rather than throwing if a member is ever added without one).
    /// </summary>
    /// <param name="providerType">The type to label.</param>
    /// <returns>The human-readable label, e.g. <c>OpenAI / Groq / DeepSeek</c>.</returns>
    public static string DisplayName(ProviderType providerType)
    {
        return Templates.TryGetValue(key: providerType, value: out var template)
            ? template.DisplayName
            : providerType.ToString();
    }

    /// <summary>
    /// A static HTTP header a provider's API requires regardless of credentials, seeded into the editor's
    /// custom-header rows when its template is applied.
    /// </summary>
    /// <param name="Name">The header name (e.g. <c>anthropic-version</c>).</param>
    /// <param name="Value">The literal value to send.</param>
    public sealed record ProviderTemplateHeader(string Name, string Value);

    /// <summary>
    /// A template for a provider type: the defaults the provider editor applies when that type is selected.
    /// </summary>
    public sealed class ProviderTemplate
    {
        /// <summary>
        /// The label shown in the editor's Provider Type dropdown. Distinct from the enum member's name
        /// because a member covers a family: <see cref="ProviderType.OpenAI"/> displays as
        /// "OpenAI / Groq / DeepSeek" so operators recognize their own provider in the list, while the
        /// persisted value stays the member name.
        /// </summary>
        public required string DisplayName { get; init; }

        /// <summary>
        /// The provider's default base URL (e.g. <c>https://api.anthropic.com</c>). Empty when the endpoint
        /// is per-tenant and no useful default exists (Azure OpenAI, <see cref="ProviderType.Other"/>).
        /// </summary>
        public required string BaseUrl { get; init; }

        /// <summary>
        /// Whether this provider authenticates with a credential the operator supplies. False for
        /// <see cref="ProviderType.LocalRuntime"/> (unauthenticated by default) and
        /// <see cref="ProviderType.Bedrock"/> (signed by the AWS SDK, not by a header), which is what
        /// leaves the editor's Credentials fieldset switched off for those types.
        /// </summary>
        public required bool RequiresAuth { get; init; }

        /// <summary>
        /// The header name carrying the credential (e.g. <c>Authorization</c>, <c>x-api-key</c>). Empty
        /// when <see cref="RequiresAuth"/> is false.
        /// </summary>
        public required string AuthHeaderName { get; init; }

        /// <summary>
        /// The credential value suggested (as placeholder text, never pre-filled) when the credential is
        /// sourced from an environment variable - either a bare variable name such as
        /// <c>ANTHROPIC_API_KEY</c>, or a template such as <c>Bearer {env:OPENAI_API_KEY}</c> when the API
        /// expects a scheme prefix. See <see cref="AuthValueTemplate"/> for the syntax. Empty when
        /// <see cref="RequiresAuth"/> is false or no conventional variable name exists.
        /// </summary>
        public required string SuggestedEnvValue { get; init; }

        /// <summary>
        /// Whether this provider's requests cost nothing, seeding the editor's "Free provider" checkbox.
        /// True only for <see cref="ProviderType.LocalRuntime"/>.
        /// </summary>
        public required bool DefaultsToFree { get; init; }

        /// <summary>
        /// Static headers the provider's API requires, merged into the editor's custom-header rows. This is
        /// how Anthropic's mandatory <c>anthropic-version</c> reaches a newly created provider: without it
        /// the endpoint answers 400 to every request and the operator has no indication why.
        /// </summary>
        public required IReadOnlyList<ProviderTemplateHeader> DefaultHeaders { get; init; }

        /// <summary>
        /// An explanation shown beneath the editor's Requires Authentication checkbox, for types where the
        /// absence of a credential needs justifying, or <see langword="null"/> for the self-evident cases.
        /// </summary>
        public string? AuthHint { get; init; }
    }
}