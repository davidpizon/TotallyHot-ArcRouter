using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers the projection of <c>ModelRouting:Providers</c> into add-provider templates: the credential
/// header stays off the custom-header list, and every other header is kept.
/// </summary>
public sealed class ProviderTemplateCatalogTests
{
    [Fact]
    public void Project_OmitsTheAuthHeaderAndKeepsTheRemainingHeaders()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    AuthHeaderName = "x-api-key",
                    Headers =
                    [
                        new ProviderHeader { Name = "x-api-key", ValueEnvVar = "ANTHROPIC_API_KEY", Locked = false },
                        new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01", Locked = false }
                    ]
                },
                ["ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    IsFree = true
                }
            }
        };

        var templates = ProviderTemplateCatalog.Project(options);

        Assert.Equal(expected: ["anthropic", "ollama"], actual: templates.Select(template => template.Key));
        var anthropic = templates[0];
        Assert.Equal(expected: "x-api-key", actual: anthropic.AuthHeaderName);
        var header = Assert.Single(anthropic.Headers);
        Assert.Equal(expected: "anthropic-version", actual: header.Name);
        Assert.Equal(expected: "2023-06-01", actual: header.Value);
        Assert.True(templates[1].IsFree);
        Assert.Empty(templates[1].Headers);
    }
}
