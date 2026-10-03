using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Classification;

namespace TotallyHot.ArcRouter.Tests.Router.Classification;

/// <summary>
/// Covers <see cref="SubagentSignalDetector"/> (issue #163): each implemented signal is detected, and every
/// absent, malformed, oversized, duplicated, conflicting or disabled input yields no signal, which is what makes
/// the caller route exactly as it did before the feature.
/// </summary>
public class SubagentSignalDetectorTests
{
    private static readonly SubagentBiasOptions Defaults = new();

    private static JsonObject Body(string model = "auto")
    {
        return new JsonObject { ["model"] = model };
    }

    private static HeaderDictionary Headers(params (string Name, string Value)[] pairs)
    {
        var headers = new HeaderDictionary();
        foreach (var (name, value) in pairs) headers.Append(key: name, value: value);

        return headers;
    }

    private static string CodexMetadata(string threadSource = "subagent", string? parent = "root",
        string? kind = "thread_spawn")
    {
        var node = new JsonObject { ["thread_id"] = "child", ["thread_source"] = threadSource };
        if (parent is not null) node["parent_thread_id"] = parent;

        if (kind is not null) node["subagent_kind"] = kind;

        return node.ToJsonString();
    }

    // ---- Claude Code ----

    [Fact]
    public void Detect_ClaudeCodeAgentIdOnly_ReportsSubagent()
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: Defaults);

        Assert.Equal(expected: new SubagentSignal(Harness: "claude-code", Kind: "subagent",
            Source: "x-claude-code-agent-id", RouteClass: SubagentRouteClass.Normal), actual: signal);
    }

    [Fact]
    public void Detect_HeaderNameIsCaseInsensitive()
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("X-Claude-Code-Agent-Id", "agent-1")),
            requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
    }

    [Theory]
    [InlineData("subagent", "subagent", SubagentRouteClass.Normal)]
    [InlineData("SubAgent", "subagent", SubagentRouteClass.Normal)]
    [InlineData("auxiliary", "auxiliary", SubagentRouteClass.Helper)]
    public void Detect_ClaudeCodeRequestClass_ReportsKind(string requestClass, string expectedKind,
        SubagentRouteClass expectedRoute)
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", requestClass)), requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
        Assert.Equal(expected: expectedKind, actual: signal.Kind);
        Assert.Equal(expected: "x-claude-code-request-class", actual: signal.Source);
        Assert.Equal(expected: expectedRoute, actual: signal.RouteClass);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("compaction")]
    [InlineData("workflow")]
    [InlineData("something-new")]
    public void Detect_ClaudeCodeRequestClassThatIsNotACostSignal_ReportsNothing(string requestClass)
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", requestClass)), requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_MainClassWithAgentId_IsContradictoryAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "main"), ("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_SubagentCompactionKeepsItsAgentIdButIsNotACostSignal()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "compaction"), ("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_ParentAgentIdWithoutAgentId_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-parent-agent-id", "agent-0")), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_NestedAgent_ReportsSubagent()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-claude-code-parent-agent-id", "agent-0")),
            requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
    }

    [Fact]
    public void Detect_SubagentClassWithClassToggleOff_FallsBackToAgentId()
    {
        var options = new SubagentBiasOptions { ClaudeCodeHintHeaders = false };

        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "subagent"), ("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: options);

        Assert.NotNull(signal);
        Assert.Equal(expected: "x-claude-code-agent-id", actual: signal.Source);
    }

    [Fact]
    public void Detect_AuxiliaryClassWithClassToggleOff_ReportsNothing()
    {
        var options = new SubagentBiasOptions { ClaudeCodeHintHeaders = false };

        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "auxiliary")), requestBody: Body(), options: options);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_AgentIdWithAgentIdToggleOff_ReportsNothing()
    {
        var options = new SubagentBiasOptions { ClaudeCodeAgentId = false };

        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: options);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_DuplicateAgentIdHeader_IsAmbiguousAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-claude-code-agent-id", "agent-2")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("café")]
    [InlineData("line\tbreak")]
    public void Detect_BlankOrNonPrintableAgentId_ReportsNothing(string value)
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-claude-code-agent-id", value)),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_OversizedAgentId_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", new string(c: 'a', count: 257))), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    // ---- Claude Code agent type (route class) ----

    [Theory]
    [InlineData("Explore", "explore", SubagentRouteClass.LightSubagent)]
    [InlineData("explore", "explore", SubagentRouteClass.LightSubagent)]
    [InlineData("claude-code-guide", "claude-code-guide", SubagentRouteClass.LightSubagent)]
    [InlineData("Plan", "plan", SubagentRouteClass.Normal)]
    [InlineData("general-purpose", "general-purpose", SubagentRouteClass.Normal)]
    [InlineData("statusline-setup", "statusline-setup", SubagentRouteClass.Normal)]
    [InlineData("custom", "custom", SubagentRouteClass.Normal)]
    [InlineData("teammate", "teammate", SubagentRouteClass.Normal)]
    [InlineData("fork", "fork", SubagentRouteClass.Normal)]
    public void Detect_SubagentWithAgentType_ReportsTypeAndRouteClass(string agentType, string expectedKind,
        SubagentRouteClass expectedRoute)
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "subagent"), ("x-claude-code-agent-id", "agent-1"),
                ("x-claude-code-agent-type", agentType)),
            requestBody: Body(), options: Defaults);

        Assert.Equal(expected: new SubagentSignal(Harness: "claude-code", Kind: expectedKind,
            Source: "x-claude-code-agent-type", RouteClass: expectedRoute), actual: signal);
    }

    [Fact]
    public void Detect_UnknownAgentType_IsReportedAsAPlainSubagentAndNeverEchoed()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "subagent"), ("x-claude-code-agent-id", "agent-1"),
                ("x-claude-code-agent-type", "my-secret-agent-name")),
            requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
        Assert.Equal(expected: "subagent", actual: signal.Kind);
        Assert.Equal(expected: SubagentRouteClass.Normal, actual: signal.RouteClass);
    }

    [Fact]
    public void Detect_AgentTypeWithoutRequestClass_StillNamesTheSubagent()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-claude-code-agent-type", "Explore")),
            requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
        Assert.Equal(expected: SubagentRouteClass.LightSubagent, actual: signal.RouteClass);
    }

    [Fact]
    public void Detect_AgentTypeWithoutAgentId_IsContradictoryAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "subagent"), ("x-claude-code-agent-type", "Explore")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_TypedAuxiliaryRequest_IsContradictoryAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "auxiliary"), ("x-claude-code-agent-id", "agent-1"),
                ("x-claude-code-agent-type", "Explore")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_SubagentSideRequest_KeepsItsAgentIdAndIsAHelper()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "auxiliary"), ("x-claude-code-agent-id", "agent-1")),
            requestBody: Body(), options: Defaults);

        Assert.NotNull(signal);
        Assert.Equal(expected: SubagentRouteClass.Helper, actual: signal.RouteClass);
    }

    [Fact]
    public void Detect_ExploreWithHintHeadersOff_FallsBackToAPlainSubagent()
    {
        var options = new SubagentBiasOptions { ClaudeCodeHintHeaders = false };

        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-request-class", "subagent"), ("x-claude-code-agent-id", "agent-1"),
                ("x-claude-code-agent-type", "Explore")),
            requestBody: Body(), options: options);

        Assert.NotNull(signal);
        Assert.Equal(expected: "x-claude-code-agent-id", actual: signal.Source);
        Assert.Equal(expected: SubagentRouteClass.Normal, actual: signal.RouteClass);
    }

    [Fact]
    public void Detect_DuplicateAgentType_IsAmbiguousAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-claude-code-agent-type", "Explore"),
                ("x-claude-code-agent-type", "Plan")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    // ---- Codex ----

    [Theory]
    [InlineData("thread_spawn")]
    [InlineData("memory_consolidation")]
    public void Detect_CodexSubagentKind_ReportsKind(string kind)
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-codex-turn-metadata", CodexMetadata(kind: kind))), requestBody: Body(),
            options: Defaults);

        Assert.Equal(expected: new SubagentSignal(Harness: "codex", Kind: kind, Source: "x-codex-turn-metadata",
            RouteClass: SubagentRouteClass.Normal), actual: signal);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("guardian")]
    [InlineData("review")]
    [InlineData("agent_job:nightly")]
    [InlineData("other")]
    public void Detect_CodexKindThatIsNotACostSignal_ReportsNothing(string kind)
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-codex-turn-metadata", CodexMetadata(kind: kind))), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexKindWithoutSubagentThreadSource_IsContradictoryAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-codex-turn-metadata", CodexMetadata(threadSource: "user"))), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexWithoutParentThreadId_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-codex-turn-metadata", CodexMetadata(parent: null))), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexWithoutKind_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-codex-turn-metadata", CodexMetadata(kind: null))), requestBody: Body(),
            options: Defaults);

        Assert.Null(signal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"subagent\"")]
    [InlineData("{\"thread_source\":")]
    public void Detect_CodexMalformedMetadata_ReportsNothing(string value)
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-codex-turn-metadata", value)),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexNonStringFields_ReportsNothing()
    {
        var value = """{"thread_source":"subagent","parent_thread_id":7,"subagent_kind":"thread_spawn"}""";

        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-codex-turn-metadata", value)),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexOversizedMetadata_ReportsNothing()
    {
        var padded = CodexMetadata().TrimEnd('}') + ",\"pad\":\"" + new string(c: 'x', count: 9_000) + "\"}";

        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-codex-turn-metadata", padded)),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CodexWithToggleOff_ReportsNothing()
    {
        var options = new SubagentBiasOptions { CodexTurnMetadata = false };

        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-codex-turn-metadata", CodexMetadata())),
            requestBody: Body(), options: options);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_LegacyCodexSubagentHeaderAlone_IsNotImplementedAndReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-openai-subagent", "collab_spawn")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    // ---- Copilot aliases ----

    [Theory]
    [InlineData("copilot-utility")]
    [InlineData("copilot-utility-small")]
    [InlineData("Copilot-Utility-Small")]
    [InlineData("copilot-utility-tiny")]
    [InlineData("  copilot-utility  ")]
    public void Detect_CopilotUtilityAlias_ReportsAlias(string model)
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(), requestBody: Body(model: model),
            options: Defaults);

        Assert.Equal(expected: new SubagentSignal(Harness: "copilot", Kind: "utility-alias", Source: "model",
            RouteClass: SubagentRouteClass.Helper), actual: signal);
    }

    [Theory]
    [InlineData("copilot-utilit")]
    [InlineData("my-copilot-utility")]
    [InlineData("gpt-5.4")]
    [InlineData("auto")]
    public void Detect_ModelThatIsNotAUtilityAlias_ReportsNothing(string model)
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(), requestBody: Body(model: model),
            options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_CopilotAliasWithToggleOff_ReportsNothing()
    {
        var options = new SubagentBiasOptions { CopilotUtilityAlias = false };

        var signal = SubagentSignalDetector.Detect(headers: Headers(), requestBody: Body(model: "copilot-utility"),
            options: options);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_NonStringModel_ReportsNothing()
    {
        var body = new JsonObject { ["model"] = 5 };

        var signal = SubagentSignalDetector.Detect(headers: Headers(), requestBody: body, options: Defaults);

        Assert.Null(signal);
    }

    [Theory]
    [InlineData("copilot-utility", true)]
    [InlineData(" Copilot-Utility-Small ", true)]
    [InlineData("auto", false)]
    [InlineData(null, false)]
    public void IsCopilotUtilityAlias_MatchesThePrefixCaseInsensitively(string? model, bool expected)
    {
        Assert.Equal(expected: expected, actual: SubagentSignalDetector.IsCopilotUtilityAlias(model));
    }

    // ---- Cross-cutting ----

    [Fact]
    public void Detect_NoMarkersAtAll_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("authorization", "Bearer x")),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_MasterSwitchOff_IgnoresEveryMarker()
    {
        var options = new SubagentBiasOptions { Enabled = false };

        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-codex-turn-metadata", CodexMetadata())),
            requestBody: Body(model: "copilot-utility"), options: options);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_TwoHarnessesClaimingOneRequest_ReportsNothing()
    {
        var signal = SubagentSignalDetector.Detect(
            headers: Headers(("x-claude-code-agent-id", "agent-1"), ("x-codex-turn-metadata", CodexMetadata())),
            requestBody: Body(), options: Defaults);

        Assert.Null(signal);
    }

    [Fact]
    public void Detect_AMalformedMarkerDoesNotHideAnUnrelatedValidOne()
    {
        var signal = SubagentSignalDetector.Detect(headers: Headers(("x-codex-turn-metadata", "not json")),
            requestBody: Body(model: "copilot-utility"), options: Defaults);

        Assert.NotNull(signal);
        Assert.Equal(expected: "copilot", actual: signal.Harness);
    }

    [Fact]
    public void ToLabel_JoinsHarnessAndKind()
    {
        var signal = new SubagentSignal(Harness: "codex", Kind: "thread_spawn", Source: "x-codex-turn-metadata",
            RouteClass: SubagentRouteClass.Normal);

        Assert.Equal(expected: "codex/thread_spawn", actual: signal.ToLabel());
    }

    [Fact]
    public void Detect_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SubagentSignalDetector.Detect(headers: null!, requestBody: Body(), options: Defaults));
        Assert.Throws<ArgumentNullException>(() =>
            SubagentSignalDetector.Detect(headers: Headers(), requestBody: null!, options: Defaults));
        Assert.Throws<ArgumentNullException>(() =>
            SubagentSignalDetector.Detect(headers: Headers(), requestBody: Body(), options: null!));
    }
}
