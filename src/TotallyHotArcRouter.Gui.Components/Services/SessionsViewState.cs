namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Which side of a turn is selected in the Sessions messages pane: the client request or the model
/// response. The Message tab keys its field set off this; the Routing tab ignores it and always shows
/// the whole turn.
/// </summary>
public enum MessageSide
{
    /// <summary>The request (client) row of a turn.</summary>
    Request,

    /// <summary>The response (routed model) row of a turn.</summary>
    Response
}

/// <summary>
/// Which details-pane tab is active in the Sessions three-pane layout (#176). Survives tab switches via
/// the <see cref="SessionsViewState"/> singleton.
/// </summary>
public enum SessionsDetailsTab
{
    /// <summary>Per-message metadata for the selected request or response row.</summary>
    Message,

    /// <summary>Routing decision, overhead, and step log for the selected turn.</summary>
    Routing,

    /// <summary>Session-level identity, totals, and models-used list.</summary>
    Session
}

/// <summary>
/// A selected message row in the Sessions tab: one side of one turn in one session (#176 §6.1).
/// </summary>
/// <param name="SessionId">The conversation/session id holding the turn.</param>
/// <param name="TurnNumber">The turn number within that session.</param>
/// <param name="Side">Whether the request or response row is selected.</param>
public sealed record MessageSelection(string SessionId, int TurnNumber, MessageSide Side);

/// <summary>
/// Sessions-tab UI state that must survive Dashboard's <c>@key="_activeTab"</c> teardown (#176 §6.5).
/// Divider widths live in <c>localStorage</c> via <c>split-pane.js</c>; the selected session id stays on
/// Dashboard so Cost Analytics can share it. Everything else that the operator would notice resetting
/// on a tab switch lives here as a singleton.
/// </summary>
public sealed class SessionsViewState
{
    /// <summary>
    /// Idle threshold for the rail status dot and Session tab status line: a live session whose newest
    /// turn is older than this is "live but idle" rather than "live and active" (#176 §4.2).
    /// </summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The message row currently driving the details pane, or <see langword="null"/> when the Messages
    /// pane should fall back to the newest response row (implicit selection).
    /// </summary>
    public MessageSelection? MessageSelection { get; set; }

    /// <summary>Which of the three details tabs is showing.</summary>
    public SessionsDetailsTab DetailsTab { get; set; } = SessionsDetailsTab.Message;

    /// <summary>The rail's session search box text.</summary>
    public string RailSearch { get; set; } = string.Empty;

    /// <summary>Whether the messages title bar's in-session search input is open.</summary>
    public bool InSessionSearchOpen { get; set; }

    /// <summary>The in-session filter text over loaded message bodies and model names.</summary>
    public string InSessionFilter { get; set; } = string.Empty;

    /// <summary>
    /// Clears message-scoped state when the operator picks a different session so the Message/Routing
    /// tabs do not keep showing a turn that is no longer in view.
    /// </summary>
    public void ClearMessageSelection()
    {
        MessageSelection = null;
    }
}
