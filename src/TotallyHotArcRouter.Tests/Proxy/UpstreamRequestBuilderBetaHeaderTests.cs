using Microsoft.AspNetCore.Http;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="UpstreamRequestBuilder"/>'s <c>anthropic-beta</c> pairing for an operator-configured header
/// (ADR-0017 Strip rule 1): a beta value paired with a body field a candidate dropped must not reach the upstream
/// from the provider's own configuration any more than from the client.
/// </summary>
public sealed class UpstreamRequestBuilderBetaHeaderTests
{
    private static ResolvedModelRoute RouteWithConfiguredBeta(string betaValue)
    {
        return new ResolvedModelRoute(ModelName: "claude-haiku", Provider: "anthropic",
            ProviderModelId: "claude-haiku-4-5-20251001", UpstreamBaseUrl: new Uri("https://api.anthropic.com"),
            ExtraHeaders: [new KeyValuePair<string, string>("anthropic-beta", betaValue)],
            ConfiguredHeaderNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anthropic-beta" });
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/messages";
        return context;
    }

    private static string[] BetaValues(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues(name: "anthropic-beta", values: out var values) ? [.. values] : [];
    }

    [Fact]
    public void ConfiguredBeta_LosesTheValuesPairedWithDroppedFields()
    {
        using var request = UpstreamRequestBuilder.Build(context: Context(),
            route: RouteWithConfiguredBeta("context-management-2025-06-27,prompt-caching-scope-2026-01-05"),
            translator: null, rewrittenBody: "{}"u8.ToArray(), droppedBetaPrefixes: ["context-management-"]);

        Assert.Equal(expected: ["prompt-caching-scope-2026-01-05"], actual: BetaValues(request));
    }

    [Fact]
    public void ConfiguredBeta_IsOmitted_WhenEveryValueWasDropped()
    {
        using var request = UpstreamRequestBuilder.Build(context: Context(),
            route: RouteWithConfiguredBeta("context-management-2025-06-27"),
            translator: null, rewrittenBody: "{}"u8.ToArray(), droppedBetaPrefixes: ["context-management-"]);

        Assert.Empty(BetaValues(request));
    }

    [Fact]
    public void ConfiguredBeta_IsForwardedAsConfigured_WhenNothingWasDropped()
    {
        using var request = UpstreamRequestBuilder.Build(context: Context(),
            route: RouteWithConfiguredBeta("context-management-2025-06-27"),
            translator: null, rewrittenBody: "{}"u8.ToArray());

        Assert.Equal(expected: ["context-management-2025-06-27"], actual: BetaValues(request));
    }
}
