using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Builds gRPC channels to the proxy's loopback TLS endpoint, and defines what "trusted" means for the
/// self-signed certificate it presents.
/// </summary>
/// <remarks>
/// One factory rather than per-client channel setup so the trust decision below exists exactly once. Every
/// consumer on this endpoint (the <c>TelemetryService.StreamEvents</c> event stream,
/// <see cref="PriceSourceAdminClient"/>'s management calls) is talking to the same process over the same
/// certificate; a second, subtly different copy of the validation callback would be a security bug waiting
/// to drift.
/// </remarks>
public static class TelemetryChannelFactory
{
    /// <summary>
    /// The proxy's TLS gRPC endpoint. HTTPS/2 via ALPN rather than unencrypted h2c: h2c proved unreliable on
    /// at least one managed/corporate Windows machine, where every connection failed with the HTTP/2-level
    /// <c>HTTP_1_1_REQUIRED</c> error - consistent with something on the network path (VPN client, endpoint
    /// security agent, TLS-inspecting proxy) mangling the h2c preface even on loopback. See
    /// docs/router/grpc-migration.md's "Transport" section. <c>5004</c>, not the former dedicated <c>5002</c>
    /// gRPC port Phase P9 retired as fully redundant once every gRPC admin service was dual-mapped onto the
    /// web port.
    /// </summary>
    public const string DefaultServerAddress = "https://localhost:5004";

    /// <summary>
    /// Creates a channel to <paramref name="serverAddress"/> that trusts the proxy's self-signed loopback
    /// certificate. The caller owns disposal.
    /// </summary>
    public static GrpcChannel Create(string serverAddress = DefaultServerAddress)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateLoopbackCertificate
        };

        // DisposeHttpClient: GrpcChannel doesn't own a caller-supplied HttpHandler by default, so without
        // this the handler would outlive the channel's disposal.
        return GrpcChannel.ForAddress(
            address: serverAddress,
            channelOptions: new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    /// <summary>
    /// Creates a channel to <paramref name="serverAddress"/> authenticated by ADR-0012's loopback session
    /// cookie rather than <see cref="Authenticated"/>'s shared <c>x-admin-token</c>: issues itself a
    /// session via <c>POST {serverAddress}/auth/session</c> (the loopback fast path - no credential is
    /// presented, only the caller's loopback remote address matters), then reuses the very
    /// <see cref="HttpClientHandler"/> that request went through - with its now-populated
    /// <see cref="CookieContainer"/> - as the channel's own transport. No client-side gRPC interceptor is
    /// needed: <c>TelemetryAuthInterceptor</c>'s server-side fallback reads the session cookie straight off
    /// <c>ServerCallContext.GetHttpContext().Request.Cookies</c>, and a plain <see cref="CookieContainer"/>
    /// replays an <c>HttpOnly</c>/<c>Secure</c>/<c>SameSite=Strict</c> cookie on every subsequent request to
    /// the same origin with no special handling - those attributes are all browser-enforced, not
    /// <see cref="CookieContainer"/>-enforced. The returned channel's session is only as durable as the
    /// server process: a router restart invalidates every outstanding ticket (ADR-0012's own tradeoff, "a
    /// restart means silent re-issue on loopback"), so a long-lived caller across a router restart must call
    /// this again for a fresh channel rather than assume the old one keeps working.
    /// </summary>
    /// <param name="serverAddress">The proxy's TLS endpoint to authenticate against and connect to.</param>
    /// <param name="cancellationToken">Cancels the session-issuance request only; the returned channel is unaffected once issued.</param>
    /// <exception cref="HttpRequestException">The session request failed (the router is unreachable, or returned a non-success status).</exception>
    public static async Task<GrpcChannel> CreateSessionAuthenticatedAsync(
        string serverAddress = DefaultServerAddress, CancellationToken cancellationToken = default)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateLoopbackCertificate,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        try
        {
            // disposeHandler: false - GrpcChannelOptions.DisposeHttpClient below takes over ownership of
            // the same handler once the channel is built; this HttpClient's own disposal must not tear it
            // down first.
            using var sessionClient = new HttpClient(handler, disposeHandler: false);
            using var response = await sessionClient
                .PostAsync(requestUri: $"{serverAddress}/auth/session", content: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            handler.Dispose();
            throw;
        }

        return GrpcChannel.ForAddress(
            address: serverAddress,
            channelOptions: new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    /// <summary>
    /// Wraps <paramref name="channel"/> with <see cref="TelemetryAuthClientInterceptor"/> so every call
    /// made through the returned <see cref="CallInvoker"/> presents the shared management token. Callers
    /// still own and dispose <paramref name="channel"/> itself - intercepting doesn't change ownership.
    /// </summary>
    public static CallInvoker Authenticated(GrpcChannel channel)
    {
        return channel.Intercept(new TelemetryAuthClientInterceptor());
    }

    /// <summary>
    /// Trusts a certificate only if its subject is exactly <c>CN=localhost</c> and the request targets a
    /// loopback host. Chain-trust errors are ignored, which is expected and normal for a self-signed
    /// certificate with no CA behind it.
    /// </summary>
    /// <remarks>
    /// The trust boundary is "same machine, same OS user": the proxy persists this certificate under
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\</c> (see <c>TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate</c>), and
    /// there is no CA that would issue a real certificate for a loopback address. Not a thumbprint pin - a
    /// future hardening could read that same <c>.pfx</c> and pin its exact public certificate.
    /// </remarks>
    public static bool ValidateLoopbackCertificate(
        HttpRequestMessage requestMessage,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null) return false;

        // Exact match, not a substring: "CN=localhost.evil" must not pass.
        if (certificate.Subject != "CN=localhost") return false;

        // The request's host must match the certificate's subject, so a local process cannot impersonate the
        // telemetry server by presenting a CN=localhost certificate on some other address.
        var targetHost = requestMessage.RequestUri?.Host;
        return targetHost is "localhost" or "127.0.0.1" or "::1";
    }
}