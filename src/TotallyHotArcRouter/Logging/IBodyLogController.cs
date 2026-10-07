namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Owns the rolling <c>bodies-*.log</c> Serilog sink so Clear can flush it, delete every body file, and
/// reopen it without racing an open writer (#184 plan §3.3). Diagnostic sinks never receive marked
/// events, so Clear does not touch <c>arcrouter-*.log</c>.
/// </summary>
public interface IBodyLogController
{
    /// <summary>
    /// Closes the body sink (flushing and releasing its file), deletes every <c>bodies*.log</c> under
    /// the logs directory, then reopens the sink when body excerpts are currently enabled. Safe to call
    /// when the sink was never opened or when body logging is off.
    /// </summary>
    void ClearBodyFiles();
}
