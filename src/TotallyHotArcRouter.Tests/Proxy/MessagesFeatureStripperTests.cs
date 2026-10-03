using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="MessagesFeatureStripper"/> (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>
/// Amendment 1): what a router-chosen candidate's copy of a native Messages request drops, decided only by the
/// candidate model's own capability record and the names the request carries.
/// <para>
/// The cases that carry weight are the captured Claude Code shapes against the real Haiku 4.5 record, the rule that
/// an unknown record or key changes nothing, and the pairing of each removed body field with its beta value.
/// </para>
/// </summary>
public sealed class MessagesFeatureStripperTests
{
    private static JsonObject Parse(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static JsonObject PlanAndApply(string json, out MessagesFeatureStrip strip,
        TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ModelFeatureSupport? support = null)
    {
        var body = Parse(json);
        strip = MessagesFeatureStripper.Plan(body: body, support: support ?? Haiku45());
        MessagesFeatureStripper.Apply(body: body, strip: strip);
        return body;
    }

    // ----- The captured shapes against the Haiku 4.5 record -----

    [Fact]
    public void ExploreShape_OnHaiku_DropsAdaptiveThinkingEffortAndTheClearThinkingEdit()
    {
        var body = PlanAndApply(ExploreBody, out var strip);

        Assert.Equal(
            expected: ["thinking.adaptive", "output_config.effort", "context_management.clear_thinking_20251015",
                "context_management"],
            actual: strip.Features);
        Assert.Equal(expected: [MessagesFeatureStripper.EffortBetaPrefix, MessagesFeatureStripper.ContextManagementBetaPrefix],
            actual: strip.BetaPrefixes);
        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        Assert.Null(body["context_management"]);
    }

    [Fact]
    public void ExploreShape_OnHaiku_LeavesSystemToolsAndMessagesUntouched()
    {
        var original = Parse(ExploreBody);
        var body = PlanAndApply(ExploreBody, out _);

        // Strip rule 2: the prefix the thinking-binding check compares must be byte-identical.
        Assert.Equal(expected: original["system"]!.ToJsonString(), actual: body["system"]!.ToJsonString());
        Assert.Equal(expected: original["tools"]!.ToJsonString(), actual: body["tools"]!.ToJsonString());
        Assert.Equal(expected: original["messages"]!.ToJsonString(), actual: body["messages"]!.ToJsonString());
        Assert.Equal(expected: 32000, actual: body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void HelperShape_OnHaiku_DropsOnlyEffort()
    {
        var body = PlanAndApply(HelperBody, out var strip);

        Assert.Equal(expected: ["output_config.effort"], actual: strip.Features);
        Assert.Equal(expected: [MessagesFeatureStripper.EffortBetaPrefix], actual: strip.BetaPrefixes);
        Assert.Null(body["output_config"]);
        Assert.NotNull(body["messages"]);
    }

    [Fact]
    public void ExploreShape_OnACapableModel_RemovesNothing()
    {
        var strip = MessagesFeatureStripper.Plan(body: Parse(ExploreBody), support: Capable());

        Assert.Same(expected: MessagesFeatureStrip.None, actual: strip);
        Assert.True(strip.IsEmpty);
        Assert.Null(strip.HeaderValue);
    }

    [Fact]
    public void Plan_NeverChangesTheBodyItReads()
    {
        var body = Parse(ExploreBody);
        var before = body.ToJsonString();

        MessagesFeatureStripper.Plan(body: body, support: Haiku45());

        Assert.Equal(expected: before, actual: body.ToJsonString());
    }

    // ----- Unknown means unchanged -----

    [Fact]
    public void ARecordThatSaysNothing_RemovesNothing()
    {
        Assert.True(MessagesFeatureStripper.Plan(body: Parse(ExploreBody), support: Record("{}")).IsEmpty);
    }

    [Fact]
    public void NullOrNonBooleanFlags_AreUnknown_AndRemoveNothing()
    {
        var support = Record("""
            {"thinking":{"types":{"adaptive":null}},"effort":{"supported":"no"},"context_management":null}
            """);

        Assert.True(MessagesFeatureStripper.Plan(body: Parse(ExploreBody), support: support).IsEmpty);
    }

    [Fact]
    public void AThinkingTypeTheRecordDoesNotName_IsLeftAlone()
    {
        // The classifier's own shape: thinking disabled. "disabled" is not a key in thinking.types.
        var strip = MessagesFeatureStripper.Plan(body: Parse("""{"thinking":{"type":"disabled"}}"""),
            support: Haiku45());

        Assert.True(strip.IsEmpty);
    }

    [Fact]
    public void AnEffortLevelTheRecordDoesNotName_IsLeftAlone_WhenEffortItselfIsSupported()
    {
        var support = Record("""{"effort":{"supported":true,"high":{"supported":true}}}""");

        var strip = MessagesFeatureStripper.Plan(body: Parse("""{"output_config":{"effort":"ultra"}}"""),
            support: support);

        Assert.True(strip.IsEmpty);
    }

    // ----- Per-key decisions -----

    [Fact]
    public void AnUnsupportedEffortLevel_IsRemoved_EvenWhenEffortIsSupported()
    {
        var support = Record("""{"effort":{"supported":true,"high":{"supported":true},"xhigh":{"supported":false}}}""");

        var body = PlanAndApply("""{"output_config":{"effort":"xhigh"}}""", out var strip, support);

        Assert.Equal(expected: ["output_config.effort"], actual: strip.Features);
        Assert.Null(body["output_config"]);
    }

    [Fact]
    public void RemovingEffort_KeepsTheRestOfOutputConfig()
    {
        var body = PlanAndApply("""{"output_config":{"effort":"high","format":{"type":"json_schema"}}}""", out _);

        var outputConfig = Assert.IsType<JsonObject>(body["output_config"]);
        Assert.Null(outputConfig["effort"]);
        Assert.NotNull(outputConfig["format"]);
    }

    [Fact]
    public void ThinkingUnsupportedAltogether_RemovesAnyThinkingType()
    {
        var support = Record("""{"thinking":{"supported":false}}""");

        var strip = MessagesFeatureStripper.Plan(body: Parse("""{"thinking":{"type":"enabled","budget_tokens":2048}}"""),
            support: support);

        Assert.Equal(expected: ["thinking.enabled"], actual: strip.Features);
        Assert.Empty(strip.BetaPrefixes);
    }

    [Fact]
    public void AnUnsupportedStrategy_IsRemoved_AndSupportedEditsAndTheBetaValueStay()
    {
        var body = PlanAndApply("""
            {"context_management":{"edits":[{"type":"clear_tool_uses_20250919"},{"type":"compact_20260112"}]}}
            """, out var strip);

        Assert.Equal(expected: ["context_management.compact_20260112"], actual: strip.Features);
        Assert.Empty(strip.BetaPrefixes);
        var edit = Assert.Single(body["context_management"]!["edits"]!.AsArray());
        Assert.Equal(expected: "clear_tool_uses_20250919", actual: edit!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AClearThinkingEdit_StaysWhenThinkingStays()
    {
        // Haiku supports clear_thinking; with no thinking field to remove, the edit is kept.
        var strip = MessagesFeatureStripper.Plan(
            body: Parse("""{"context_management":{"edits":[{"type":"clear_thinking_20251015","keep":"all"}]}}"""),
            support: Haiku45());

        Assert.True(strip.IsEmpty);
    }

    [Fact]
    public void ContextManagementWithOtherMembers_KeepsTheObjectAndTheBetaValue()
    {
        var body = PlanAndApply("""
            {"context_management":{"edits":[{"type":"compact_20260112"}],"other":true}}
            """, out var strip);

        Assert.Equal(expected: ["context_management.compact_20260112"], actual: strip.Features);
        Assert.Empty(strip.BetaPrefixes);
        Assert.Empty(body["context_management"]!["edits"]!.AsArray());
        Assert.True(body["context_management"]!["other"]!.GetValue<bool>());
    }

    [Fact]
    public void ContextManagementUnsupported_RemovesTheWholeObjectWithItsBetaValue()
    {
        var support = Record("""{"context_management":{"supported":false}}""");

        var body = PlanAndApply("""{"context_management":{"edits":[{"type":"clear_tool_uses_20250919"}]}}""",
            out var strip, support);

        Assert.Equal(expected: ["context_management"], actual: strip.Features);
        Assert.Equal(expected: [MessagesFeatureStripper.ContextManagementBetaPrefix], actual: strip.BetaPrefixes);
        Assert.Null(body["context_management"]);
    }

    [Fact]
    public void NoStrippableField_RemovesNothing()
    {
        Assert.True(MessagesFeatureStripper.Plan(body: Parse("""{"model":"auto","messages":[]}"""),
            support: Haiku45()).IsEmpty);
    }

    [Fact]
    public void FeatureNames_AreRestrictedToASafeAlphabet()
    {
        // Only a name the record itself carries can be removed, but the name still reaches a log line and a header.
        var support = Record("""{"thinking":{"types":{"ad\r\nap tive":{"supported":false}}}}""");

        var strip = MessagesFeatureStripper.Plan(body: Parse("""{"thinking":{"type":"ad\r\nap tive"}}"""),
            support: support);

        Assert.Equal(expected: ["thinking.ad__ap_tive"], actual: strip.Features);
    }

    [Fact]
    public void HeaderValue_JoinsTheFeatureNames()
    {
        PlanAndApply(ExploreBody, out var strip);

        Assert.Equal(
            expected:
            "thinking.adaptive, output_config.effort, context_management.clear_thinking_20251015, context_management",
            actual: strip.HeaderValue);
    }

    // ----- anthropic-beta filtering -----

    [Fact]
    public void FilterBetaHeader_DropsOnlyThePairedValues_AndKeepsOrder()
    {
        var filtered = MessagesFeatureStripper.FilterBetaHeader(headerValue: CapturedBetaHeader,
            prefixes: [MessagesFeatureStripper.EffortBetaPrefix, MessagesFeatureStripper.ContextManagementBetaPrefix]);

        Assert.Equal(
            expected: "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
                      "prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
                      "mid-conversation-tool-changes-2026-07-01",
            actual: filtered);
    }

    [Fact]
    public void FilterBetaHeader_MatchesAnyDatedValueByPrefix_IgnoringCaseAndSpaces()
    {
        var filtered = MessagesFeatureStripper.FilterBetaHeader(headerValue: " Effort-2027-01-01 , other-1",
            prefixes: [MessagesFeatureStripper.EffortBetaPrefix]);

        Assert.Equal(expected: "other-1", actual: filtered);
    }

    [Fact]
    public void FilterBetaHeader_ReturnsNullWhenNothingIsLeft()
    {
        Assert.Null(MessagesFeatureStripper.FilterBetaHeader(headerValue: "effort-2025-11-24",
            prefixes: [MessagesFeatureStripper.EffortBetaPrefix]));
    }
}
