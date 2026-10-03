namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// How a request carrying a <see cref="SubagentSignal"/> is routed (issue #163;
/// <c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>). The class follows what the
/// harness vendors themselves do with the same traffic: helpers on cheap models, a few narrow subagents on
/// cheaper models only when those are nearly as good, and every other subagent on the main model.
/// </summary>
public enum SubagentRouteClass
{
    /// <summary>
    /// Routed exactly as the same request would be without a signal; the signal is only recorded for the
    /// log line and telemetry. Every subagent that is not a light subagent, including all Codex subagents.
    /// </summary>
    Normal,

    /// <summary>
    /// A narrow, read-only subagent (Claude Code <c>Explore</c> and <c>claude-code-guide</c>): routed by the
    /// utility rule, restricted to candidates whose known score is close to the best known score
    /// (<see cref="Models.SubagentBiasOptions.LightSubagentRelativeFloor"/>), and routed normally when no such
    /// candidate is priced.
    /// </summary>
    LightSubagent,

    /// <summary>
    /// A helper task (Claude Code <c>auxiliary</c> requests, Copilot <c>copilot-utility*</c> aliases): routed as
    /// utility traffic by <see cref="UtilityRoutingPolicy"/>.
    /// </summary>
    Helper
}
