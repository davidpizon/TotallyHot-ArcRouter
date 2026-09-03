using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers known-model allowlist resolution behavior for <see cref="ModelRouteResolver"/>.
/// </summary>
public class ModelRouteResolverTests
{
    [Fact]
    public void TryResolve_KnownModel_ReturnsUpstreamRoute()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com",
            authHeaderName: "Authorization");

        var resolved = resolver.TryResolve(modelName: "gpt-5.4", route: out var route);

        Assert.True(resolved);
        Assert.Equal(expected: "gpt-5.4", actual: route!.ModelName);
        Assert.Equal(expected: "gpt-5.4-2026-01", actual: route.ProviderModelId);
        Assert.Equal(expected: "https://api.openai.com/", actual: route.UpstreamBaseUrl.ToString());
        Assert.Equal(expected: "Authorization", actual: route.AuthHeaderName);
    }

    [Fact]
    public void TryResolve_EnvVarHeader_ResolvesValueFromEnvironment()
    {
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("MY_PROVIDER_API_KEY")).Returns("Bearer resolved-from-env");

        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com",
            authHeaderName: "Authorization",
            apiKey: null,
            headers: [new ProviderHeader { Name = "Authorization", ValueEnvVar = "MY_PROVIDER_API_KEY" }],
            environment: environment.Object);

        var resolved = resolver.TryResolve(modelName: "gpt-5.4", route: out var route);

        Assert.True(resolved);
        var header = Assert.Single(route!.ExtraHeaders);
        Assert.Equal(expected: "Authorization", actual: header.Key);
        Assert.Equal(expected: "Bearer resolved-from-env", actual: header.Value);
    }

    [Fact]
    public void TryResolve_EnvVarHeaderMissingFromEnvironment_OmitsTheHeader()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com",
            authHeaderName: "Authorization",
            apiKey: null,
            headers: [new ProviderHeader { Name = "Authorization", ValueEnvVar = "UNSET_PROVIDER_API_KEY" }],
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var resolved = resolver.TryResolve(modelName: "gpt-5.4", route: out var route);

        Assert.True(resolved);
        Assert.Empty(route!.ExtraHeaders);
    }

    [Fact]
    public void TryResolve_AuthHeaderNameHasSurroundingWhitespace_StillMatchesConfiguredHeader()
    {
        // Regression test: ManagementFacade.MergeProvider stores AuthHeaderName verbatim (no trim), so a
        // provider persisted with e.g. " x-api-key " must still be recognized as having that header
        // configured - otherwise ProxyMiddleware's client-header-stripping decision (route.AuthHeaderConfigured)
        // would wrongly conclude no auth header is configured and let a client-sent "x-api-key" pass through
        // unstripped, alongside (or instead of) the provider's own.
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "claude-sonnet-5",
            providerModelId: "claude-sonnet-5",
            baseUrl: "https://api.anthropic.com",
            authHeaderName: " x-api-key ",
            apiKey: null,
            headers: [new ProviderHeader { Name = "x-api-key", Value = "real-anthropic-key" }]);

        var resolved = resolver.TryResolve(modelName: "claude-sonnet-5", route: out var route);

        Assert.True(resolved);
        Assert.True(route!.AuthHeaderConfigured);
    }

    [Fact]
    public void ToString_RedactsAwsSecrets_ButKeepsNonSecretFieldsVisible()
    {
        var route = new ResolvedModelRoute(
            ModelName: "claude-sonnet-bedrock",
            Provider: "bedrock-anthropic",
            ProviderModelId: "anthropic.claude-3-5-sonnet-20241022-v2:0",
            UpstreamBaseUrl: new Uri("https://bedrock-runtime.us-east-1.amazonaws.com"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: [new KeyValuePair<string, string>(key: "x-api-key", value: "another-secret-value")],
            AwsRegion: "us-east-1",
            AwsAccessKeyId: "AKIAEXAMPLE",
            AwsSecretAccessKey: "wJalrXUtnFEMI/EXAMPLESECRETKEY",
            AwsSessionToken: "FQoGZXIvYXdzEXAMPLESESSIONTOKEN");

        var text = route.ToString();

        Assert.DoesNotContain(expectedSubstring: "another-secret-value", actualString: text);
        Assert.DoesNotContain(expectedSubstring: "wJalrXUtnFEMI/EXAMPLESECRETKEY", actualString: text);
        Assert.DoesNotContain(expectedSubstring: "FQoGZXIvYXdzEXAMPLESESSIONTOKEN", actualString: text);

        // Non-secret identifying fields must still be there - this is a redaction, not a black hole.
        Assert.Contains(expectedSubstring: "claude-sonnet-bedrock", actualString: text);
        Assert.Contains(expectedSubstring: "bedrock-anthropic", actualString: text);
        Assert.Contains(expectedSubstring: "AKIAEXAMPLE", actualString: text);
        Assert.Contains(expectedSubstring: "us-east-1", actualString: text);
        Assert.Contains(expectedSubstring: "x-api-key", actualString: text);
    }

    [Fact]
    public void ToString_NullSecrets_PrintsNullMarkerNotEmptyOrThrow()
    {
        var route = new ResolvedModelRoute(
            ModelName: "gpt-5.4",
            Provider: "openai",
            ProviderModelId: "gpt-5.4-2026-01",
            UpstreamBaseUrl: new Uri("https://api.openai.com"),
            AuthHeaderName: "Authorization",
            ExtraHeaders: []);

        var text = route.ToString();

        Assert.Contains(expectedSubstring: "AwsSecretAccessKey = <null>", actualString: text);
        Assert.Contains(expectedSubstring: "AwsSessionToken = <null>", actualString: text);
    }

    // ProxyMiddleware reads IsFree off the resolved route to decide whether a request's cost is a known
    // zero or an unknown null, so the flag has to survive the provider -> route hop.
    [Fact]
    public void TryResolve_FreeProvider_PropagatesIsFreeOntoTheRoute()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: "http://localhost:11434/v1",
            providerName: "ollama",
            isFree: true);

        Assert.True(resolver.TryResolve(modelName: "llama3", route: out var route));
        Assert.True(route!.IsFree);
    }

    // A provider is paid unless the operator says otherwise - the default must never be "free," which
    // would report a fabricated $0.00 for a model that actually bills.
    [Fact]
    public void TryResolve_ProviderWithoutIsFree_ResolvesAsNotFree()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");

        Assert.True(resolver.TryResolve(modelName: "gpt-5.4", route: out var route));
        Assert.False(route!.IsFree);
    }

    [Fact]
    public void TryResolve_IsCaseInsensitiveOnModelName()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");

        Assert.True(resolver.TryResolve(modelName: "GPT-5.4", route: out _));
    }

    [Fact]
    public void TryResolve_UnknownModel_ReturnsFalse()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");

        var resolved = resolver.TryResolve(modelName: "some-other-model", route: out var route);

        Assert.False(resolved);
        Assert.Null(route);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_NullOrWhitespaceModelName_ReturnsFalse(string? modelName)
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");

        Assert.False(resolver.TryResolve(modelName: modelName, route: out var route));
        Assert.Null(route);
    }

    [Fact]
    public void Constructor_ModelListReferencesUnknownProvider_ThrowsOptionsValidationException()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(),
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };

        Assert.Throws<OptionsValidationException>(() =>
            new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
                environment: Mock.Of<IEnvironmentVariableProvider>()));
    }

    [Fact]
    public void Constructor_EmptyConfiguration_DoesNotThrow_AndResolvesNothing()
    {
        var resolver = ModelRouteResolverTestFactory.Empty();

        Assert.False(resolver.TryResolve(modelName: "anything", route: out _));
    }

    // Verifies that ListModels() surfaces every configured model with its provider key, in the same order
    // they appear in configuration, so /v1/models can be answered deterministically from this data.
    [Fact]
    public void ListModels_ReturnsAllConfiguredModels_WithProviderKeys_InConfigurationOrder()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("claude-opus-4.6", "anthropic", "claude-opus-4-6"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));

        var models = resolver.ListModels();

        Assert.Equal(
            expected:
            [
                new AvailableModel(ModelName: "gpt-5.4", Provider: "openai"),
                new AvailableModel(ModelName: "claude-opus-4.6", Provider: "anthropic"),
                new AvailableModel(ModelName: "kimi-k2.5", Provider: "moonshot")
            ], actual: models);
    }

    // Verifies that an empty configuration yields an empty model list rather than throwing, so /v1/models
    // can still return a valid (empty) response for a proxy with no routes configured yet.
    [Fact]
    public void ListModels_EmptyConfiguration_ReturnsEmptyList()
    {
        var resolver = ModelRouteResolverTestFactory.Empty();

        Assert.Empty(resolver.ListModels());
    }

    // Verifies the live-reload guarantee: after a provider is edited in the store, the SAME resolver
    // instance resolves the model against the new endpoint without being reconstructed.
    [Fact]
    public async Task TryResolve_ReflectsProviderEdit_WithoutReconstruction()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1", AuthHeaderName = "Authorization" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3" }]
        };
        var store = new InMemoryProviderConfigStore(options);
        var resolver = new ModelRouteResolver(store: store, environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.True(resolver.TryResolve(modelName: "llama3", route: out var before));
        // Uri only appends a trailing slash to authority-only URLs; one carrying a path (/v1) is
        // stringified verbatim. (Forwarding is unaffected either way - ProxyMiddleware TrimEnd('/')s it.)
        Assert.Equal(expected: "http://localhost:11434/v1", actual: before!.UpstreamBaseUrl.ToString());

        await store.UpsertProviderAsync(
            key: "local",
            provider: new ProviderOptions
            { BaseUrl = "http://192.168.1.50:11434/v1", AuthHeaderName = "Authorization" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(resolver.TryResolve(modelName: "llama3", route: out var after));
        Assert.Equal(expected: "http://192.168.1.50:11434/v1", actual: after!.UpstreamBaseUrl.ToString());
    }

    // Verifies a newly added model becomes routable (and shows up in ListModels) on the same resolver
    // instance after the store is edited.
    [Fact]
    public async Task TryResolve_ReflectsNewlyAddedModel_WithoutReconstruction()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1", AuthHeaderName = "Authorization" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3" }]
        };
        var store = new InMemoryProviderConfigStore(options);
        var resolver = new ModelRouteResolver(store: store, environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.False(resolver.TryResolve(modelName: "mistral", route: out _));

        await store.UpsertModelAsync(
            entry: new ModelRouteEntry { ModelName = "mistral", Provider = "local", ProviderModelId = "mistral" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(resolver.TryResolve(modelName: "mistral", route: out _));
        Assert.Contains(expected: new AvailableModel(ModelName: "mistral", Provider: "local"),
            collection: resolver.ListModels());
    }

    // Governance > Providers' Stop/Play control writes ProviderOptions.Enabled; IsProviderEnabled is the
    // routing-path read side of that flag (see RequestInterceptor/ProxyMiddleware).
    [Fact]
    public void IsProviderEnabled_DefaultConfiguredProvider_ReturnsTrue()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com", providerName: "openai");

        Assert.True(resolver.IsProviderEnabled("openai"));
    }

    [Fact]
    public void IsProviderEnabled_UnknownProviderKey_ReturnsTrue()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com", providerName: "openai");

        Assert.True(resolver.IsProviderEnabled("does-not-exist"));
    }

    [Fact]
    public void IsProviderEnabled_ProviderDisabled_ReturnsFalse()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1", Enabled = false }
            },
            ModelList = [new ModelRouteEntry { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3" }]
        };
        var resolver = new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.False(resolver.IsProviderEnabled("local"));
    }

    // Verifies the same live-reload guarantee as TryResolve_ReflectsProviderEdit_WithoutReconstruction: a
    // Stop-toggle edit made through the store is visible on the same resolver instance without rebuilding it.
    [Fact]
    public async Task IsProviderEnabled_ReflectsLiveToggle_WithoutReconstruction()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3" }]
        };
        var store = new InMemoryProviderConfigStore(options);
        var resolver = new ModelRouteResolver(store: store, environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.True(resolver.IsProviderEnabled("local"));

        await store.UpsertProviderAsync(
            key: "local",
            provider: new ProviderOptions { BaseUrl = "http://localhost:11434/v1", Enabled = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(resolver.IsProviderEnabled("local"));
    }

    // The model-level twin of the four IsProviderEnabled_* tests above - Governance > Providers' per-model
    // Start/Stop control writes ModelRouteEntry.Enabled; a "Refresh from endpoint" scan writes
    // ModelRouteEntry.PresentUpstream. IsModelEnabled is the routing-path read side of both.
    [Fact]
    public void IsModelEnabled_DefaultConfiguredModel_ReturnsTrue()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com", providerName: "openai");

        Assert.True(resolver.IsModelEnabled("gpt-5.4"));
    }

    [Fact]
    public void IsModelEnabled_UnknownModelName_ReturnsTrue()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com", providerName: "openai");

        Assert.True(resolver.IsModelEnabled("does-not-exist"));
    }

    [Fact]
    public void IsModelEnabled_ModelDisabled_ReturnsFalse()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList =
            [
                new ModelRouteEntry
                    { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3", Enabled = false }
            ]
        };
        var resolver = new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.False(resolver.IsModelEnabled("llama3"));
    }

    [Fact]
    public void IsModelEnabled_ModelNotPresentUpstream_ReturnsFalse()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList =
            [
                new ModelRouteEntry
                    { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3", PresentUpstream = false }
            ]
        };
        var resolver = new ModelRouteResolver(store: new InMemoryProviderConfigStore(options),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.False(resolver.IsModelEnabled("llama3"));
    }

    // Verifies the same live-reload guarantee as IsProviderEnabled_ReflectsLiveToggle_WithoutReconstruction:
    // an edit made through the store is visible on the same resolver instance without rebuilding it.
    [Fact]
    public async Task IsModelEnabled_ReflectsLiveToggle_WithoutReconstruction()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList = [new ModelRouteEntry { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3" }]
        };
        var store = new InMemoryProviderConfigStore(options);
        var resolver = new ModelRouteResolver(store: store, environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.True(resolver.IsModelEnabled("llama3"));

        await store.UpsertModelAsync(
            entry: new ModelRouteEntry
            { ModelName = "llama3", Provider = "local", ProviderModelId = "llama3", Enabled = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(resolver.IsModelEnabled("llama3"));
    }
}