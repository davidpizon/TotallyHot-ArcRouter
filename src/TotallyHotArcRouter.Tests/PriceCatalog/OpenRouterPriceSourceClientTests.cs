using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="OpenRouterPriceSourceClient.Normalize"/> against a fixture payload.</summary>
public class OpenRouterPriceSourceClientTests
{
    // Mirrors the real OpenRouter shape (verified 2026-07-16): { "data": [ { id, pricing: { ... } } ] },
    // pricing values as decimal STRINGS in USD per token, trimmed to the fields the normalizer reads.
    private const string FixtureJson = """
                                       {
                                         "data": [
                                           {
                                             "id": "anthropic/claude-opus-4",
                                             "pricing": {
                                               "prompt": "0.000015",
                                               "completion": "0.000075",
                                               "input_cache_read": "0.0000015"
                                             }
                                           },
                                           {
                                             "id": "no-provider-segment",
                                             "pricing": { "prompt": "0.000001", "completion": "0.000002" }
                                           },
                                           {
                                             "id": "trailing-slash/",
                                             "pricing": { "prompt": "0.000001", "completion": "0.000002" }
                                           },
                                           {
                                             "id": "some/moderation-only-model",
                                             "pricing": { "web_search": "0.01" }
                                           },
                                           {
                                             "id": "some/no-pricing-object"
                                           }
                                         ]
                                       }
                                       """;

    [Fact]
    public void Normalize_ConvertsPerTokenStringsToPerMillion_AndSplitsIdIntoProvider()
    {
        var prices = NormalizeFixture();

        var claude = Assert.Single(collection: prices, predicate: p => p.ModelIdentifier == "anthropic/claude-opus-4");

        Assert.Equal(expected: "anthropic", actual: claude.Provider);
        Assert.Equal(15.0m, actual: claude.StandardInputPrice);
        Assert.Equal(75.0m, actual: claude.StandardOutputPrice);
        Assert.Equal(1.5m, actual: claude.CachedInputPrice);
    }

    [Fact]
    public void Normalize_NeverProducesBatchRates()
    {
        // OpenRouter publishes no batch pricing at all - not offered, per D7, never a fabricated zero.
        var prices = NormalizeFixture();

        Assert.All(collection: prices, action: p => Assert.Null(p.BatchInputPrice));
        Assert.All(collection: prices, action: p => Assert.Null(p.BatchOutputPrice));
    }

    [Fact]
    public void Normalize_SkipsIdsWithNoProviderSegment()
    {
        var prices = NormalizeFixture();

        Assert.DoesNotContain(collection: prices, filter: p => p.ModelIdentifier == "no-provider-segment");
        Assert.DoesNotContain(collection: prices, filter: p => p.ModelIdentifier == "trailing-slash/");
    }

    [Fact]
    public void Normalize_SkipsEntriesWithNoStandardRateOrNoPricingObject()
    {
        var prices = NormalizeFixture();

        Assert.DoesNotContain(collection: prices, filter: p => p.ModelIdentifier == "some/moderation-only-model");
        Assert.DoesNotContain(collection: prices, filter: p => p.ModelIdentifier == "some/no-pricing-object");
    }

    [Fact]
    public void Normalize_MissingDataArray_ReturnsEmptyRatherThanThrowing()
    {
        using var document = JsonDocument.Parse("""{ "not_data": [] }""");
        var client = Client();

        var prices = client.Normalize(document.RootElement);

        Assert.Empty(prices);
    }

    [Fact]
    public void Normalize_NonNumericPricingString_IsSkippedNotCoercedToZero()
    {
        using var document = JsonDocument.Parse("""
                                                { "data": [ { "id": "openai/gpt-4o", "pricing": { "prompt": "not-a-number", "completion": "0.00001" } } ] }
                                                """);
        var client = Client();

        var price = Assert.Single(client.Normalize(document.RootElement));

        Assert.Null(price.StandardInputPrice);
        Assert.Equal(10.0m, actual: price.StandardOutputPrice);
    }

    private static IReadOnlyList<NormalizedPrice> NormalizeFixture()
    {
        using var document = JsonDocument.Parse(FixtureJson);
        return Client().Normalize(document.RootElement);
    }

    private static OpenRouterPriceSourceClient Client()
    {
        return new OpenRouterPriceSourceClient(httpClient: new HttpClient(),
            url: OpenRouterPriceSourceClient.DefaultUrl,
            logger: NullLogger<OpenRouterPriceSourceClient>.Instance);
    }
}