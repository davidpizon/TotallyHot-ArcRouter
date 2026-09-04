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
    public void EnsureValid_Throws_WhenProviderAuthHeaderNameIsMissing()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "" }
            }
        };

        Assert.Throws<OptionsValidationException>(options.EnsureValid);
    }
}