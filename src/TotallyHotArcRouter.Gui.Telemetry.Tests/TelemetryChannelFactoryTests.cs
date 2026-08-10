using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TotallyHot.ArcRouter.Gui.Telemetry;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="TelemetryChannelFactory"/>, and in particular for what it decides to trust.
/// </summary>
/// <remarks>
/// This is the one place the GUI accepts a certificate that normal chain validation would reject, so the
/// rules it relies on - exact subject match, loopback-only host - are pinned here rather than left to
/// inspection. It lives in this plain net10.0 test project (not the Windows-only bUnit one) so CI actually
/// runs it.
/// </remarks>
public class TelemetryChannelFactoryTests
{
    [Fact]
    public void Rejects_a_null_certificate()
    {
        Validate(certificate: null, requestUri: "https://localhost:5002").Should().BeFalse();
    }

    [Fact]
    public void Accepts_the_proxys_self_signed_loopback_certificate()
    {
        using var certificate = SelfSigned("CN=localhost");

        // Chain-trust errors are expected and normal here: it is self-signed, and no CA issues certificates
        // for a loopback address.
        Validate(certificate, "https://localhost:5002", SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("https://127.0.0.1:5002")]
    [InlineData("https://localhost:5002")]
    public void Accepts_loopback_hosts(string requestUri)
    {
        using var certificate = SelfSigned("CN=localhost");

        Validate(certificate, requestUri).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_subject_that_merely_starts_with_localhost()
    {
        // The reason the check is an exact match rather than a StartsWith/Contains: a certificate for
        // "localhost.evil.com" must not be accepted just because its subject begins with the right word.
        using var certificate = SelfSigned("CN=localhost.evil");

        Validate(certificate, "https://localhost:5002").Should().BeFalse();
    }

    [Theory]
    [InlineData("CN=example.com")]
    [InlineData("CN=LOCALHOST")]
    public void Rejects_any_other_subject(string subject)
    {
        using var certificate = SelfSigned(subject);

        Validate(certificate, "https://localhost:5002").Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_valid_certificate_presented_off_loopback()
    {
        // The trust boundary is "same machine, same OS user". A CN=localhost certificate offered by a remote
        // host is not the proxy, whatever it claims - so the host check has to gate the subject check.
        using var certificate = SelfSigned("CN=localhost");

        Validate(certificate, "https://telemetry.example.com:5002").Should().BeFalse();
    }

    [Fact]
    public void Create_builds_a_channel_for_the_default_loopback_address()
    {
        using var channel = TelemetryChannelFactory.Create();

        channel.Target.Should().Contain("localhost");
    }

    [Fact]
    public void Create_honors_an_explicit_address()
    {
        using var channel = TelemetryChannelFactory.Create("https://127.0.0.1:6001");

        channel.Target.Should().Contain("127.0.0.1:6001");
    }

    [Fact]
    public void Authenticated_wraps_the_channel_in_a_call_invoker()
    {
        using var channel = TelemetryChannelFactory.Create();

        var invoker = TelemetryChannelFactory.Authenticated(channel);

        invoker.Should().NotBeNull();
    }

    [Fact]
    public void DefaultServerAddress_is_the_tls_grpc_port()
    {
        // Pinned because the proxy binds 5002 for gRPC over TLS while 5001 stays plain HTTP for LLM
        // forwarding; pointing this at the wrong one fails in a way that looks like the proxy being down.
        TelemetryChannelFactory.DefaultServerAddress.Should().Be("https://localhost:5002");
    }

    private static bool Validate(
        X509Certificate2? certificate,
        string requestUri,
        SslPolicyErrors errors = SslPolicyErrors.None) =>
        TelemetryChannelFactory.ValidateLoopbackCertificate(
            new HttpRequestMessage(HttpMethod.Post, requestUri),
            certificate,
            chain: null,
            errors);

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}

