using System.Text.Json;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="LiteLlmPriceSourceClient.Normalize"/> against a fixture payload.</summary>
public class LiteLlmPriceSourceClientTests
{
    // Mirrors the real LiteLLM shape (per-token costs, litellm_provider, a sample_spec sentinel), trimmed
    // to the fields the normalizer reads.
    private const string FixtureJson = """
        {
          "sample_spec": {
            "input_cost_per_token": 0.0,
            "litellm_provider": "example"
          },
          "gpt-4o": {
            "input_cost_per_token": 2.5e-06,
            "output_cost_per_token": 1e-05,
            "cache_read_input_token_cost": 1.25e-06,
            "input_cost_per_token_batches": 1.25e-06,
            "output_cost_per_token_batches": 5e-06,
            "litellm_provider": "openai"
          },
          "missing-provider": {
            "input_cost_per_token": 1e-06
          },
          "no-token-price": {
            "litellm_provider": "openai",
            "mode": "moderation"
          }
        }
        """;

    [Fact]
    public void Normalize_ConvertsPerTokenToPerMillion_AndCarriesProvider()
    {
        var prices = NormalizeFixture();

        var gpt4o = Assert.Single(prices, p => p.ModelIdentifier == "gpt-4o");

        Assert.Equal("openai", gpt4o.Provider);
        Assert.Equal(2.5m, gpt4o.StandardInputPrice);
        Assert.Equal(10.0m, gpt4o.StandardOutputPrice);
        Assert.Equal(1.25m, gpt4o.CachedInputPrice);
        Assert.Equal(1.25m, gpt4o.BatchInputPrice);
        Assert.Equal(5.0m, gpt4o.BatchOutputPrice);
    }

    [Fact]
    public void Normalize_SkipsSampleSpecAndUnmappableEntries()
    {
        var prices = NormalizeFixture();

        // Only gpt-4o survives: sample_spec is a sentinel, missing-provider has no provider, and
        // no-token-price has no standard rate to store.
        Assert.Single(prices);
        Assert.DoesNotContain(prices, p => p.ModelIdentifier == "sample_spec");
        Assert.DoesNotContain(prices, p => p.ModelIdentifier == "missing-provider");
        Assert.DoesNotContain(prices, p => p.ModelIdentifier == "no-token-price");
    }

    private static IReadOnlyList<NormalizedPrice> NormalizeFixture()
    {
        var client = new LiteLlmPriceSourceClient(
            new HttpClient(),
            LiteLlmPriceSourceClient.DefaultUrl,
            NullLogger<LiteLlmPriceSourceClient>.Instance);

        using var document = JsonDocument.Parse(FixtureJson);
        return client.Normalize(document.RootElement);
    }
}

