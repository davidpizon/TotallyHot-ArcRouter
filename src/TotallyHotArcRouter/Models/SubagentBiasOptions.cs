namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Operator switches for subagent-aware routing (issue #163,
/// <c>docs/plans/issue-163-subagent-aware-routing.md</c>): when a harness marks a request as coming from a
/// subagent or a helper task, <see cref="Router.Classification.SubagentSignalDetector"/> reports a
/// <see cref="Router.Classification.SubagentSignal"/>, and its <see cref="Router.Classification.SubagentRouteClass"/>
/// decides how it is routed (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>). Bound from
/// <c>Routing:SubagentBias</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every flag defaults to <see langword="true"/> (David's decision, 2026-09-29): only signals the vendor
/// documents are implemented, and <see cref="Enabled"/> is the kill switch. A signal is a cost preference,
/// not an authorization boundary - a client can send any header, and the worst outcome of a forged one is a
/// cheaper pick from the operator's own allowlist.
/// </para>
/// <para>
/// The per-signal flags exist so an operator can drop a single marker (for example one whose harness version
/// misbehaves) without losing the others. They are ignored while <see cref="Enabled"/> is
/// <see langword="false"/>.
/// </para>
/// </remarks>
public sealed class SubagentBiasOptions
{
    /// <summary>
    /// Gets a value indicating whether subagent signals are detected at all. When <see langword="false"/> the
    /// detector reports nothing and routing behaves exactly as it did before #163.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether Claude Code's <c>x-claude-code-agent-id</c> header, sent only on
    /// requests from a subagent Claude Code spawned, counts as a signal. On its own it marks a subagent of unknown
    /// type, which routes normally.
    /// </summary>
    public bool ClaudeCodeAgentId { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether Claude Code's gateway hint headers count as signals:
    /// <c>x-claude-code-request-class</c> (<c>subagent</c>, or <c>auxiliary</c> for helper tasks) and
    /// <c>x-claude-code-agent-type</c> (which selects the light-subagent route for <c>Explore</c> and
    /// <c>claude-code-guide</c>). Claude Code only sends them to a custom base URL when
    /// <c>CLAUDE_CODE_GATEWAY_HINT_HEADERS=1</c> is set.
    /// </summary>
    public bool ClaudeCodeHintHeaders { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether Codex's <c>x-codex-turn-metadata</c> header, carrying
    /// <c>thread_source: "subagent"</c> with a <c>thread_spawn</c> or <c>memory_consolidation</c> kind, counts
    /// as a signal. Codex subagents route normally; the signal is recorded for visibility.
    /// </summary>
    public bool CodexTurnMetadata { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a request whose <c>model</c> starts with <c>copilot-utility</c> (the
    /// aliases VS Code uses for background work) counts as a helper signal. The names are unverified on the wire;
    /// see <c>docs/router/utility-model-routing.md</c>.
    /// </summary>
    public bool CopilotUtilityAlias { get; init; } = true;

    /// <summary>
    /// Gets the fraction of the best known candidate score a model must reach before a light subagent may be
    /// routed to it on value (<see cref="Router.UtilityRoutingPolicy.SelectNearBestValue"/>). 0.9 means "within 10%
    /// of the best known model in this category". A starting point, not a measured value: retune it once
    /// per-session savings data shows graded light-subagent traffic. Must be in (0, 1]; checked by
    /// <see cref="RoutingOptions.EnsureValid"/>.
    /// </summary>
    public double LightSubagentRelativeFloor { get; init; } = 0.9;
}
