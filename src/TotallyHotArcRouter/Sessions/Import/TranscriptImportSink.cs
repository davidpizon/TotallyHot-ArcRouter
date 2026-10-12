using System.Text.Json;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>
/// Writes a metadata-only transcript row for each imported turn, so the embedding backfill and the quality
/// rescan treat it like any other unscored turn (#165 plan, decision 3). The row is marked
/// <see cref="SessionTurnOrigin.Imported"/>, which the spend, savings and ROI readers filter out: the router did
/// not serve the turn, so it must never change those totals.
/// </summary>
/// <param name="transcripts">The transcript store the row is written to.</param>
public sealed class TranscriptImportSink(ITranscriptStore transcripts) : IImportedTurnSink
{
    /// <inheritdoc/>
    /// <remarks>
    /// The correlation id is <c>imp-{archive session id}:{turn}</c>. It never collides with a captured turn's
    /// <c>{client session id}:{turn}</c>, which matters because a score update is applied to every row that
    /// carries the correlation id, and a client session id can repeat across machines.
    /// </remarks>
    public async Task TurnStoredAsync(ImportedTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var metadata = turn.Metadata;
        var record = new TranscriptRecord(
            Id: 0,
            CorrelationId: string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"imp-{turn.ArchiveSessionId:N}:{turn.TurnSequence}"),
            CreatedAtUtc: turn.CreatedAtUtc,
            RequestedModel: Text(metadata, "requested_model") ?? "unknown",
            RoutedModel: Text(metadata, "routed_model") ?? "unknown",
            Dimension: Text(metadata, "dimension"),
            Difficulty: Text(metadata, "difficulty"),
            Language: Text(metadata, "language"),
            IsUtility: Flag(metadata, "utility"),
            PromptText: turn.NewestUserMessage,
            ResponseText: turn.ResponseText,
            Score: null,
            Cost: Decimal(metadata, "estimated_cost_usd"),
            IsExploratory: Flag(metadata, "exploratory"),
            Propensity: Number(metadata, "propensity") ?? 1.0,
            InputTokens: Integer(metadata, "prompt_tokens"),
            OutputTokens: Integer(metadata, "completion_tokens"),
            MemoryEntryId: null,
            ArchiveSessionId: turn.ArchiveSessionId,
            ArchiveTurnId: turn.ArchiveTurnId,
            Origin: SessionTurnOrigin.Imported);

        await transcripts.InsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a string property, or <see langword="null"/> when it is absent or not a string.</summary>
    /// <param name="metadata">The metadata object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    private static string? Text(JsonElement metadata, string name) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads a boolean property, treating an absent or non-boolean value as <see langword="false"/>.</summary>
    /// <param name="metadata">The metadata object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The flag.</returns>
    private static bool Flag(JsonElement metadata, string name) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Reads a numeric property as a double, or <see langword="null"/> when it is absent or not a number.</summary>
    /// <param name="metadata">The metadata object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    private static double? Number(JsonElement metadata, string name) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>Reads a numeric property as an integer, or <see langword="null"/> when it is absent or not an integer.</summary>
    /// <param name="metadata">The metadata object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The integer, or <see langword="null"/>.</returns>
    private static int? Integer(JsonElement metadata, string name) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>Reads a numeric property as a decimal, or <see langword="null"/> when it is absent or not a number.</summary>
    /// <param name="metadata">The metadata object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The decimal, or <see langword="null"/>.</returns>
    private static decimal? Decimal(JsonElement metadata, string name) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;
}
