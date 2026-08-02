using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;

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
    /// docs/router/grpc-migration.md's "Transport" section.
    /// </summary>
    public const string DefaultServerAddress = "https://localhost:5002";

    /// <summary>
    /// Creates a channel to <paramref name="serverAddress"/> that trusts the proxy's self-signed loopback
    /// certificate. The caller owns disposal.
    /// </summary>
    public static GrpcChannel Create(string serverAddress = DefaultServerAddress)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateLoopbackCertificate,
        };

        // DisposeHttpClient: GrpcChannel doesn't own a caller-supplied HttpHandler by default, so without
        // this the handler would outlive the channel's disposal.
        return GrpcChannel.ForAddress(
            serverAddress,
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    /// <summary>
    /// Trusts a certificate only if its subject is exactly <c>CN=localhost</c> and the request targets a
    /// loopback host. Chain-trust errors are ignored, which is expected and normal for a self-signed
    /// certificate with no CA behind it.
    /// </summary>
    /// <remarks>
    /// The trust boundary is "same machine, same OS user": the proxy persists this certificate under
    /// <c>%LOCALAPPDATA%\TotallyHot.ArcRouter\</c> (see <c>TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate</c>), and
    /// there is no CA that would issue a real certificate for a loopback address. Not a thumbprint pin - a
    /// future hardening could read that same <c>.pfx</c> and pin its exact public certificate.
    /// </remarks>
    public static bool ValidateLoopbackCertificate(
        HttpRequestMessage requestMessage,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            return false;
        }

        // Exact match, not a substring: "CN=localhost.evil" must not pass.
        if (certificate.Subject != "CN=localhost")
        {
            return false;
        }

        // The request's host must match the certificate's subject, so a local process cannot impersonate the
        // telemetry server by presenting a CN=localhost certificate on some other address.
        var targetHost = requestMessage.RequestUri?.Host;
        return targetHost is "localhost" or "127.0.0.1" or "::1";
    }
}

