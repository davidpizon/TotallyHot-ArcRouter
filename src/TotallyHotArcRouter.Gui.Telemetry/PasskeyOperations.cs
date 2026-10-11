using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The operation names the router's passkey gate recognizes for one-operation authorizations (ADR-0020).
/// Duplicated from the router's <c>GatedOperation</c> constants because the GUI cannot reference the router
/// project - the same "one fact, two copies across a process boundary" tradeoff
/// <see cref="TelemetryChannelFactory.ValidateLoopbackCertificate"/> accepts. The values travel on the wire
/// in <c>BeginOneOperationRequest.operation</c> and must match the router's strings exactly.
/// </summary>
public static class PasskeyOperations
{
    /// <summary>Authorizes one <c>ManagementTokenAdminService.GetManagementToken</c> call.</summary>
    public const string GetManagementToken = "get_management_token";

    /// <summary>Authorizes one <c>ManagementTokenAdminService.RegenerateManagementToken</c> call.</summary>
    public const string RegenerateManagementToken = "regenerate_management_token";

    /// <summary>Authorizes one <c>ConversationAdminService.ExportConversations</c> call (#165).</summary>
    public const string Export = "export";

    /// <summary>
    /// The parameter string bound to the operations above. Neither takes parameters, so the router binds
    /// them with an empty string and the GUI must send exactly that.
    /// </summary>
    public const string NoParameters = "";

    /// <summary>
    /// The command-line flag an operator runs from an elevated shell to mint a single-use enrollment code,
    /// used as the fallback hint when the router's gate status does not supply one.
    /// </summary>
    public const string EnrollmentCommandFlag = "--mint-passkey-enrollment-code";

    /// <summary>
    /// The command-line flag prefix an operator runs from an elevated shell to remove a passkey (ADR-0020
    /// decision 4: removal is elevated-channel only, never a dashboard action).
    /// </summary>
    public const string RevokeCommandFlag = "--revoke-passkey=";

    /// <summary>The version tag that starts the canonical encoding <see cref="ExportParameters"/> hashes.</summary>
    private const string ExportParametersTag = "arcrouter-export-v1";

    /// <summary>
    /// Builds the parameters string the router binds an <see cref="Export"/> approval to: the lowercase-hex
    /// SHA-256 of a canonical encoding of the export's filter and destination. This is a copy of the router's
    /// <c>GatedOperation.ExportParameters</c> (the GUI cannot reference the router project), and the two must
    /// produce the same digest for the same export or the router refuses the approval; a golden-value test in
    /// each project pins them to one digest.
    /// </summary>
    /// <remarks>
    /// The hashed bytes are the ASCII tag <c>arcrouter-export-v1</c> followed by seven fields in this order:
    /// <c>from</c>, <c>to</c>, <c>session</c>, <c>harness</c>, <c>provider</c>, <c>model</c>, <c>destination</c>.
    /// Each field is its UTF-8 bytes preceded by their count as a 4-byte big-endian unsigned integer. A time bound
    /// is converted to UTC and written as <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> (invariant culture), or empty when
    /// absent. A <see langword="null"/>, empty or whitespace-only string is the empty field; any other string is
    /// written exactly as given, with no trimming and no case folding. The caller must send the router exactly the
    /// values it passed here.
    /// </remarks>
    /// <param name="filter">Which turns the export includes.</param>
    /// <param name="destinationPath">The zip's destination path, exactly as it will be sent.</param>
    /// <returns>The 64-character lowercase hex SHA-256 of the canonical encoding.</returns>
    public static string ExportParameters(ConversationExportFilterInfo filter, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(destinationPath);

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.ASCII.GetBytes(ExportParametersTag));
        WriteField(buffer, FormatBound(filter.From));
        WriteField(buffer, FormatBound(filter.To));
        WriteField(buffer, NormalizeText(filter.SessionId));
        WriteField(buffer, NormalizeText(filter.Harness));
        WriteField(buffer, NormalizeText(filter.Provider));
        WriteField(buffer, NormalizeText(filter.Model));
        WriteField(buffer, destinationPath);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Formats an optional time bound for <see cref="ExportParameters"/>: UTC, seven fractional digits, or empty.</summary>
    private static string FormatBound(DateTimeOffset? bound) =>
        bound is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Maps a null, empty or whitespace-only filter value to the empty field and leaves any other value as given.</summary>
    private static string NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    /// <summary>Appends a length-prefixed UTF-8 field to the canonical encoding.</summary>
    private static void WriteField(MemoryStream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }
}
