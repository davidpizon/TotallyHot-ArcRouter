namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Public documentation URLs the dashboard cites. These open the GitHub-hosted docs, not a local
/// file, because the WASM dashboard has no docs tree of its own. Static so every surface that needs
/// a citation shares one address rather than restating a blob URL.
/// </summary>
public static class ProductDocs
{
    /// <summary>
    /// The as-built score-delta methodology: how live estimated regret and Cost Analytics Routing ROI
    /// are computed against the frozen untrained baseline, and how that relates to the offline
    /// Regret Harness. Cited from the Routing ROI chart and the Governance Regret Harness pane.
    /// </summary>
    public const string ScoreDeltaMethodologyUrl =
        "https://github.com/davidpizon/TotallyHot-ArcRouter/blob/main/docs/score-delta-methodology.md";
}
