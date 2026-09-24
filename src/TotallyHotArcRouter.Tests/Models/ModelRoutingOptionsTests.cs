using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers domain validation for <see cref="ModelRoutingOptions"/>.
/// </summary>
public class ModelRoutingOptionsTests
{
    [Fact]
    public void EnsureValid_EmptyConfiguration_DoesNotThrow()
    {
        var options = new ModelRoutingOptions();

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_ValidConfiguration_DoesNotThrow()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_Throws_WhenModelReferencesUnknownProvider()
    {
        var options = new ModelRoutingOptions
        {
            Providers = [],
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_Throws_WhenModelNameIsDuplicated()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" },
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4-b" }
            ]
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_Throws_WhenProviderBaseUrlIsNotAbsolute()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai"] = new() { BaseUrl = "not-a-url" }
            }
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_Throws_WhenModelNameIsMissing()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "", Provider = "openai", ProviderModelId = "gpt-5.4" }]
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_DoesNotThrow_WhenProviderKeyIsTheReservedBlankTemplateSpelling()
    {
        // "Other" is a valid live provider key: it is only reserved for the appsettings template
        // catalog (ProviderTemplateCatalog), not for the live store, since a pre-existing configuration
        // may legitimately use it as a dictionary key.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["Other"] = new() { BaseUrl = "https://example.invalid" }
            }
        };

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_Throws_WhenTwoProvidersShareTheSameDisplayName()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai-1"] = new() { Name = "My Provider", BaseUrl = "https://api.openai.com" },
                ["openai-2"] = new() { Name = "my provider", BaseUrl = "https://api.openai.com" }
            }
        };

        var ex = Assert.Throws<OptionsValidationException>(options.EnsureValid);
        Assert.Contains(expectedSubstring: "used by more than one provider", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureValid_DoesNotThrow_WhenMultipleProvidersHaveNoDisplayName()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai-1"] = new() { BaseUrl = "https://api.openai.com" },
                ["openai-2"] = new() { BaseUrl = "https://api.openai.com" }
            }
        };

        options.EnsureValid();
    }
}