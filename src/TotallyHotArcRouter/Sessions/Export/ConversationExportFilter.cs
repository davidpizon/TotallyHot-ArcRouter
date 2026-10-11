namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>
/// Selects which turns a conversation export includes. Every field is optional and all that are set must
/// match, so an empty filter exports every session still in the store. The filter is recorded in the zip's
/// manifest, so a later import knows the file is not "the whole router".
/// </summary>
/// <param name="From">The earliest turn time to include (UTC, inclusive), or <see langword="null"/> for no lower bound.</param>
/// <param name="To">The latest turn time to include (UTC, inclusive), or <see langword="null"/> for no upper bound.</param>
/// <param name="SessionId">An archive session id or a client session id; either selects the sessions that carry it.</param>
/// <param name="Harness">The normalized harness token a turn must have, compared without regard to case.</param>
/// <param name="Provider">The provider a turn must have been routed to, compared without regard to case.</param>
/// <param name="Model">A model a turn must have requested, been routed to, or resolved to, compared without regard to case.</param>
public sealed record ConversationExportFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? SessionId = null,
    string? Harness = null,
    string? Provider = null,
    string? Model = null)
{
    /// <summary>
    /// Gets a value indicating whether the filter needs each turn's metadata snapshot, which the index does not
    /// carry, so the writer must read the small metadata frame of every candidate turn.
    /// </summary>
    public bool NeedsMetadata =>
        !string.IsNullOrWhiteSpace(Harness) || !string.IsNullOrWhiteSpace(Provider) || !string.IsNullOrWhiteSpace(Model);
}
