namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// The type of AI provider, used to categorize providers and apply provider-specific defaults and validation rules.
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// A custom or unknown provider. No provider-specific defaults or validation rules are applied.
    /// </summary>
    Other = 0,

    /// <summary>
    /// An Anthropic provider, typically pointing to https://api.anthropic.com or a compatible endpoint.
    /// </summary>
    Anthropic = 1,

    /// <summary>
    /// An OpenAI provider, typically pointing to https://api.openai.com or a compatible endpoint.
    /// </summary>
    OpenAI = 2
}
