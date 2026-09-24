using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Pins that the <em>value</em> behind an env-var-backed header never leaves the process through a
/// management surface or a diagnostic string. The variable's name is deliberately allowed to appear - it is
/// not a secret - so each test asserts the name is present and the resolved value is not, which also proves
/// the marker really did resolve rather than the assertion passing on an unresolved header.
/// </summary>
public sealed class EnvVarHeaderValueLeakTests
{
    private const string EnvVarName = "OPENAI_API_KEY";
    private const string SecretMarker = "sk-env-var-value-must-never-leak";

    private static readonly StubEnvironment Environment = new(new Dictionary<string, string>
    {
        [EnvVarName] = SecretMarker
    });

    private static InMemoryProviderConfigStore CreateStore()
    {
        return new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new()
                {
                    BaseUrl = "https://api.openai.com",
                    Headers = [new ProviderHeader { Name = "Authorization", ValueEnvVar = EnvVarName, Locked = false }]
                }
            },
            ModelList = [new ModelRouteEntry { ModelName = "gpt", Provider = "openai", ProviderModelId = "gpt" }]
        });
    }

    [Fact]
    public void ListProviders_WholeResponseJson_ShowsTheEnvVarNameButNeverItsValue()
    {
        var facade = new ManagementFacade(store: CreateStore(), environment: Environment,
            httpClient: new HttpClient());

        var json = JsonSerializer.Serialize(facade.ListProviders());

        Assert.Contains(expectedSubstring: EnvVarName, actualString: json, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: SecretMarker, actualString: json,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvedRoute_CarriesTheValueUpstreamButNeverPrintsIt()
    {
        var resolver = new ModelRouteResolver(store: CreateStore(), environment: Environment);

        Assert.True(resolver.TryResolve(modelName: "gpt", route: out var route));

        // Sanity: the value really resolved, so the assertions below are not vacuous.
        Assert.Contains(collection: route.ExtraHeaders, filter: h => h.Value.Contains(SecretMarker));
        Assert.DoesNotContain(expectedSubstring: SecretMarker, actualString: route.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderOptions_ToString_NeverPrintsTheEnvVarValue()
    {
        var provider = CreateStore().Snapshot.Options.Providers["openai"];

        Assert.DoesNotContain(expectedSubstring: SecretMarker, actualString: provider.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    /// <summary>A fixed set of environment variables, so no test mutates real process state.</summary>
    private sealed class StubEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        /// <inheritdoc/>
        public string? GetVariable(string name)
        {
            return values.GetValueOrDefault(name);
        }
    }
}
