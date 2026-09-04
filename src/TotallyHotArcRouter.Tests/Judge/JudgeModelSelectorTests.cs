using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeModelSelector"/> - which Providers-screen model the shadow judge runs on. The
/// behaviors that matter: only free, enabled, OpenAI-shaped models are eligible; the operator's pick wins
/// while it stays eligible; and an ineligible or absent pick degrades to a fallback or an honest
/// abstention rather than an error.
/// </summary>
public class JudgeModelSelectorTests
{
    [Fact]
    public void Resolve_NoConfiguredPick_TakesFirstEligibleFreeModel()
    {
        var selector = CreateSelector(resolver: TwoFreeModels(), chosenModel: string.Empty);

        var route = selector.Resolve();

        Assert.NotNull(route);
        Assert.Equal(expected: "free-a", actual: route.ModelName);
    }

    [Fact]
    public void Resolve_ConfiguredPickIsEligible_UsesIt()
    {
        var selector = CreateSelector(resolver: TwoFreeModels(), chosenModel: "free-b");

        var route = selector.Resolve();

        Assert.Equal(expected: "free-b", actual: route!.ModelName);
    }

    /// <summary>
    /// A pick whose provider was switched off in the Providers screen must not fail the judge - it falls
    /// back, because shadow scoring degrades quietly rather than erroring.
    /// </summary>
    [Fact]
    public void Resolve_ConfiguredPicksDisabledProvider_FallsBackToAnEligibleModel()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["free-on"] = new() { BaseUrl = "http://localhost:1234/v1", IsFree = true },
                ["free-off"] = new() { BaseUrl = "http://localhost:11434/v1", IsFree = true, Enabled = false }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "still-on", Provider = "free-on", ProviderModelId = "m1" },
                new ModelRouteEntry { ModelName = "switched-off", Provider = "free-off", ProviderModelId = "m2" }
            ]
        };

        var selector = CreateSelector(resolver: Resolver(options), chosenModel: "switched-off");

        var route = selector.Resolve();

        Assert.Equal(expected: "still-on", actual: route!.ModelName);
        Assert.Equal(expected: ["still-on"], actual: selector.ListEligibleModels());
    }

    [Fact]
    public void Resolve_OnlyPaidProvidersConfigured_ReturnsNull()
    {
        var selector = CreateSelector(
            resolver: ModelRouteResolverTestFactory.Create(
                modelName: "gpt-5.4",
                providerModelId: "gpt-5.4-2026-01",
                baseUrl: "https://api.openai.com",
                isFree: false),
            chosenModel: string.Empty);

        Assert.Null(selector.Resolve());
        Assert.Empty(selector.ListEligibleModels());
    }

    /// <summary>
    /// Bedrock is free-flaggable but speaks SigV4, not OpenAI chat-completions, and the judge calls the
    /// provider directly with no translation layer - so such a route is never offered or selected.
    /// </summary>
    [Fact]
    public void Resolve_FreeBedrockRoute_IsNotEligible()
    {
        var selector = CreateSelector(
            resolver: ModelRouteResolverTestFactory.Create(
                modelName: "bedrock-claude",
                providerModelId: "anthropic.claude-v2",
                baseUrl: "https://bedrock-runtime.us-east-1.amazonaws.com",
                isFree: true,
                awsRegion: "us-east-1"),
            chosenModel: string.Empty);

        Assert.Null(selector.Resolve());
        Assert.Empty(selector.ListEligibleModels());
    }

    /// <summary>
    /// A stopped model is excluded even though its provider is on - the model-level gate
    /// (<see cref="ModelRouteEntry.Enabled"/>) applies to judging exactly as it does to routing.
    /// </summary>
    [Fact]
    public void ListEligibleModels_StoppedModel_IsExcluded()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new() { BaseUrl = "http://localhost:1234/v1", IsFree = true }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "running", Provider = "free", ProviderModelId = "m1" },
                new ModelRouteEntry
                    { ModelName = "stopped", Provider = "free", ProviderModelId = "m2", Enabled = false }
            ]
        };

        var selector = CreateSelector(resolver: Resolver(options), chosenModel: string.Empty);

        Assert.Equal(expected: ["running"], actual: selector.ListEligibleModels());
    }

    /// <summary>
    /// The eligible list and the resolved route must come from one rule, so the dropdown can never offer a model the
    /// selector would refuse.
    /// </summary>
    [Fact]
    public void ListEligibleModels_MatchesWhatResolveWouldPick()
    {
        var selector = CreateSelector(resolver: TwoFreeModels(), chosenModel: string.Empty);

        Assert.Equal(expected: selector.ListEligibleModels()[0], actual: selector.Resolve()!.ModelName);
    }

    private static JudgeModelSelector CreateSelector(IModelRouteResolver resolver, string chosenModel)
    {
        return new JudgeModelSelector(routeResolver: resolver,
            options: new StaticOptionsMonitor<JudgeOptions>(
                new JudgeOptions { Enabled = true, ModelName = chosenModel }),
            logger: NullLogger<JudgeModelSelector>.Instance);
    }

    private static IModelRouteResolver Resolver(ModelRoutingOptions options)
    {
        return new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());
    }

    /// <summary>Two free models on distinct providers, in configuration order - so "first eligible" is observable.</summary>
    private static IModelRouteResolver TwoFreeModels()
    {
        return Resolver(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["lmstudio"] = new() { BaseUrl = "http://localhost:1234/v1", IsFree = true },
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", IsFree = true }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "free-a", Provider = "lmstudio", ProviderModelId = "a" },
                new ModelRouteEntry { ModelName = "free-b", Provider = "ollama", ProviderModelId = "b" }
            ]
        });
    }
}