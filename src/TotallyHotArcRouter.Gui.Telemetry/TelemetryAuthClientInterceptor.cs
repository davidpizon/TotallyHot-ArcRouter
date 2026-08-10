using Grpc.Core;
using Grpc.Core.Interceptors;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Attaches the shared per-user management token to every call's <c>x-admin-token</c> metadata entry,
/// mirroring <c>TotallyHot.ArcRouter.Telemetry.TelemetryAuthInterceptor</c> server-side. Reads the token
/// from the same file the proxy's <c>ManagementAccessToken</c> writes
/// (<c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>); the file path and read logic are
/// duplicated here rather than referenced, the same "one fact, two copies because of the process
/// boundary" tradeoff <see cref="TelemetryChannelFactory.ValidateLoopbackCertificate"/> and
/// <c>TotallyHot.ArcRouter.Gui.Admin.ManagementTokenReader</c> already accept.
/// </summary>
public sealed class TelemetryAuthClientInterceptor : Interceptor
{
    private const string TokenHeaderName = "x-admin-token";
    private const string TokenFileName = "management-token.txt";

    private readonly string? _explicitToken;
    private readonly string? _tokenPath;

    /// <summary>Initializes a new instance of the <see cref="TelemetryAuthClientInterceptor"/> class.</summary>
    /// <param name="token">
    /// The token to attach, or <see langword="null"/> to read it from the per-user token file on every
    /// call (so a token created after this interceptor was constructed - e.g. the GUI starting before
    /// the proxy on first run - still gets picked up). Only meant to be overridden by tests; production
    /// callers omit it.
    /// </param>
    /// <param name="tokenPath">The token file path override; only meant for tests. Production callers omit it.</param>
    public TelemetryAuthClientInterceptor(string? token = null, string? tokenPath = null)
    {
        _explicitToken = token;
        _tokenPath = tokenPath;
    }

    /// <inheritdoc/>
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithToken(context));

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithToken(context));

    /// <inheritdoc/>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithToken(context));

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithToken(context));

    /// <inheritdoc/>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithToken(context));

    /// <summary>
    /// Returns <paramref name="context"/> unchanged when no token is available (the call proceeds and the
    /// server rejects it, exactly as a REST admin call with no token file yet would), otherwise returns a
    /// copy with the token attached as the <c>x-admin-token</c> metadata entry. Resolves the token fresh
    /// on every call (unless an explicit token was supplied) rather than caching it at construction time,
    /// so a token file created after this interceptor was built - e.g. the GUI starting before the proxy
    /// on first run - is picked up as soon as it exists.
    /// </summary>
    private ClientInterceptorContext<TRequest, TResponse> WithToken<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var token = _explicitToken ?? TryReadToken(_tokenPath);
        if (string.IsNullOrEmpty(token))
        {
            return context;
        }

        var headers = new Metadata();
        if (context.Options.Headers is { } existing)
        {
            foreach (var entry in existing)
            {
                if (!string.Equals(entry.Key, TokenHeaderName, StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add(entry);
                }
            }
        }

        headers.Add(TokenHeaderName, token);
        var options = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
    }

    /// <summary>
    /// Reads the token from <paramref name="path"/> (or, when <see langword="null"/>, the default
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>), or <see langword="null"/> when the
    /// file doesn't exist yet, is empty/whitespace, or can't be read.
    /// </summary>
    private static string? TryReadToken(string? path)
    {
        try
        {
            var tokenPath = path ?? DefaultTokenPath();

            if (!File.Exists(tokenPath))
            {
                return null;
            }

            var token = File.ReadAllText(tokenPath).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Gets the default token file path: <c>%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt</c>.</summary>
    private static string DefaultTokenPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TotallyHotArcRouter",
            TokenFileName);
}
