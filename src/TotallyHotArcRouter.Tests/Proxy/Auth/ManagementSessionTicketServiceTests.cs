using TotallyHot.ArcRouter.Proxy.Auth;
using TotallyHot.ArcRouter.Tests.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth;

/// <summary>
/// Covers <see cref="ManagementSessionTicketService"/>: a freshly issued ticket validates, a tampered or
/// malformed one does not, and rotating the underlying token invalidates every ticket issued before the
/// rotation - the Phase P4 exit criterion "token rotation invalidates token-login sessions".
/// </summary>
public sealed class ManagementSessionTicketServiceTests
{
    [Fact]
    public void IssueTicket_ThenIsValid_RoundTrips()
    {
        var service = new ManagementSessionTicketService(new FakeManagementTokenProvider("token"));

        var ticket = service.IssueTicket();

        Assert.True(service.IsValid(ticket));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    [InlineData("onlyonepart")]
    public void IsValid_MalformedTicket_ReturnsFalse(string? ticket)
    {
        var service = new ManagementSessionTicketService(new FakeManagementTokenProvider("token"));

        Assert.False(service.IsValid(ticket));
    }

    [Fact]
    public void IsValid_TamperedSignature_ReturnsFalse()
    {
        var service = new ManagementSessionTicketService(new FakeManagementTokenProvider("token"));
        var ticket = service.IssueTicket();
        var separator = ticket.IndexOf('.');
        var tampered = ticket[..separator] + ".not-the-real-signature";

        Assert.False(service.IsValid(tampered));
    }

    [Fact]
    public void IsValid_TicketFromDifferentServiceInstance_ReturnsFalse()
    {
        // Each instance mints its own random in-memory HMAC key - a ticket signed by one process cannot
        // be replayed against another, matching "a restart means silent re-issue on loopback".
        var tokenProvider = new FakeManagementTokenProvider("token");
        var issuer = new ManagementSessionTicketService(tokenProvider);
        var verifier = new ManagementSessionTicketService(tokenProvider);

        var ticket = issuer.IssueTicket();

        Assert.False(verifier.IsValid(ticket));
    }

    [Fact]
    public void IsValid_AfterTokenRotation_ReturnsFalse()
    {
        var tokenProvider = new FakeManagementTokenProvider("token");
        var service = new ManagementSessionTicketService(tokenProvider);
        var ticket = service.IssueTicket();

        tokenProvider.Regenerate();

        Assert.False(service.IsValid(ticket));
    }

    [Fact]
    public void IssueTicket_AfterRotation_IsValidAgain()
    {
        var tokenProvider = new FakeManagementTokenProvider("token");
        var service = new ManagementSessionTicketService(tokenProvider);
        tokenProvider.Regenerate();

        var ticket = service.IssueTicket();

        Assert.True(service.IsValid(ticket));
    }
}
