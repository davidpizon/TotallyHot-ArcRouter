namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// A verified marker that a request comes from a subagent or a narrow side task rather than the main agent
/// (issue #163). Every member is drawn from a fixed vocabulary inside <see cref="SubagentSignalDetector"/> -
/// never copied from a client-controlled value - so it is safe to log and to put on a telemetry event.
/// </summary>
/// <param name="Harness">The harness that sent the marker (<c>claude-code</c>, <c>codex</c>, or <c>copilot</c>).</param>
/// <param name="Kind">
/// What the harness says the request is, for example <c>subagent</c>, <c>auxiliary</c>,
/// <c>thread_spawn</c>, <c>memory_consolidation</c>, or <c>utility-alias</c>.
/// </param>
/// <param name="Source">
/// Where the marker was read from: a header name or <c>model</c>. Lets an operator see which toggle in
/// <see cref="Models.SubagentBiasOptions"/> produced the bias.
/// </param>
public sealed record SubagentSignal(string Harness, string Kind, string Source)
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
