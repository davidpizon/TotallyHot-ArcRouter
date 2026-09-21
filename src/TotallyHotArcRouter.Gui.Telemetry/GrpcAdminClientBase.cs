using Grpc.Core;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Base for gRPC admin clients (<see cref="PriceSourceAdminClient"/>,
/// <see cref="ClusterModelAdminClient"/>, <see cref="BenchmarkDataAdminClient"/>, and the Gui.Admin
/// <c>ProviderAdminClient</c>/<c>UsageQueryClient</c> pair): owns the generated-client constructor, unary <c>CallAsync</c>,
/// and the "unavailable → friendly message, else → server detail" exception-wrapping rule every one of them used to reimplement identically.
/// Each concrete client keeps its own RPC calls and DTO mapping - only this scaffolding lives here.
/// </summary>
/// <typeparam name="TGeneratedClient">The generated gRPC client type this admin client wraps.</typeparam>
public abstract class GrpcAdminClientBase<TGeneratedClient> : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcAdminClientBase{TGeneratedClient}"/> class over a
    /// caller-supplied generated client - one built over the shared call invoker in production, or a fake in
    /// tests. The caller owns the channel's lifetime.
    /// </summary>
    protected GrpcAdminClientBase(TGeneratedClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
    }

    /// <summary>Gets the generated gRPC client this admin client wraps.</summary>
    protected TGeneratedClient Client { get; }

    /// <summary>
    /// Does nothing: every client is built over a caller-owned generated client or shared call invoker, so
    /// there is no channel here to close. Kept so the stores' <c>ownsClient</c> disposal and existing
    /// <c>using</c> call sites need no change.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Invokes a unary RPC through <see cref="Client"/> and wraps an <see cref="RpcException"/> via
    /// <see cref="Wrap(RpcException, string)"/>. Concrete clients with many RPCs (provider/usage admin)
    /// share this rather than repeating the try/catch at every call site; clients with a handful of
    /// methods keep the inline form, which is the same wrapping rule.
    /// </summary>
    /// <typeparam name="TResponse">The RPC's response type.</typeparam>
    /// <param name="call">Starts the unary call against the generated client.</param>
    /// <param name="action">Describes the failed operation, forwarded to <see cref="Wrap(RpcException, string)"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The RPC's response.</returns>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    protected async Task<TResponse> CallAsync<TResponse>(
        Func<TGeneratedClient, CancellationToken, AsyncUnaryCall<TResponse>> call,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call(Client, cancellationToken).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: action);
        }
    }

    /// <summary>
    /// Wraps <paramref name="ex"/> into a <see cref="GrpcAdminException"/>: a plain-language,
    /// <see cref="GrpcAdminException.IsUnavailable"/>-flagged message of the form
    /// <c>"{action}: the router is not reachable."</c> when the router isn't reachable (an ordinary state
    /// for a GUI that can outlive it), or <c>"{action}: {server detail}"</c> otherwise - so callers can
    /// tell a dead connection from a rejected request without parsing the text.
    /// </summary>
    /// <param name="ex">The failed call's exception.</param>
    /// <param name="action">Describes the failed operation, e.g. <c>"Could not read the price sources"</c>.</param>
    protected static GrpcAdminException Wrap(RpcException ex, string action)
    {
        return Wrap(ex: ex, unavailableMessage: $"{action}: the router is not reachable.", action: action);
    }

    /// <summary>
    /// Wraps <paramref name="ex"/> like <see cref="Wrap(RpcException, string)"/>, but with an explicit
    /// <paramref name="unavailableMessage"/> independent of <paramref name="action"/> - for a client whose
    /// unreachable-router message doesn't follow the common <c>"{action}: the router is not reachable."</c>
    /// shape (e.g. <c>RoutingGateAdminClient</c>, whose actions describe individual calls but whose
    /// unavailable message is action-agnostic).
    /// </summary>
    protected static GrpcAdminException Wrap(RpcException ex, string unavailableMessage, string action)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.StatusCode == StatusCode.Unavailable
            ? new GrpcAdminException(message: unavailableMessage, innerException: ex, isUnavailable: true)
            : new GrpcAdminException(message: $"{action}: {ex.Status.Detail}", innerException: ex);
    }
}
