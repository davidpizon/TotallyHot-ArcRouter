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
/// The numeric values are explicit and must not be renumbered: the selected member is persisted by
/// <em>name</em> on <c>ProviderOptions.ProviderType</c>, and an unparseable name falls back to
/// <see cref="Other"/>, so renaming a member silently downgrades every provider configured with it.
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
    /// Google's Gemini API. Authenticates with a raw key in <c>x-goog-api-key</c>; the alternative
    /// <c>?key=</c> query-string form is deliberately not offered, since the header form is supported
    /// everywhere and keeps the credential out of URLs and logs.
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
