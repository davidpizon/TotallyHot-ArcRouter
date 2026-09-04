namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// The type of AI provider, used to categorize providers and apply provider-specific defaults and validation rules.
/// <para>
/// Members are <em>families</em>, not individual vendors. Every endpoint that authenticates the same way
/// shares one member, because that is the only axis this enum drives: <see cref="ProviderTemplates"/> maps
/// each member to a base URL, an auth-header shape, and any headers the API requires. Two vendors whose
/// only difference is the base URL therefore need no second member - the operator edits the URL.
/// </para>
/// </summary>
/// <remarks>
/// The <em>names</em> are the compatibility surface, not the numbers: the selected member is persisted as
/// a string on <c>ProviderOptions.ProviderType</c>, and a name that no longer parses falls back to
/// <see cref="Other"/>. So renaming a member silently downgrades every provider already configured with
/// it, while renumbering is harmless - nothing serializes the numeric value. The values are written
/// explicitly only to keep them stable and reviewable, not because anything depends on them.
/// </remarks>
public enum ProviderType
{
    /// <summary>
    /// A custom or unknown remote API. Defaults to an <c>Authorization</c> header with no suggested
    /// credential, which is the most common shape for an API this list does not name.
    /// </summary>
    Other = 0,

    /// <summary>
    /// An Anthropic provider, typically pointing to https://api.anthropic.com or a compatible endpoint.
    /// Authenticates with a raw key in <c>x-api-key</c> and requires the <c>anthropic-version</c> header.
    /// </summary>
    Anthropic = 1,

    /// <summary>
    /// OpenAI <em>and every OpenAI-compatible API</em> - Groq, DeepSeek, xAI, Together, OpenRouter,
    /// Mistral, Fireworks, Perplexity, vLLM, and the rest. They are one member rather than many because
    /// they share the identical <c>Authorization: Bearer &lt;key&gt;</c> shape and differ only in base URL
    /// and environment-variable name, both of which the operator edits after picking this type.
    /// </summary>
    OpenAI = 2,

    /// <summary>
    /// Google's Gemini API, reached through its OpenAI-compatible endpoint and so authenticated with
    /// <c>Authorization: Bearer &lt;key&gt;</c> like every other member of that family.
    /// <para>
    /// Listed separately from <see cref="OpenAI"/> only because its base URL and environment-variable name
    /// differ, and operators look for it by name. Gemini's <em>native</em> API instead takes a raw key in
    /// <c>x-goog-api-key</c> (or a <c>?key=</c> query parameter), but neither is used here: the proxy
    /// forwards OpenAI-shaped requests, which the native endpoint does not accept.
    /// </para>
    /// </summary>
    GoogleGemini = 3,

    /// <summary>
    /// Azure OpenAI Service. Distinct from <see cref="OpenAI"/> despite the shared model family: it
    /// authenticates with a raw key in <c>api-key</c> rather than a bearer token, and its endpoint is
    /// per-resource so no default base URL can be suggested.
    /// </summary>
    AzureOpenAI = 4,

    /// <summary>
    /// Cohere's API. Shares <see cref="OpenAI"/>'s bearer-token shape but is listed separately because its
    /// base URL and environment-variable name differ, and operators look for it by name.
    /// </summary>
    Cohere = 5,

    /// <summary>
    /// A locally hosted runtime serving an OpenAI-compatible API - Ollama, LM Studio, llama.cpp, and
    /// similar. One member rather than three because they are configuration-identical apart from the
    /// port: unauthenticated by default and free by definition, so this is the only type that starts with
    /// authentication switched off.
    /// </summary>
    LocalRuntime = 6,

    /// <summary>
    /// Amazon Bedrock. The only type whose credential is not an HTTP header the operator supplies: the AWS
    /// SDK signs each request with SigV4 from the provider's AWS region and credential environment
    /// variables, so this type also starts with authentication switched off.
    /// </summary>
    Bedrock = 7
}