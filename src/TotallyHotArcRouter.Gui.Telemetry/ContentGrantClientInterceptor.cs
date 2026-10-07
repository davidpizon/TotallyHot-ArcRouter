using Grpc.Core;
using Grpc.Core.Interceptors;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Attaches the dashboard's in-memory content grant to every outgoing call's <c>x-content-grant</c> metadata
/// entry (ADR-0020), so the router lets conversation text through on <c>ListPersistedSessions</c> and
/// <c>StreamEvents</c> and reports an active grant from <c>GetPasskeyGateStatus</c>. The grant is read from
/// a delegate on every call rather than captured at construction, because it is minted after the channel is
/// built and disappears again on Lock or expiry.
/// </summary>
/// <remarks>
/// The grant is a bearer token, so it is only ever put in gRPC metadata and never in a cookie or
/// browser storage. When the delegate returns <see langword="null"/> the call passes through untouched, and
/// any stale <c>x-content-grant</c> entry a caller put on the options is removed rather than forwarded.
/// A server-streaming call such as <c>StreamEvents</c> carries the header it was opened with for its whole
/// life, so a stream must be reopened to pick up a newly minted grant.
/// </remarks>
public sealed class ContentGrantClientInterceptor : Interceptor
{
    /// <summary>The gRPC metadata key the router reads the grant from.</summary>
    public const string HeaderName = "x-content-grant";

    private readonly Func<string?> _grantAccessor;

    /// <summary>Initializes a new instance of the <see cref="ContentGrantClientInterceptor"/> class.</summary>
    /// <param name="grantAccessor">
    /// Returns the grant token to attach, or <see langword="null"/> when the content is locked. Invoked once
    /// per call, on whichever thread starts the call, so it must be cheap and thread-safe.
    /// </param>
    public ContentGrantClientInterceptor(Func<string?> grantAccessor)
    {
        ArgumentNullException.ThrowIfNull(grantAccessor);
        _grantAccessor = grantAccessor;
    }

    /// <inheritdoc/>
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request: request, context: WithGrant(context));
    }

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request: request, context: WithGrant(context));
    }

    /// <inheritdoc/>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request: request, context: WithGrant(context));
    }

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(WithGrant(context));
    }

    /// <inheritdoc/>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(WithGrant(context));
    }

    /// <summary>
    /// Returns <paramref name="context"/> with the current grant attached as the <c>x-content-grant</c>
    /// entry, replacing any existing one; with no grant held, returns it with any such entry removed.
    /// </summary>
    private ClientInterceptorContext<TRequest, TResponse> WithGrant<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var grant = _grantAccessor();
        var existing = context.Options.Headers;
        var hadStale = existing is not null && existing.Any(IsGrantEntry);
        if (string.IsNullOrEmpty(grant) && !hadStale) return context;

        var headers = new Metadata();
        if (existing is not null)
            foreach (var entry in existing)
                if (!IsGrantEntry(entry))
                    headers.Add(entry);

        if (!string.IsNullOrEmpty(grant)) headers.Add(key: HeaderName, value: grant);

        return new ClientInterceptorContext<TRequest, TResponse>(method: context.Method, host: context.Host,
            options: context.Options.WithHeaders(headers));
    }

    /// <summary>Whether <paramref name="entry"/> is an <c>x-content-grant</c> metadata entry.</summary>
    private static bool IsGrantEntry(Metadata.Entry entry)
    {
        return string.Equals(a: entry.Key, b: HeaderName, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
