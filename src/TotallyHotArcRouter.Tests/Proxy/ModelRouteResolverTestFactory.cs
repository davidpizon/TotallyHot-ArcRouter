using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

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
        IEnvironmentVariableProvider? environment = null)
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
                    AwsRegion = awsRegion
                }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = modelName, Provider = providerName, ProviderModelId = providerModelId }
            ]
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), environment ?? Mock.Of<IEnvironmentVariableProvider>());
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

    /// <summary>
    /// Builds a resolver from fully-specified model entries, for tests that need to control the governance
    /// flags <see cref="CreateWithModelList"/> cannot express - <see cref="ModelRouteEntry.Enabled"/> and
    /// <see cref="ModelRouteEntry.PresentUpstream"/>, the two inputs to
    /// <see cref="IModelRouteResolver.IsModelEnabled"/>.
    /// </summary>
    /// <param name="disabledProviders">
    /// Providers to mark <see cref="ProviderOptions.Enabled"/> = <see langword="false"/>, so a test can
    /// exercise the provider gate independently of the per-model one.
    /// </param>
    /// <param name="models">The routes to configure, each carrying its own enablement flags.</param>
    public static IModelRouteResolver CreateWithModelEntries(
        IReadOnlyCollection<string>? disabledProviders,
        params ModelRouteEntry[] models)
    {
        var disabled = new HashSet<string>(disabledProviders ?? [], StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerName in models.Select(m => m.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            providers[providerName] = new ProviderOptions
            {
                BaseUrl = "https://example.com",
                Enabled = !disabled.Contains(providerName),
            };
        }

        var options = new ModelRoutingOptions
        {
            Providers = providers,
            ModelList = [.. models],
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }
}

