using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Unit coverage for <see cref="ProviderCredentialResolver"/>:
/// <see cref="ProviderCredentialResolver.ResolveExtraHeaders"/>
/// (how a provider's configured custom headers resolve to concrete name/value pairs - literal values,
/// environment-variable lookups, and the skipped cases) and <see cref="ProviderCredentialResolver.ResolveAwsCredentials"/>
/// (how a Bedrock provider's explicit AWS credential override resolves, and when it falls back to all-null).
/// </summary>
public sealed class ProviderCredentialResolverTests
{
    [Fact]
    public void ResolveExtraHeaders_UsesLiteralValue()
    {
        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.com",
            Headers = [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }]
        };

        var resolved = ProviderCredentialResolver.ResolveExtraHeaders(provider: provider,
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var header = Assert.Single(resolved);
        Assert.Equal(expected: "anthropic-version", actual: header.Key);
        Assert.Equal(expected: "2023-06-01", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_ReadsValueFromEnvironmentVariable()
    {
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("MY_HEADER")).Returns("from-env");
        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.com",
            Headers = [new ProviderHeader { Name = "X-Custom", ValueEnvVar = "MY_HEADER" }]
        };

        var resolved =
            ProviderCredentialResolver.ResolveExtraHeaders(provider: provider, environment: environment.Object);

        var header = Assert.Single(resolved);
        Assert.Equal(expected: "X-Custom", actual: header.Key);
        Assert.Equal(expected: "from-env", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_PrefersLiteralOverEnvVar()
    {
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("MY_HEADER")).Returns("from-env");
        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.com",
            Headers = [new ProviderHeader { Name = "X-Custom", Value = "literal", ValueEnvVar = "MY_HEADER" }]
        };

        var header =
            Assert.Single(
                ProviderCredentialResolver.ResolveExtraHeaders(provider: provider, environment: environment.Object));

        Assert.Equal(expected: "literal", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_SkipsHeadersWithNoResolvableValueOrBlankName()
    {
        var provider = new ProviderOptions
        {
            BaseUrl = "https://example.com",
            Headers =
            [
                new ProviderHeader { Name = "X-Missing", ValueEnvVar = "UNSET_VAR" }, // env var returns null
                new ProviderHeader { Name = "   ", Value = "orphan" }, // blank name
                new ProviderHeader { Name = "X-Kept", Value = "yes" }
            ]
        };

        var resolved = ProviderCredentialResolver.ResolveExtraHeaders(provider: provider,
            environment: Mock.Of<IEnvironmentVariableProvider>());

        var header = Assert.Single(resolved);
        Assert.Equal(expected: "X-Kept", actual: header.Key);
        Assert.Equal(expected: "yes", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_ReadsValueFromSecretStore_WhenNoLiteralOrEnvVar()
    {
        var secretReader = new Mock<ISecretReader>();
        var outValue = "sk-ant-from-store";
        secretReader.Setup(r => r.TryRead("provider:anthropic:header:x-api-key", out outValue)).Returns(true);
        var provider = new ProviderOptions
        {
            BaseUrl = "https://api.anthropic.com",
            Headers =
            [
                new ProviderHeader
                    { Name = "x-api-key", ValueSecretRef = "provider:anthropic:header:x-api-key", Locked = true }
            ]
        };

        var resolved = ProviderCredentialResolver.ResolveExtraHeaders(provider: provider,
            environment: Mock.Of<IEnvironmentVariableProvider>(), secretReader: secretReader.Object);

        var header = Assert.Single(resolved);
        Assert.Equal(expected: "x-api-key", actual: header.Key);
        Assert.Equal(expected: "sk-ant-from-store", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_LiteralAndEnvVarTakePrecedenceOverSecretStore()
    {
        var secretReader = new Mock<ISecretReader>(MockBehavior.Strict);
        var provider = new ProviderOptions
        {
            BaseUrl = "https://api.anthropic.com",
            Headers =
            [
                new ProviderHeader
                {
                    Name = "x-api-key", Value = "literal-wins", ValueSecretRef = "provider:anthropic:header:x-api-key"
                }
            ]
        };

        var header = Assert.Single(ProviderCredentialResolver.ResolveExtraHeaders(provider: provider,
            environment: Mock.Of<IEnvironmentVariableProvider>(), secretReader: secretReader.Object));

        // Strict mock: if the resolver had consulted the secret store, this test would throw on the
        // unconfigured TryRead call instead of getting here.
        Assert.Equal(expected: "literal-wins", actual: header.Value);
    }

    [Fact]
    public void ResolveExtraHeaders_SecretStoreRef_WithNoReaderSupplied_ResolvesToNothing()
    {
        var provider = new ProviderOptions
        {
            BaseUrl = "https://api.anthropic.com",
            Headers =
            [
                new ProviderHeader
                    { Name = "x-api-key", ValueSecretRef = "provider:anthropic:header:x-api-key", Locked = true }
            ]
        };

        var resolved = ProviderCredentialResolver.ResolveExtraHeaders(provider: provider,
            environment: Mock.Of<IEnvironmentVariableProvider>());

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveAwsCredentials_ResolvesAllThree_WhenAccessKeySecretAndSessionTokenConfigured()
    {
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("AWS_ID")).Returns("AKIA123");
        environment.Setup(e => e.GetVariable("AWS_SECRET")).Returns("secret456");
        environment.Setup(e => e.GetVariable("AWS_TOKEN")).Returns("token789");
        var provider = new ProviderOptions
        {
            BaseUrl = "https://bedrock.example",
            AwsAccessKeyIdEnvVar = "AWS_ID",
            AwsSecretAccessKeyEnvVar = "AWS_SECRET",
            AwsSessionTokenEnvVar = "AWS_TOKEN"
        };

        var (accessKeyId, secretAccessKey, sessionToken) =
            ProviderCredentialResolver.ResolveAwsCredentials(provider: provider, environment: environment.Object);

        Assert.Equal(expected: "AKIA123", actual: accessKeyId);
        Assert.Equal(expected: "secret456", actual: secretAccessKey);
        Assert.Equal(expected: "token789", actual: sessionToken);
    }

    [Fact]
    public void ResolveAwsCredentials_OmitsSessionToken_WhenSessionEnvVarNotConfigured()
    {
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("AWS_ID")).Returns("AKIA123");
        environment.Setup(e => e.GetVariable("AWS_SECRET")).Returns("secret456");
        var provider = new ProviderOptions
        {
            BaseUrl = "https://bedrock.example",
            AwsAccessKeyIdEnvVar = "AWS_ID",
            AwsSecretAccessKeyEnvVar = "AWS_SECRET"
            // no AwsSessionTokenEnvVar - a permanent (non-STS) credential pair
        };

        var (accessKeyId, secretAccessKey, sessionToken) =
            ProviderCredentialResolver.ResolveAwsCredentials(provider: provider, environment: environment.Object);

        Assert.Equal(expected: "AKIA123", actual: accessKeyId);
        Assert.Equal(expected: "secret456", actual: secretAccessKey);
        Assert.Null(sessionToken);
    }

    [Fact]
    public void ResolveAwsCredentials_ReturnsAllNull_WhenAccessKeyEnvVarNotConfigured()
    {
        // No AwsAccessKeyIdEnvVar at all -> no explicit override -> defer to the SDK's default chain.
        var provider = new ProviderOptions
        {
            BaseUrl = "https://bedrock.example",
            AwsSecretAccessKeyEnvVar = "AWS_SECRET"
        };
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("AWS_SECRET")).Returns("secret456");

        var result =
            ProviderCredentialResolver.ResolveAwsCredentials(provider: provider, environment: environment.Object);

        Assert.Equal(expected: (null, null, null), actual: result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAwsCredentials_ReturnsAllNull_WhenAResolvedValueIsBlank(string blank)
    {
        // The env var exists but is empty/whitespace: "set but blank" must be treated as unresolved and fall
        // back to the default chain, not accepted as a half-configured override that fails only at call time.
        var environment = new Mock<IEnvironmentVariableProvider>();
        environment.Setup(e => e.GetVariable("AWS_ID")).Returns("AKIA123");
        environment.Setup(e => e.GetVariable("AWS_SECRET")).Returns(blank);
        var provider = new ProviderOptions
        {
            BaseUrl = "https://bedrock.example",
            AwsAccessKeyIdEnvVar = "AWS_ID",
            AwsSecretAccessKeyEnvVar = "AWS_SECRET"
        };

        var result =
            ProviderCredentialResolver.ResolveAwsCredentials(provider: provider, environment: environment.Object);

        Assert.Equal(expected: (null, null, null), actual: result);
    }
}