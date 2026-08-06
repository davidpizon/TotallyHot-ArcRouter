using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Builds <see cref="IModelRouteResolver"/> instances for tests without needing real environment variables.
/// </summary>
internal static class ModelRouteResolverTestFactory
{
    /// <summary>
    /// Builds a resolver for a single model/provider. Authentication is expressed purely as a custom header
    /// (matching production): when <paramref name="apiKey"/> is non-blank, it is composed with
    /// <paramref name="authHeaderScheme"/> (scheme-prefixed, or raw when the scheme is empty) into a literal
    /// header stored under <paramref name="authHeaderName"/> and merged ahead of <paramref name="headers"/>.
    /// </summary>
    public static IModelRouteResolver Create(
        string modelName,
        string providerModelId,
        string baseUrl,
        string authHeaderName = "Authorization",
        string authHeaderScheme = "Bearer",
        string? apiKey = "test-api-key",
        string providerName = "test-provider",
        IReadOnlyList<ProviderHeader>? headers = null,
        bool isFree = false,
        string? awsRegion = null,
        bool enableToolCallGuard = false)
    {
        List<ProviderHeader> allHeaders = [];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var value = string.IsNullOrEmpty(authHeaderScheme) ? apiKey : $"{authHeaderScheme} {apiKey}";
            allHeaders.Add(new ProviderHeader { Name = authHeaderName, Value = value });
        }

        if (headers is not null)
        {
            allHeaders.AddRange(headers);
        }

        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [providerName] = new ProviderOptions
                {
                    BaseUrl = baseUrl,
                    AuthHeaderName = authHeaderName,
                    Headers = allHeaders,
                    IsFree = isFree,
                    AwsRegion = awsRegion,
                    EnableToolCallGuard = enableToolCallGuard
                }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = modelName, Provider = providerName, ProviderModelId = providerModelId }
            ]
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }

    /// <summary>
    /// Builds a resolver with several models across distinct providers (each provider gets its own
    /// <paramref name="models"/> BaseUrl so a test handler can route by host). With no static per-model
    /// fallback list anymore (see <c>docs/router/agent-resilience-strategies.md</c>'s Circuit Breaker,
    /// which replaced it), every model configured here is automatically a dynamic next-best candidate for
    /// every other - callers don't need to declare backup relationships explicitly.
    /// </summary>
    public static IModelRouteResolver CreateWithModels(
        params (string ModelName, string Provider, string ProviderModelId, string BaseUrl)[] models)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (!providers.ContainsKey(model.Provider))
            {
                providers[model.Provider] = new ProviderOptions
                {
                    BaseUrl = model.BaseUrl
                };
            }
        }

        var options = new ModelRoutingOptions
        {
            Providers = providers,
            ModelList = models
                .Select(m => new ModelRouteEntry
                {
                    ModelName = m.ModelName,
                    Provider = m.Provider,
                    ProviderModelId = m.ProviderModelId
                })
                .ToList()
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }

    public static IModelRouteResolver Empty() =>
        new ModelRouteResolver(new InMemoryProviderConfigStore(new ModelRoutingOptions()), Mock.Of<IEnvironmentVariableProvider>());

    /// <summary>
    /// Builds a resolver configured with several models across one or more providers, for tests that need
    /// to observe ordering or multi-provider behavior (e.g. <see cref="IModelRouteResolver.ListModels"/>).
    /// </summary>
    public static IModelRouteResolver CreateWithModelList(params (string ModelName, string Provider, string ProviderModelId)[] models)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerName in models.Select(m => m.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            providers[providerName] = new ProviderOptions { BaseUrl = "https://example.com" };
        }

        var options = new ModelRoutingOptions
        {
            Providers = providers,
            ModelList = models
                .Select(m => new ModelRouteEntry { ModelName = m.ModelName, Provider = m.Provider, ProviderModelId = m.ProviderModelId })
                .ToList()
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }
}

