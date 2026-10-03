namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// A verified marker that a request comes from a subagent or a helper task rather than the main agent
/// (issue #163). Every member is drawn from a fixed vocabulary inside <see cref="SubagentSignalDetector"/> -
/// never copied from a client-controlled value - so it is safe to log and to put on a telemetry event.
/// </summary>
/// <param name="Harness">The harness that sent the marker (<c>claude-code</c>, <c>codex</c>, or <c>copilot</c>).</param>
/// <param name="Kind">
/// What the harness says the request is: a Claude Code agent type (<c>explore</c>, <c>plan</c>,
/// <c>general-purpose</c>, ...), <c>subagent</c> when the type is unknown or not sent, <c>auxiliary</c>,
/// a Codex <c>thread_spawn</c> or <c>memory_consolidation</c>, or Copilot's <c>utility-alias</c>.
/// </param>
/// <param name="Source">
/// Where the marker was read from: a header name or <c>model</c>. Lets an operator see which toggle in
/// <see cref="Models.SubagentBiasOptions"/> produced the signal.
/// </param>
/// <param name="RouteClass">
/// How this kind of request should be routed when the client delegated the model choice
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c>). The caller still applies the
/// delegation rule before acting on it.
/// </param>
public sealed record SubagentSignal(string Harness, string Kind, string Source, SubagentRouteClass RouteClass)
{
    /// <summary>
    /// Renders the signal as one compact, log-safe token (<c>harness/kind</c>), the form the routing log line
    /// and telemetry carry.
    /// </summary>
    /// <returns>The <c>harness/kind</c> label.</returns>
    public string ToLabel()
    {
        return $"{Harness}/{Kind}";
    }
}
