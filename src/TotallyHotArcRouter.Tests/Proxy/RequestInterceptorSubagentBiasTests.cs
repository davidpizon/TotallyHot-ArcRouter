using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers issue #163's wiring in <see cref="RequestInterceptor.ResolveModelRouteAsync"/>: a verified harness
/// marker turns the request into utility traffic on the agentic-routing path only, and every other case
/// (no marker, switches off, an explicit model pick, invalid JSON) leaves routing exactly as it was.
/// </summary>
public class RequestInterceptorSubagentBiasTests
{
    private const string ClaudeCodeAgentIdHeader = "x-claude-code-agent-id";

    private static (RequestInterceptor Interceptor, CapturingPolicy Policy) Build(
        SubagentBiasOptions? options = null)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new CapturingPolicy("kimi-k2.5");
        var monitor = options is null
            ? null
            : new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { SubagentBias = options });
        var interceptor = new RequestInterceptor(
            logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver,
            routingPolicy: policy,
            routingOptionsMonitor: monitor);

        return (interceptor, policy);
    }

    private static DefaultHttpContext Context(string body, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        foreach (var (name, value) in headers) context.Request.Headers.Append(key: name, value: value);

        return context;
    }

    [Fact]
    public async Task AutoRequest_WithClaudeCodeAgentId_RoutesAsUtilityAndRecordsTheSignal()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.Equal(expected: "claude-code/subagent", actual: result.Classification!.Subagent!.ToLabel());
        Assert.True(result.Classification.IsUtility);
    }

    [Fact]
    public async Task AutoRequest_WithoutAnyMarker_IsNotUtilityAndCarriesNoSignal()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto","messages":[{"role":"user","content":"Refactor the billing module and add tests for the retry path so that it handles every timeout case we discussed earlier today in the planning meeting."}]}"""),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task UnresolvedModel_WithCopilotUtilityAlias_RoutesAsUtility()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"copilot-utility-small"}"""),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.Equal(expected: "copilot/utility-alias", actual: result.Classification!.Subagent!.ToLabel());
    }

    [Fact]
    public async Task AutoRequest_WithCodexSubagentMetadata_RoutesAsUtility()
    {
        var (interceptor, policy) = Build();
        const string metadata =
            """{"thread_id":"c","parent_thread_id":"p","thread_source":"subagent","subagent_kind":"thread_spawn"}""";

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", ("x-codex-turn-metadata", metadata)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.Equal(expected: "codex/thread_spawn", actual: result.Classification!.Subagent!.ToLabel());
    }

    [Fact]
    public async Task MasterSwitchOff_IgnoresTheMarker()
    {
        var (interceptor, policy) = Build(new SubagentBiasOptions { Enabled = false });

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task PerSignalToggleOff_IgnoresThatMarker()
    {
        var (interceptor, policy) = Build(new SubagentBiasOptions { ClaudeCodeAgentId = false });

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task ToggleChangedAtRuntime_AppliesToTheNextRequest()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var policy = new CapturingPolicy("gpt-5.4");
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions());
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver, routingPolicy: policy, routingOptionsMonitor: monitor);

        await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(policy.LastContext!.IsUtility);

        monitor.Set(new RoutingOptions { SubagentBias = new SubagentBiasOptions { Enabled = false } });
        await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(policy.LastContext.IsUtility);
    }

    [Fact]
    public async Task ExplicitConfiguredModel_WithMarker_KeepsItsModelAndItsClassification()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"gpt-5.4"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.Route!.ModelName);
        Assert.Null(policy.LastContext);
        Assert.Null(result.Classification!.Subagent);
        Assert.False(result.Classification.IsUtility);
    }

    [Fact]
    public async Task MalformedMarker_FallsBackToExistingRouting()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", ("x-codex-turn-metadata", "{not json")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task InvalidRequestJson_IsRejectedBeforeAnyMarkerIsRead()
    {
        var (interceptor, policy) = Build();

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("not json", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(policy.LastContext);
    }

    [Fact]
    public async Task MarkedRequest_NeverResolvesOutsideTheConfiguredModels()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver, routingPolicy: new CapturingPolicy("not-a-configured-model"));

        var result = await interceptor.ResolveModelRouteAsync(
            context: Context("""{"model":"auto"}""", (ClaudeCodeAgentIdHeader, "agent-1")),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.Route!.ModelName);
    }

    private sealed class CapturingPolicy(string selection) : IRoutingPolicy
    {
        public RoutingContext? LastContext { get; private set; }

        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(selection);
        }
    }
}
