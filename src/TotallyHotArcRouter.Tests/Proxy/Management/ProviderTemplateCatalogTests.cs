using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers the projection of <c>ModelRouting:Providers</c> into add-provider templates: a locked (secret)
/// header is projected as an empty locked row with its env-var name, a public header is kept as is, and a
/// template with no credential projects none.
/// </summary>
public sealed class ProviderTemplateCatalogTests
{
    [Fact]
    public void Project_ProjectsTheLockedCredentialRowAndKeepsThePublicHeader()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    Headers =
                    [
                        new ProviderHeader { Name = "x-api-key", ValueEnvVar = "ANTHROPIC_API_KEY", Locked = true },
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
        Assert.Equal(expected: 2, actual: anthropic.Headers.Count);

        var credential = anthropic.Headers[0];
        Assert.Equal(expected: "x-api-key", actual: credential.Name);
        Assert.Equal(expected: "ANTHROPIC_API_KEY", actual: credential.ValueEnvVar);
        Assert.Null(credential.Value);
        Assert.True(credential.Locked);

        var version = anthropic.Headers[1];
        Assert.Equal(expected: "anthropic-version", actual: version.Name);
        Assert.Equal(expected: "2023-06-01", actual: version.Value);
        Assert.False(version.Locked);
        Assert.True(templates[1].IsFree);
        Assert.Empty(templates[1].Headers);
    }

    [Fact]
    public void Project_NeverProjectsALockedLiteralValue()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom"] = new()
                {
                    BaseUrl = "https://example.invalid",
                    Headers =
                    [
                        new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01", Locked = false },
                        new ProviderHeader { Name = "X-Secret", Value = "super-secret", Locked = true },
                        new ProviderHeader
                        {
                            Name = "X-Both",
                            Value = "also-secret",
                            ValueEnvVar = "BOTH_TOKEN",
                            Locked = true
                        }
                    ]
                }
            }
        };

        var headers = ProviderTemplateCatalog.Project(options)[0].Headers;

        Assert.Equal(expected: 3, actual: headers.Count);
        Assert.Equal(expected: "2023-06-01", actual: headers[0].Value);

        // The secret is dropped but the row survives, so the editor can offer an empty locked box for it.
        Assert.Equal(expected: "X-Secret", actual: headers[1].Name);
        Assert.Null(headers[1].Value);
        Assert.Null(headers[1].ValueEnvVar);
        Assert.True(headers[1].Locked);

        Assert.Equal(expected: "X-Both", actual: headers[2].Name);
        Assert.Null(headers[2].Value);
        Assert.Equal(expected: "BOTH_TOKEN", actual: headers[2].ValueEnvVar);
        Assert.True(headers[2].Locked);
    }

    [Fact]
    public void Project_ProjectsNoCredentialRowForTemplatesThatDeclareNone()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                // Unauthenticated local runtime.
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", IsFree = true },
                // SDK-signed provider: authenticates through the AWS SDK, not an HTTP header.
                ["bedrock-anthropic"] = new()
                {
                    BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
                    AwsRegion = "us-east-1"
                }
            }
        };

        var templates = ProviderTemplateCatalog.Project(options);

        Assert.All(templates, template => Assert.Empty(template.Headers));
    }

    [Fact]
    public void Project_RejectsTheReservedBlankProviderKey()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["other"] = new()
                {
                    BaseUrl = "https://example.invalid",
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderTemplateCatalog.Project(options));
        Assert.Contains(expectedSubstring: "reserved", actualString: ex.Message, comparisonType: StringComparison.Ordinal);
    }
}
