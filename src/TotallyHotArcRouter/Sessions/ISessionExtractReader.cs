using System.Text.Json;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// The newest user message and the assistant reply stored in a turn's Extracts frame (#165 phase 2).
/// Learning jobs and the Sessions tab read these instead of <c>request_transcripts</c> text columns.
/// </summary>
/// <param name="NewestUserMessage">The newest user message, or <see langword="null"/> when the extract omitted it.</param>
/// <param name="ResponseText">The assistant reply, or <see langword="null"/> when capture recorded it as missing or truncated.</param>
public sealed record SessionExtracts(string? NewestUserMessage, string? ResponseText)
{
    /// <summary>Parses the Extracts JSON written at capture. Unknown fields are ignored.</summary>
    /// <param name="utf8Json">The decrypted Extracts body.</param>
    /// <returns>The two text fields, either of which may be null.</returns>
    public static SessionExtracts Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        return new SessionExtracts(Text(root, "newest_user_message"), Text(root, "response_text"));
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Reads one turn's Extracts frame from its session file. A missing session, turn, or frame is
/// <see langword="null"/>, never an empty stand-in and never a scan of every session.
/// </summary>
public interface ISessionExtractReader
{
    /// <summary>Decrypts the Extracts frame for one turn.</summary>
    /// <param name="archiveSessionId">The session file's archive id.</param>
    /// <param name="archiveTurnId">The turn's archive id.</param>
    /// <returns>The extracts, or <see langword="null"/> when they cannot be read.</returns>
    SessionExtracts? TryReadExtracts(Guid archiveSessionId, Guid archiveTurnId);
}
