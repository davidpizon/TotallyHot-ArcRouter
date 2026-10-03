using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers issue #163's wiring in <see cref="RequestInterceptor.ResolveModelRouteAsync"/>, per
/// <c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>: a helper becomes utility traffic, a
/// light subagent carries the near-best floor, every other subagent routes as without a signal, and the bias applies
/// only when the client delegated the model choice. No marker, switches off, an explicit model pick or invalid JSON
/// leave routing exactly as it was.
/// </summary>
public class RequestInterceptorSubagentBiasTests
{
    private static readonly (string, string) AgentId = ("x-claude-code-agent-id", "agent-1");
    private static readonly (string, string) AuxiliaryClass = ("x-claude-code-request-class", "auxiliary");
    private static readonly (string, string) SubagentClass = ("x-claude-code-request-class", "subagent");
    private static readonly (string, string) ExploreType = ("x-claude-code-agent-type", "Explore");
    private static readonly (string, string) GeneralPurposeType = ("x-claude-code-agent-type", "general-purpose");

    private static (RequestInterceptor Interceptor, CapturingPolicy Policy) Build(
        SubagentBiasOptions? options = null, ILogger<RequestInterceptor>? logger = null)
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4"),
            ("kimi-k2.5", "moonshot", "kimi-k2.5"));
        var policy = new CapturingPolicy("kimi-k2.5");
        var monitor = options is null
            ? null
            : new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { SubagentBias = options });
        var interceptor = new RequestInterceptor(
            logger: logger ?? Mock.Of<ILogger<RequestInterceptor>>(),
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

    private static Task<ModelRouteResolutionResult> Resolve(RequestInterceptor interceptor, string body,
        params (string Name, string Value)[] headers)
    {
        return interceptor.ResolveModelRouteAsync(context: Context(body: body, headers: headers),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    // ---- Route classes on a delegated request ----

    [Fact]
    public async Task AutoRequest_ClaudeCodeHelper_RoutesAsUtility()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.Null(policy.LastContext.NearBestValueFloor);
        Assert.Equal(expected: "claude-code/auxiliary", actual: result.Classification!.Subagent!.ToLabel());
        Assert.True(result.Classification.IsUtility);
    }

    [Fact]
    public async Task CopilotUtilityAlias_RoutesAsUtility()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, """{"model":"copilot-utility-small"}""");

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.Equal(expected: "copilot/utility-alias", actual: result.Classification!.Subagent!.ToLabel());
    }

    [Fact]
    public async Task AutoRequest_ExploreSubagent_CarriesTheNearBestFloorAndIsNotUtility()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, """{"model":"auto"}""", SubagentClass, AgentId, ExploreType);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Equal(expected: 0.9, actual: policy.LastContext.NearBestValueFloor);
        Assert.Equal(expected: "claude-code/explore", actual: result.Classification!.Subagent!.ToLabel());
    }

    [Fact]
    public async Task AutoRequest_ExploreSubagent_OutranksThePayloadHeuristic()
    {
        var (interceptor, policy) = Build();

        // max_tokens <= 64 makes the payload heuristic call this utility; the documented marker wins.
        var result = await Resolve(interceptor, """{"model":"auto","max_tokens":16}""", SubagentClass, AgentId,
            ExploreType);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.NotNull(policy.LastContext.NearBestValueFloor);
    }

    [Fact]
    public async Task AutoRequest_ExploreSubagent_UsesTheLiveRelativeFloor()
    {
        var (interceptor, policy) = Build(new SubagentBiasOptions { LightSubagentRelativeFloor = 0.75 });

        await Resolve(interceptor, """{"model":"auto"}""", SubagentClass, AgentId, ExploreType);

        Assert.Equal(expected: 0.75, actual: policy.LastContext!.NearBestValueFloor);
    }

    [Theory]
    [InlineData("untyped-claude-code")]
    [InlineData("general-purpose")]
    [InlineData("codex-thread-spawn")]
    public async Task AutoRequest_NormalSubagent_RoutesExactlyAsWithoutASignal(string signal)
    {
        var (baseline, baselinePolicy) = Build();
        var (interceptor, policy) = Build();

        await Resolve(baseline, """{"model":"auto"}""");
        var result = await Resolve(interceptor, """{"model":"auto"}""", NormalSubagentHeaders[signal]);

        Assert.True(result.IsSuccess);
        AssertSameRouting(expected: baselinePolicy.LastContext!, actual: policy.LastContext!);
        Assert.NotNull(result.Classification!.Subagent);
        Assert.False(result.Classification.IsUtility);
    }

    /// <summary>Signals whose route class is <c>Normal</c>: untyped and open-ended Claude Code subagents, and Codex.</summary>
    private static readonly Dictionary<string, (string, string)[]> NormalSubagentHeaders = new()
    {
        ["untyped-claude-code"] = [AgentId],
        ["general-purpose"] = [SubagentClass, AgentId, GeneralPurposeType],
        ["codex-thread-spawn"] =
        [
            ("x-codex-turn-metadata",
                """{"thread_id":"c","parent_thread_id":"p","thread_source":"subagent","subagent_kind":"thread_spawn"}""")
        ]
    };

    /// <summary>
    /// Asserts two routing contexts would be dispatched identically: same dimension, utility flag, near-best floor
    /// and candidate menu. Record equality alone compares the candidate lists by reference.
    /// </summary>
    private static void AssertSameRouting(RoutingContext expected, RoutingContext actual)
    {
        Assert.Equal(expected: expected.Dimension, actual: actual.Dimension);
        Assert.Equal(expected: expected.IsUtility, actual: actual.IsUtility);
        Assert.Equal(expected: expected.NearBestValueFloor, actual: actual.NearBestValueFloor);
        Assert.Equal(expected: expected.Candidates, actual: actual.Candidates);
    }

    // ---- Delegation rule ----

    [Fact]
    public async Task UnresolvedSpecificModelName_HelperSignal_RoutesWithoutBias()
    {
        var (baseline, baselinePolicy) = Build();
        var (interceptor, policy) = Build();

        // The auto-mode classifier names its model; an unknown specific name still takes the unresolved-name
        // fallback, but never the cheap path.
        await Resolve(baseline, """{"model":"claude-sonnet-5"}""");
        var result = await Resolve(interceptor, """{"model":"claude-sonnet-5"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        AssertSameRouting(expected: baselinePolicy.LastContext!, actual: policy.LastContext!);
        Assert.False(result.Classification!.IsUtility);
        Assert.Equal(expected: "claude-code/auxiliary", actual: result.Classification.Subagent!.ToLabel());
    }

    [Fact]
    public async Task UnresolvedSpecificModelName_ExploreSignal_GetsNoFloor()
    {
        var (interceptor, policy) = Build();

        await Resolve(interceptor, """{"model":"claude-opus-5"}""", SubagentClass, AgentId, ExploreType);

        Assert.Null(policy.LastContext!.NearBestValueFloor);
        Assert.False(policy.LastContext.IsUtility);
    }

    [Fact]
    public async Task RouterAliasName_CountsAsDelegation()
    {
        var (interceptor, policy) = Build();

        await Resolve(interceptor, """{"model":"TotallyHot-ArcRouter"}""", AuxiliaryClass);

        Assert.True(policy.LastContext!.IsUtility);
    }

    [Fact]
    public async Task ExplicitConfiguredModel_WithMarker_KeepsItsModelAndItsClassification()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, """{"model":"gpt-5.4"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.Route!.ModelName);
        Assert.Null(policy.LastContext);
        Assert.Null(result.Classification!.Subagent);
        Assert.False(result.Classification.IsUtility);
    }

    // ---- Switches, fallbacks and boundaries ----

    [Fact]
    public async Task AutoRequest_WithoutAnyMarker_IsNotUtilityAndCarriesNoSignal()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor,
            """{"model":"auto","messages":[{"role":"user","content":"Refactor the billing module and add tests for the retry path so that it handles every timeout case we discussed earlier today in the planning meeting."}]}""");

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(policy.LastContext.NearBestValueFloor);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task MasterSwitchOff_IgnoresTheMarker()
    {
        var (interceptor, policy) = Build(new SubagentBiasOptions { Enabled = false });

        var result = await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task HintHeaderToggleOff_IgnoresTheHelperMarker()
    {
        var (interceptor, policy) = Build(new SubagentBiasOptions { ClaudeCodeHintHeaders = false });

        var result = await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);

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

        await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);
        Assert.True(policy.LastContext!.IsUtility);

        monitor.Set(new RoutingOptions { SubagentBias = new SubagentBiasOptions { Enabled = false } });
        await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);

        Assert.False(policy.LastContext.IsUtility);
    }

    [Fact]
    public async Task MalformedMarker_FallsBackToExistingRouting()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, """{"model":"auto"}""", ("x-codex-turn-metadata", "{not json"));

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(result.Classification!.Subagent);
    }

    [Fact]
    public async Task InvalidRequestJson_IsRejectedBeforeAnyMarkerIsRead()
    {
        var (interceptor, policy) = Build();

        var result = await Resolve(interceptor, "not json", AuxiliaryClass);

        Assert.False(result.IsSuccess);
        Assert.Null(policy.LastContext);
    }

    [Fact]
    public async Task MarkedRequest_NeverResolvesOutsideTheConfiguredModels()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver, routingPolicy: new CapturingPolicy("not-a-configured-model"));

        var result = await Resolve(interceptor, """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "gpt-5.4", actual: result.Route!.ModelName);
    }

    // ---- Native Anthropic Messages traffic (ADR-0022 option A) ----

    private static (RequestInterceptor Interceptor, CapturingPolicy Policy, CapturingLogger Logger) BuildMixed(
        bool includeAnthropic)
    {
        var models = new List<(string, string, string)> { ("gpt-5.4", "openai", "gpt-5.4"), ("local", "lmstudio", "local") };
        if (includeAnthropic) models.Add(("claude-haiku", "anthropic", "claude-haiku"));
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList([.. models]);
        var policy = new CapturingPolicy(includeAnthropic ? "claude-haiku" : "gpt-5.4");
        var logger = new CapturingLogger();
        var interceptor = new RequestInterceptor(logger: logger, modelRouteResolver: resolver, routingPolicy: policy);
        return (interceptor, policy, logger);
    }

    private static Task<ModelRouteResolutionResult> ResolveAt(RequestInterceptor interceptor, string path,
        string body, params (string Name, string Value)[] headers)
    {
        var context = Context(body: body, headers: headers);
        context.Request.Path = path;
        return interceptor.ResolveModelRouteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NativeMessagesHelper_OnlyAnthropicCandidatesAreOffered()
    {
        var (interceptor, policy, _) = BuildMixed(includeAnthropic: true);

        var result = await ResolveAt(interceptor, "/v1/messages", """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.True(policy.LastContext!.IsUtility);
        Assert.All(policy.LastContext.Candidates, c => Assert.Equal(expected: "anthropic", actual: c.Provider));
        Assert.Equal(expected: "claude-haiku", actual: result.Route!.ModelName);
    }

    [Fact]
    public async Task NativeMessagesExplore_OnlyAnthropicCandidatesAreOffered()
    {
        var (interceptor, policy, _) = BuildMixed(includeAnthropic: true);

        await ResolveAt(interceptor, "/v1/messages/", """{"model":"auto"}""", SubagentClass, AgentId, ExploreType);

        Assert.NotNull(policy.LastContext!.NearBestValueFloor);
        Assert.All(policy.LastContext.Candidates, c => Assert.Equal(expected: "anthropic", actual: c.Provider));
    }

    /// <summary>
    /// The three ways a biased native Messages request reaches the memory-ranking fallback: a policy pick outside
    /// the anthropic-only menu, a policy that throws, and no policy at all. Each must keep the restriction.
    /// </summary>
    public static TheoryData<string> FallbackPolicies => ["ineligible-pick", "throws", "none"];

    [Theory]
    [MemberData(nameof(FallbackPolicies))]
    public async Task NativeMessagesHelper_MemoryFallback_StaysOnAnthropic(string policyKind)
    {
        // Configured non-anthropic models first, so an unrestricted fallback would pick one of them.
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"),
            ("local", "lmstudio", "local"), ("claude-haiku", "anthropic", "claude-haiku"));
        IRoutingPolicy? policy = policyKind switch
        {
            "ineligible-pick" => new CapturingPolicy("gpt-5.4"),
            "throws" => new ThrowingPolicy(),
            _ => null
        };
        var interceptor = new RequestInterceptor(logger: new CapturingLogger(), modelRouteResolver: resolver,
            routingPolicy: policy);

        var result = await ResolveAt(interceptor, "/v1/messages", """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: "anthropic", actual: result.Route!.Provider);
    }

    [Fact]
    public async Task NativeMessagesHelper_MemoryFallbackWithNoAnthropicCandidate_StillServesTheRequest()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"),
            ("local", "lmstudio", "local"));
        var interceptor = new RequestInterceptor(logger: new CapturingLogger(), modelRouteResolver: resolver,
            routingPolicy: new ThrowingPolicy());

        var result = await ResolveAt(interceptor, "/v1/messages", """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(expected: "anthropic", actual: result.Route!.Provider);
    }

    [Fact]
    public async Task NativeMessagesHelper_NoAnthropicCandidate_WithdrawsTheBias()
    {
        var (interceptor, policy, logger) = BuildMixed(includeAnthropic: false);

        var result = await ResolveAt(interceptor, "/v1/messages", """{"model":"auto"}""", AuxiliaryClass);

        Assert.True(result.IsSuccess);
        Assert.False(policy.LastContext!.IsUtility);
        Assert.Null(policy.LastContext.NearBestValueFloor);
        Assert.Equal(2, actual: policy.LastContext.Candidates.Count);
        Assert.False(result.Classification!.IsUtility);
        Assert.Equal(expected: "claude-code/auxiliary", actual: result.Classification.Subagent!.ToLabel());
        Assert.Contains(collection: logger.Messages, filter: m => m.Contains("route=normal"));
    }

    [Fact]
    public async Task ChatCompletionsHelper_IsNotRestricted()
    {
        var (interceptor, policy, _) = BuildMixed(includeAnthropic: true);

        await ResolveAt(interceptor, "/v1/chat/completions", """{"model":"copilot-utility"}""");

        Assert.True(policy.LastContext!.IsUtility);
        Assert.Equal(3, actual: policy.LastContext.Candidates.Count);
    }

    [Fact]
    public async Task NativeMessagesNormalSubagent_IsNotRestricted()
    {
        var (interceptor, policy, _) = BuildMixed(includeAnthropic: true);

        await ResolveAt(interceptor, "/v1/messages", """{"model":"auto"}""", SubagentClass, AgentId, GeneralPurposeType);

        Assert.Equal(3, actual: policy.LastContext!.Candidates.Count);
    }

    // ---- Routing log line ----

    [Theory]
    [InlineData("none", "subagentSignal=none, route=none")]
    [InlineData("helper", "isUtility=True, subagentSignal=claude-code/auxiliary, route=helper")]
    [InlineData("explore", "subagentSignal=claude-code/explore, route=light-subagent")]
    [InlineData("general-purpose", "subagentSignal=claude-code/general-purpose, route=normal")]
    [InlineData("undelegated-helper", "subagentSignal=claude-code/auxiliary, route=normal")]
    public async Task RoutingLogLine_NamesTheSignalAndTheRouteApplied(string scenario, string expected)
    {
        var logger = new CapturingLogger();
        var (interceptor, _) = Build(logger: logger);
        var (body, headers) = LogLineScenarios[scenario];

        await Resolve(interceptor, body, headers);

        var line = Assert.Single(logger.Messages, m => m.Contains("Routing policy selected 'kimi-k2.5'"));
        Assert.Contains(expectedSubstring: expected, actualString: line);
    }

    /// <summary>One request per route the log line can report.</summary>
    private static readonly Dictionary<string, (string Body, (string, string)[] Headers)> LogLineScenarios = new()
    {
        ["none"] = ("""{"model":"auto"}""", []),
        ["helper"] = ("""{"model":"auto"}""", [AuxiliaryClass]),
        ["explore"] = ("""{"model":"auto"}""", [SubagentClass, AgentId, ExploreType]),
        ["general-purpose"] = ("""{"model":"auto"}""", [SubagentClass, AgentId, GeneralPurposeType]),
        ["undelegated-helper"] = ("""{"model":"claude-sonnet-5"}""", [AuxiliaryClass])
    };

    [Fact]
    public async Task RoutingLogLine_NeverEchoesAForgedHeaderValue()
    {
        var logger = new CapturingLogger();
        var (interceptor, _) = Build(logger: logger);

        await Resolve(interceptor, """{"model":"auto"}""", ("x-claude-code-agent-id", "forged-agent-id-marker"),
            ("x-claude-code-agent-type", "forged-agent-type-marker"));

        Assert.DoesNotContain(collection: logger.Messages,
            filter: m => m.Contains("forged-agent-id-marker") || m.Contains("forged-agent-type-marker"));
    }

    private sealed class CapturingLogger : ILogger<RequestInterceptor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(arg1: state, arg2: exception));
        }
    }

    /// <summary>A policy whose selection always throws, sending the request to the memory-ranking fallback.</summary>
    private sealed class ThrowingPolicy : IRoutingPolicy
    {
        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("policy failure");
        }
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
