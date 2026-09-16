using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// Issues and validates the signed session ticket carried in the web port's <c>__Host-</c> session
/// cookie (ADR-0012). The signing key is generated fresh in memory for each
/// <see cref="ManagementSessionTicketService"/> instance - one per inner-host process lifetime, never
/// persisted - so a router restart silently invalidates every outstanding cookie; per the migration
/// plan's Phase P4 deliverables, that is the intended behavior ("a restart means silent re-issue on
/// loopback"), not a bug to work around.
/// </summary>
/// <remarks>
/// A ticket embeds <see cref="IManagementTokenProvider.Generation"/> at issuance time, so
/// <see cref="ManagementTokenProvider.Regenerate"/> invalidates every outstanding ticket - loopback and
/// token-login alike - without this service tracking a session table. This satisfies the Phase P4 exit
/// criterion "token rotation invalidates token-login sessions" with the simplest mechanism available;
/// loopback sessions are invalidated too, which is a harmless superset since a loopback caller silently
/// re-issues on its next <see cref="ManagementAuthEndpoints"/> call.
/// </remarks>
public sealed class ManagementSessionTicketService
{
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly IManagementTokenProvider _tokenProvider;

    /// <summary>Initializes a new instance of the <see cref="ManagementSessionTicketService"/> class.</summary>
    /// <param name="tokenProvider">Supplies the rotation generation a ticket is checked against.</param>
    public ManagementSessionTicketService(IManagementTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Mints a new ticket for the current token generation, formatted as
    /// <c>base64url(generation).base64url(HMAC-SHA256)</c>.
    /// </summary>
    public string IssueTicket()
    {
        var payload = Encode(_tokenProvider.Generation);
        var signature = Sign(payload);
        return $"{payload}.{signature}";
    }

    /// <summary>
    /// Verifies <paramref name="ticket"/>'s signature and that it was issued under the current token
    /// generation.
    /// </summary>
    public bool IsValid(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return false;

        var separator = ticket.IndexOf('.');
        if (separator < 0 || separator == ticket.Length - 1) return false;

        var payload = ticket[..separator];
        var presentedSignature = ticket[(separator + 1)..];
        var expectedSignature = Sign(payload);

        var presentedBytes = Encoding.ASCII.GetBytes(presentedSignature);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSignature);
        if (presentedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(left: presentedBytes, right: expectedBytes))
            return false;

        return TryDecode(payload, out var generation) && generation == _tokenProvider.Generation;
    }

    private string Sign(string payload)
    {
        var signature = HMACSHA256.HashData(key: _hmacKey, source: Encoding.ASCII.GetBytes(payload));
        return Convert.ToBase64String(signature).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Encode(int generation)
    {
        return Convert.ToBase64String(BitConverter.GetBytes(generation))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool TryDecode(string payload, out int generation)
    {
        generation = 0;
        try
        {
            var padded = payload.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != sizeof(int)) return false;

            generation = BitConverter.ToInt32(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
