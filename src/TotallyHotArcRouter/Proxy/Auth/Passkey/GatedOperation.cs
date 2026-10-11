using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Canonical operation names and parameter-binding helpers for one-operation passkey authorizations
/// (ADR-0020). Each gated RPC pairs an operation constant with a deterministic parameters string so an
/// approval for one action cannot be replayed against another.
/// </summary>
public static class GatedOperation
{
    /// <summary>The version tag that starts the canonical encoding <see cref="ExportParameters"/> hashes.</summary>
    private const string ExportParametersTag = "arcrouter-export-v1";

    /// <summary>Operation id for copying the management token through the admin API.</summary>
    public const string GetManagementToken = "get_management_token";

    /// <summary>Operation id for rotating the management token through the admin API.</summary>
    public const string RegenerateManagementToken = "regenerate_management_token";

    /// <summary>Operation id for unlocking conversation content (issues a content grant, not a one-op token).</summary>
    public const string ContentUnlock = "content_unlock";

    /// <summary>Operation id for exporting router state (#165).</summary>
    public const string Export = "export";

    /// <summary>Operation id for importing router state (#165).</summary>
    public const string Import = "import";

    /// <summary>
    /// Builds the parameters string for <see cref="GetManagementToken"/> — empty because the operation
    /// has no request-specific binding.
    /// </summary>
    public static string GetManagementTokenParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="RegenerateManagementToken"/> — empty because rotation
    /// has no request-specific binding beyond the operation name itself.
    /// </summary>
    public static string RegenerateManagementTokenParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="ContentUnlock"/> — empty; the resulting grant is
    /// issued separately via <see cref="ContentGrantTable"/>.
    /// </summary>
    public static string ContentUnlockParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="Export"/>: the lowercase-hex SHA-256 of a canonical encoding
    /// of the export's filter and destination, so the passkey approval is bound to exactly what will be
    /// written and an approval for one export cannot run another (#165 phase 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Canonical encoding (version 1).</b> The hashed bytes are the ASCII tag <c>arcrouter-export-v1</c>
    /// followed by seven fields in this fixed order: <c>from</c>, <c>to</c>, <c>session</c>, <c>harness</c>,
    /// <c>provider</c>, <c>model</c>, <c>destination</c>. Each field is its UTF-8 bytes preceded by their count
    /// as a 4-byte big-endian unsigned integer, so no field value can shift a boundary or imitate another field.
    /// </para>
    /// <para>
    /// A timestamp is converted to UTC and written as <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> (invariant culture,
    /// seven fractional digits); an absent bound is the empty field. A <see langword="null"/>, empty or
    /// whitespace-only string is the empty field; any other string is written exactly as given - no trimming and
    /// no case folding - because the export writer matches the values as given, so a different spelling is a
    /// different export. The destination is the path exactly as the client sent it, before the router
    /// normalizes it.
    /// </para>
    /// <para>
    /// The dashboard has its own copy of this encoding (<c>PasskeyOperations.ExportParameters</c> in the
    /// <c>Gui.Telemetry</c> project, which cannot reference the router); a golden-value test in each project
    /// pins both to the same digest.
    /// </para>
    /// </remarks>
    /// <param name="filter">Which turns the export includes.</param>
    /// <param name="destinationPath">The zip's destination path, as the client sent it.</param>
    /// <returns>The 64-character lowercase hex SHA-256 of the canonical encoding.</returns>
    public static string ExportParameters(ConversationExportFilter filter, string destinationPath)
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

    /// <summary>
    /// Builds the parameters string for an import bound to a staged archive digest (#165).
    /// </summary>
    /// <param name="stagedArchiveSha256Hex">Lowercase hex SHA-256 of the staged import bytes.</param>
    public static string ImportParameters(string stagedArchiveSha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedArchiveSha256Hex);
        return stagedArchiveSha256Hex;
    }

    /// <summary>
    /// Combines <paramref name="operation"/> and <paramref name="parameters"/> into the single binding key
    /// stored with a one-operation authorization.
    /// </summary>
    public static string FormatBinding(string operation, string? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return $"{operation}\0{parameters ?? string.Empty}";
    }

    /// <summary>Formats an optional time bound for <see cref="ExportParameters"/>: UTC, seven fractional digits, or empty.</summary>
    /// <param name="bound">The bound, or <see langword="null"/> for none.</param>
    /// <returns>The canonical text of the bound.</returns>
    private static string FormatBound(DateTimeOffset? bound) =>
        bound is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Maps a null, empty or whitespace-only filter value to the empty field and leaves any other value as given.</summary>
    /// <param name="value">The filter value.</param>
    /// <returns>The canonical text of the value.</returns>
    private static string NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    /// <summary>Appends a length-prefixed UTF-8 field to the canonical encoding.</summary>
    /// <param name="buffer">The encoding being built.</param>
    /// <param name="value">The field's text.</param>
    private static void WriteField(MemoryStream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }
}
