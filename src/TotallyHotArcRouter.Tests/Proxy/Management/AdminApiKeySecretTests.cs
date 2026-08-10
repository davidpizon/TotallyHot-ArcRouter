using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade.SetSecret"/>/<see cref="ManagementFacade.DeleteSecret"/> (the
/// write-only <c>PUT</c>/<c>DELETE /admin/secrets/{name}</c> surface, docs/router/secrets-at-rest-plan.md
/// §4/§7) and <see cref="ProviderView.HasStoredAdminKey"/>.
/// </summary>
public sealed class AdminApiKeySecretTests
{
    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new ProviderOptions { BaseUrl = "https://api.anthropic.com", AuthHeaderName = "x-api-key" }
        }
    };

    private static ManagementFacade CreateFacade(ISecretWriter? secretWriter = null, ISecretReader? secretReader = null) =>
        new(
            new InMemoryProviderConfigStore(SeedOptions()),
            Mock.Of<IEnvironmentVariableProvider>(),
            new HttpClient(),
            secretWriter: secretWriter,
            secretReader: secretReader);

    [Fact]
    public void SetSecret_RecognizedAnthropicAdminKeyName_WritesToStore()
    {
        var writer = new Mock<ISecretWriter>();
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.SetSecret("reconciliation:anthropic:admin-key", "sk-ant-admin01-abc");

        Assert.True(result.Success);
        writer.Verify(w => w.Write("reconciliation:anthropic:admin-key", "sk-ant-admin01-abc"), Times.Once);
    }

    [Fact]
    public void SetSecret_RecognizedOpenAiAdminKeyName_WritesToStore()
    {
        var writer = new Mock<ISecretWriter>();
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.SetSecret("reconciliation:openai:admin-key", "sk-openai-admin");

        Assert.True(result.Success);
        writer.Verify(w => w.Write("reconciliation:openai:admin-key", "sk-openai-admin"), Times.Once);
    }

    [Theory]
    [InlineData("provider:anthropic:header:x-api-key")] // a provider header ref - not an admin key
    [InlineData("reconciliation:bedrock-anthropic:admin-key")] // unrecognized provider
    [InlineData("telemetry:cert-password")] // a different secret family entirely
    [InlineData("reconciliation:anthropic:admin-key-extra")]
    public void SetSecret_UnrecognizedName_IsRejected_AndNeverReachesTheStore(string name)
    {
        var writer = new Mock<ISecretWriter>(MockBehavior.Strict);
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.SetSecret(name, "some-value");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
        // Strict mock: Write would throw if called, since nothing is set up on it.
    }

    [Fact]
    public void SetSecret_BlankValue_IsRejected()
    {
        var writer = new Mock<ISecretWriter>(MockBehavior.Strict);
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.SetSecret("reconciliation:anthropic:admin-key", "   ");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetSecret_NoSecretWriterConfigured_ReportsUnavailable()
    {
        var facade = CreateFacade(secretWriter: null);

        var result = facade.SetSecret("reconciliation:anthropic:admin-key", "sk-ant-admin01-abc");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void DeleteSecret_RecognizedName_DeletesFromStore()
    {
        var writer = new Mock<ISecretWriter>();
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.DeleteSecret("reconciliation:anthropic:admin-key");

        Assert.True(result.Success);
        writer.Verify(w => w.Delete("reconciliation:anthropic:admin-key"), Times.Once);
    }

    [Fact]
    public void DeleteSecret_UnrecognizedName_IsRejected_AndNeverReachesTheStore()
    {
        var writer = new Mock<ISecretWriter>(MockBehavior.Strict);
        var facade = CreateFacade(secretWriter: writer.Object);

        var result = facade.DeleteSecret("provider:anthropic:header:x-api-key");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void ListProviders_HasStoredAdminKey_TrueWhenAdminKeySecretExists()
    {
        var reader = new Mock<ISecretReader>();
        string stored = "sk-ant-admin01-abc";
        reader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out stored)).Returns(true);
        var facade = CreateFacade(secretReader: reader.Object);

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.True(provider.HasStoredAdminKey);
    }

    [Fact]
    public void ListProviders_HasStoredAdminKey_FalseWhenNoAdminKeyStored()
    {
        var reader = new Mock<ISecretReader>();
        string empty = string.Empty;
        reader.Setup(r => r.TryRead("reconciliation:anthropic:admin-key", out empty)).Returns(false);
        var facade = CreateFacade(secretReader: reader.Object);

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.False(provider.HasStoredAdminKey);
    }

    [Fact]
    public void ListProviders_HasStoredAdminKey_FalseWhenNoSecretReaderConfigured()
    {
        var facade = CreateFacade(secretReader: null);

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.False(provider.HasStoredAdminKey);
    }
}
