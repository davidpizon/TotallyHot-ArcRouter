using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Reads <c>created_at_utc</c>, <c>input_tokens</c>, <c>output_tokens</c>, and <c>cost</c> from
/// <c>request_transcripts</c> for the token-efficiency line (issue #304 phase 9). Capture being off
/// does not hide rows already stored: this reader does not consult <c>TranscriptOptions.Enabled</c>.
/// It does not select question text or response text, and it does not read <c>usage_rollup</c>.
/// </summary>
public sealed class TranscriptTokenCostReader
{
    /// <summary>
    /// The four columns this chart is allowed to read. A test fails if prompt or response text appears here.
    /// </summary>
    public const string SelectCommandText = """
                                            SELECT id, created_at_utc, input_tokens, output_tokens, cost
                                            FROM request_transcripts
                                            WHERE created_at_utc >= $from AND created_at_utc < $to
                                            ORDER BY created_at_utc, id;
                                            """;

    private readonly TranscriptDatabase _database;
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscriptTokenCostReader"/> class.</summary>
    /// <param name="database">The transcript file. Rows already stored are read even when capture is off.</param>
    /// <param name="logger">Receives a warning when a row's timestamp does not parse. May be null in a test.</param>
    public TranscriptTokenCostReader(TranscriptDatabase database, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Reads the rows in <c>[from, to)</c>. A null cost stays null. A row whose timestamp does not parse
    /// is skipped and is not plotted at time zero.
    /// </summary>
    /// <param name="from">Inclusive lower bound, compared in UTC.</param>
    /// <param name="to">Exclusive upper bound, compared in UTC.</param>
    /// <returns>The rows, oldest first. Empty when the file does not exist yet.</returns>
    public IReadOnlyList<TranscriptTokenCostPoint> List(DateTimeOffset from, DateTimeOffset to)
    {
        if (!File.Exists(_database.DatabasePath))
            return [];

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectCommandText;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        command.Parameters.AddWithValue(parameterName: "$from", value: fromUtc.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$to", value: toUtc.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));

        var points = new List<TranscriptTokenCostPoint>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!DateTimeOffset.TryParse(input: reader.GetString(1), formatProvider: CultureInfo.InvariantCulture,
                    styles: DateTimeStyles.RoundtripKind, result: out var created))
            {
                _logger?.LogWarning("Skipping transcript {TranscriptId} because created_at_utc did not parse.", id);
                continue;
            }

            var createdUtc = created.ToUniversalTime();
            if (createdUtc < fromUtc || createdUtc >= toUtc)
                continue;

            points.Add(new TranscriptTokenCostPoint(
                Id: id,
                CreatedAtUtc: createdUtc,
                InputTokens: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                OutputTokens: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Cost: ReadCost(reader: reader, ordinal: 4)));
        }

        return points;
    }

    /// <summary>
    /// Reads <c>cost</c> without turning null into 0. A decimal stored as text keeps its exact digits.
    /// A REAL comes back as the decimal of that double, which is how <c>SqliteTranscriptStore</c> already reads it.
    /// </summary>
    private static decimal? ReadCost(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal stored => stored,
            double real => (decimal)real,
            float real => (decimal)real,
            long integer => integer,
            int integer => integer,
            string text => decimal.Parse(s: text, provider: CultureInfo.InvariantCulture),
            _ => decimal.Parse(s: Convert.ToString(value: value, provider: CultureInfo.InvariantCulture)!, provider: CultureInfo.InvariantCulture)
        };
    }
}
