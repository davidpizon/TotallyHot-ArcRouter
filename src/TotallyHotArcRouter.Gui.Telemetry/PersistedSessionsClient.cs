using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a persisted-sessions read call fails. Carries a message fit to render in the Sessions tab
/// rather than a raw <see cref="RpcException"/>, mirroring <see cref="RoutingModeAdminException"/>.
/// </summary>
public sealed class PersistedSessionsClientException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PersistedSessionsClientException"/> class.</summary>
    public PersistedSessionsClientException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>Gets whether the call failed because the router could not be reached.</summary>
    public bool IsUnavailable { get; }
}

/// <summary>
/// The result of a <see cref="IPersistedSessionsClient.ListAsync"/> call.
/// </summary>
/// <param name="TranscriptCaptureEnabled">
/// Whether transcript capture is currently enabled on the router. <see langword="false"/> means
/// <paramref name="Transcripts"/> is empty because capture is off, not because no traffic has been
/// persisted yet - the Sessions tab renders these two states differently.
/// </param>
/// <param name="Transcripts">The most recent persisted transcript rows, as returned by the router.</param>
public sealed record PersistedSessionsResult(
    bool TranscriptCaptureEnabled,
    IReadOnlyList<PersistedTranscriptDto> Transcripts);

/// <summary>Reads persisted session history from the router's <c>TelemetryService.ListPersistedSessions</c> RPC.</summary>
public interface IPersistedSessionsClient
{
    /// <summary>Loads the most recent persisted transcript rows, up to <paramref name="limit"/>.</summary>
    /// <param name="limit">The maximum number of rows to request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>TelemetryService.ListPersistedSessions</c> RPC - the GUI Sessions tab's
/// persisted-history read (docs/router/sessions-tab-training-data-plan.md Phase 2). Lives in this plain
/// <c>net10.0</c> library rather than the Windows-only MAUI project so CI can unit-test it, exactly like
/// <see cref="RoutingModeAdminClient"/>.
/// </summary>
public sealed class PersistedSessionsClient : IPersistedSessionsClient, IDisposable
{
    private readonly Contract.TelemetryService.TelemetryServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionsClient"/> class, creating and owning
    /// a channel to <paramref name="serverAddress"/>.
    /// </summary>
    public PersistedSessionsClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.TelemetryService.TelemetryServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistedSessionsClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public PersistedSessionsClient(Contract.TelemetryService.TelemetryServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .ListPersistedSessionsAsync(new Contract.ListPersistedSessionsRequest { Limit = limit }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new PersistedSessionsResult(
                response.TranscriptCaptureEnabled,
                [.. response.Transcripts.Select(ToDto)]);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>Converts a gRPC-contract <see cref="Contract.PersistedTranscript"/> into the plain <see cref="PersistedTranscriptDto"/>.</summary>
    private static PersistedTranscriptDto ToDto(Contract.PersistedTranscript t) => new(
        SessionId: t.SessionId,
        CorrelationId: t.CorrelationId,
        CreatedAtUtc: t.CreatedAtUtc.ToDateTimeOffset(),
        RequestedModel: t.RequestedModel,
        RoutedModel: t.RoutedModel,
        PromptText: t.HasPromptText ? t.PromptText : null,
        ResponseText: t.HasResponseText ? t.ResponseText : null,
        // Decimal-as-string, not double: see "Decimal encoding" in docs/router/grpc-migration.md.
        CostUsd: t.HasCostUsd ? decimal.Parse(t.CostUsd, System.Globalization.CultureInfo.InvariantCulture) : null,
        InputTokens: t.HasInputTokens ? t.InputTokens : null,
        OutputTokens: t.HasOutputTokens ? t.OutputTokens : null,
        MemoryEntryId: t.HasMemoryEntryId ? t.MemoryEntryId : null);

    // Unavailable means the proxy isn't running, which is an ordinary state for a GUI that can outlive it -
    // so it gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can
    // tell a dead connection from a rejected request without parsing the text. Mirrors RoutingModeAdminClient.Wrap.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="PersistedSessionsClientException"/>, flagging router-unreachable errors distinctly.</summary>
    private static PersistedSessionsClientException Wrap(RpcException ex) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new PersistedSessionsClientException("Could not read persisted sessions: the router is not reachable.", ex, isUnavailable: true)
            : new PersistedSessionsClientException($"Could not read persisted sessions: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
