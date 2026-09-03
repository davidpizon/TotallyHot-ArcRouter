using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers domain validation for <see cref="PriceCatalogOptions"/>.</summary>
public class PriceCatalogOptionsTests
{
    [Fact]
    public void EnsureValid_EmptyConfiguration_DoesNotThrow()
    {
        new PriceCatalogOptions().EnsureValid();
    }

    [Fact]
    public void EnsureValid_LiteLlmWithUrlOverrideOnly_DoesNotThrow()
    {
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["litellm"] = new PriceSourceOptions { Url = "https://mirror.example/prices.json" },
            },
        };

        options.EnsureValid();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(13)]
    [InlineData(0)]
    public void EnsureValid_Throws_WhenPollIntervalOutOfBand(int pollIntervalHours)
    {
        var options = new PriceCatalogOptions { PollIntervalHours = pollIntervalHours };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_Throws_WhenSourceNameUnknown()
    {
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["litellmm"] = new PriceSourceOptions(),
            },
        };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }

    [Fact]
    public void EnsureValid_OpenRouterWithUrlOverrideOnly_DoesNotThrow()
    {
        // OpenRouter's client shipped alongside the priority gate; naming it in config is no longer an
        // error - it is exactly as recognized as litellm.
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openrouter"] = new PriceSourceOptions { Url = "https://mirror.example/models.json" },
            },
        };

        options.EnsureValid();
    }

    [Fact]
    public void EnsureValid_Throws_WhenSourceNameIsAStillUnbuiltSource()
    {
        // openpipe was investigated and rejected as a source entirely (no machine-readable pricing, no
        // independent price data) - see "Still open" in model-price-catalog.md. Naming it remains a hard
        // error, not a silent no-op: an operator who configures a source and gets no polling has been misled.
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openpipe"] = new PriceSourceOptions(),
            },
        };

        Assert.Throws<OptionsValidationException>(() => options.EnsureValid());
    }

    [Fact]
    public void GetSourceUrl_ReturnsNull_WhenNoOverrideConfigured()
    {
        Assert.Null(new PriceCatalogOptions().GetSourceUrl(PriceCatalogOptions.LiteLlmSourceName));
    }

    [Fact]
    public void GetSourceUrl_ReturnsOverride_WhenConfigured()
    {
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["litellm"] = new PriceSourceOptions { Url = "https://mirror.example/prices.json" },
            },
        };

        Assert.Equal("https://mirror.example/prices.json", options.GetSourceUrl(PriceCatalogOptions.LiteLlmSourceName));
    }

    [Fact]
    public void GetSourceUrl_TreatsWhitespaceAsAbsent()
    {
        var options = new PriceCatalogOptions
        {
            Sources = new Dictionary<string, PriceSourceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["litellm"] = new PriceSourceOptions { Url = "   " },
            },
        };

        Assert.Null(options.GetSourceUrl(PriceCatalogOptions.LiteLlmSourceName));
    }

    [Fact]
    public void KnownSources_ContainLiteLlmAndOpenRouter_BothEnabledByDefault()
    {
        // Seeding is driven by this list, and a seeded row is something the Governance panel offers to
        // switch on. A name here without a client would put a source in the UI that can never poll.
        var names = PriceCatalogOptions.KnownSources.Select(s => s.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(PriceCatalogOptions.LiteLlmSourceName, names);
        Assert.Contains(PriceCatalogOptions.OpenRouterSourceName, names);
        Assert.All(PriceCatalogOptions.KnownSources, s => Assert.True(s.DefaultEnabled));
    }

    [Fact]
    public void KnownSources_LiteLlmDefaultOutranksOpenRouterDefault()
    {
        // LiteLLM must win by default on every install, including one that already has a litellm row seeded
        // at priority 0 from before ranking existed - seeding is ON CONFLICT DO NOTHING, so that row is never
        // revisited. OpenRouter's default has to be strictly below 0, not equal to it, for that to hold
        // without a migration.
        var liteLlm = PriceCatalogOptions.KnownSources.Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        var openRouter = PriceCatalogOptions.KnownSources.Single(s => s.Name == PriceCatalogOptions.OpenRouterSourceName);

        Assert.True(liteLlm.DefaultPriorityScore > openRouter.DefaultPriorityScore);
    }
}

