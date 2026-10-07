namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Opt-in switch for writing conversation-bearing body excerpts to their own rolling log files
/// (<c>bodies-*.log</c>), separate from the diagnostic <c>arcrouter-*.log</c> stream. Bound from the
/// <c>Logging:BodyExcerpts</c> configuration section. Off by default so a shipped install never persists
/// prompt or response text in logs unless an operator turns it on (#184 phase 3 / plan §3.3).
/// </summary>
public sealed class BodyExcerptOptions
{
    /// <summary>Gets the configuration section name used for body-excerpt logging.</summary>
    public const string SectionName = "Logging:BodyExcerpts";

    /// <summary>
    /// Gets whether the four conversation-bearing interceptor templates write to <c>bodies-*.log</c>.
    /// Defaults to <see langword="false"/>. When off, those templates emit nothing; when on, they emit at
    /// Information with the <see cref="ConversationBodyLogging.PropertyName"/> marker so only the body
    /// sink receives them.
    /// </summary>
    public bool Enabled { get; init; }
}
