namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Unit coverage for <see cref="ModelCapabilityBadges"/>: the three badges Governance &gt; Providers shows per model
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1), classified from the
/// model's capability record - using the Claude Haiku 4.5 record Anthropic reported on 2026-10-02.
/// </summary>
public sealed class ModelCapabilityBadgesTests
{
    private static CapabilityGroupAdminView Group(string name, bool? supported, params (string Name, bool Supported)[] options)
    {
        return new CapabilityGroupAdminView(Name: name, Supported: supported,
            Options: [.. options.Select(o => new CapabilityOptionAdminView(Name: o.Name, Supported: o.Supported))]);
    }

    private static ModelCapabilitiesAdminView Record(params CapabilityGroupAdminView[] groups)
    {
        return new ModelCapabilitiesAdminView(ScannedAtUtc: DateTimeOffset.Parse("2026-10-02T12:00:00Z"), Groups: groups);
    }

    private static ModelCapabilitiesAdminView Haiku45()
    {
        return Record(
            Group("batch", true),
            Group("context_management", true, ("clear_tool_uses_20250919", true), ("clear_thinking_20251015", true),
                ("compact_20260112", false)),
            Group("effort", false, ("low", false), ("medium", false), ("high", false), ("xhigh", false), ("max", false)),
            Group("thinking", true, ("enabled", true), ("adaptive", false)));
    }

    [Fact]
    public void NoRecord_ShowsNoBadges()
    {
        Assert.Empty(ModelCapabilityBadges.For(null));
    }

    [Fact]
    public void Haiku45_ShowsPartialThinkingNoEffortAndPartialContextManagement_InDisplayOrder()
    {
        var badges = ModelCapabilityBadges.For(Haiku45());

        Assert.Equal(expected: ["Thinking", "Effort", "Context mgmt"], actual: badges.Select(b => b.Label));
        Assert.Equal(
            expected: [ModelCapabilityBadgeState.Partial, ModelCapabilityBadgeState.Unsupported, ModelCapabilityBadgeState.Partial],
            actual: badges.Select(b => b.State));
    }

    [Fact]
    public void APartialBadgeTip_NamesWhatIsAndIsNotSupported_AndWhatTheRouterDoes()
    {
        var thinking = ModelCapabilityBadges.For(Haiku45())[0];

        Assert.Equal(
            expected: "Thinking: supported (enabled); not supported (adaptive). " +
                      "The router removes unsupported settings from requests it routes to this model.",
            actual: thinking.Tip);
    }

    [Fact]
    public void AFullySupportedGroup_IsSupported_WithItsOptionsListed()
    {
        var badge = Assert.Single(ModelCapabilityBadges.For(Record(Group("effort", true, ("low", true), ("high", true)))));

        Assert.Equal(expected: ModelCapabilityBadgeState.Supported, actual: badge.State);
        Assert.Equal(expected: "Effort: supported (low, high).", actual: badge.Tip);
    }

    [Fact]
    public void AGroupWithOnlyUnsupportedOptions_IsUnsupported_EvenWithoutAGroupFlag()
    {
        var badge = Assert.Single(ModelCapabilityBadges.For(Record(Group("thinking", null, ("adaptive", false)))));

        Assert.Equal(expected: ModelCapabilityBadgeState.Unsupported, actual: badge.State);
    }

    [Fact]
    public void AGroupTheRecordDoesNotDescribe_HasNoBadge()
    {
        var badges = ModelCapabilityBadges.For(Record(Group("thinking", null), Group("batch", true)));

        Assert.Empty(badges);
    }

    [Fact]
    public void GroupsOtherThanTheThreeStrippedFeatures_AreNotShown()
    {
        Assert.Empty(ModelCapabilityBadges.For(Record(Group("batch", true), Group("pdf_input", false))));
    }
}
