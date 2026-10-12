namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// One turn loaded from a schema-v1 conversation export zip. The reader yields these so a test can
/// feed request/response bytes into an existing parser or translator without talking to the store.
/// </summary>
/// <param name="ArchiveTurnId">The turn's stable id from <c>turns.jsonl</c>.</param>
/// <param name="ArchiveSessionId">The session's stable id from <c>turns.jsonl</c>.</param>
/// <param name="ClientSessionId">The client's session id, metadata only.</param>
/// <param name="TurnSequence">The turn's position in its source session.</param>
/// <param name="CreatedAtUtc">The turn's original timestamp.</param>
/// <param name="MetadataJson">The nested <c>metadata</c> object as JSON text, or <see langword="null"/>.</param>
/// <param name="ClientRequest">The client request bytes, or <see langword="null"/> when missing.</param>
/// <param name="ClientResponse">The client response bytes, or <see langword="null"/> when missing.</param>
/// <param name="ProviderRequest">The provider request bytes when the turn was translated; otherwise <see langword="null"/>.</param>
/// <param name="ProviderResponse">The provider response bytes when the turn was translated; otherwise <see langword="null"/>.</param>
internal sealed record ConversationZipTurn(
    Guid ArchiveTurnId,
    Guid ArchiveSessionId,
    string ClientSessionId,
    int TurnSequence,
    DateTimeOffset CreatedAtUtc,
    string? MetadataJson,
    byte[]? ClientRequest,
    byte[]? ClientResponse,
    byte[]? ProviderRequest,
    byte[]? ProviderResponse);
