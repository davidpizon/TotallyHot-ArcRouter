using System.Globalization;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>One named incomplete reason and how many receipts in the window used it.</summary>
/// <param name="Reason">The stored reason, or <see cref="IncompleteGradeCountQuery.NoReasonStored"/>.</param>
/// <param name="Count">How many incomplete receipts had this reason. Zero means no receipt, not an unknown cost.</param>
public sealed record IncompleteReasonCount(string Reason, int Count);

/// <summary>One abstention token that occurred on an incomplete receipt in the window.</summary>
/// <param name="Token">The <c>grader:reason</c> piece, as stored.</param>
/// <param name="Count">How many times that piece occurred. A token that never occurred is not listed.</param>
public sealed record AbstainedTokenCount(string Token, int Count);

/// <summary>
/// Incomplete receipts in a window, counted by reason, plus the abstention tokens that actually occurred
/// (issue #304 phase 9). The headline is the row count. It is not the sum of the two lists.
/// </summary>
/// <param name="IncompleteReceipts">How many <c>Incomplete</c> rows fell in the window.</param>
/// <param name="ByReason">The eight named reasons, zeros included, then any extra reason that occurred.</param>
/// <param name="Abstained">Abstention tokens with a count of at least one, in first-seen order.</param>
public sealed record IncompleteGradeCounts(
    int IncompleteReceipts,
    IReadOnlyList<IncompleteReasonCount> ByReason,
    IReadOnlyList<AbstainedTokenCount> Abstained);

/// <summary>
/// Counts <c>grade_receipts</c> whose status is <c>Incomplete</c> (issue #304 phase 9). It does not select
/// question text. A <c>Complete</c> or <c>StructureOnly</c> row is not an incomplete reason.
/// </summary>
public sealed class IncompleteGradeCountQuery
{
    /// <summary>The label for a null or empty <c>incomplete_reason</c>. Shown only when at least one row has none.</summary>
    public const string NoReasonStored = "no reason stored";

    /// <summary>The columns this count is allowed to read.</summary>
    public const string SelectCommandText = """
                                            SELECT incomplete_reason, abstained_graders
                                            FROM grade_receipts
                                            WHERE grade_status = 'Incomplete'
                                              AND created_at_utc >= $from AND created_at_utc < $to;
                                            """;

    private static readonly string[] NamedReasons =
    [
        IncompleteReasons.QueueFull,
        IncompleteReasons.NoText,
        IncompleteReasons.Empty,
        IncompleteReasons.NoJudge,
        IncompleteReasons.JudgeIsAnswerer,
        IncompleteReasons.Disputed,
        IncompleteReasons.DeferredToLoop,
        IncompleteReasons.LoopUnlinked
    ];

    private readonly RouterMemoryDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="IncompleteGradeCountQuery"/> class.</summary>
    /// <param name="database">The router-memory file that holds <c>grade_receipts</c>.</param>
    public IncompleteGradeCountQuery(RouterMemoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Counts incomplete receipts in <c>[from, to)</c>. The eight named reasons are always present.
    /// A reason with no row is 0. <see cref="NoReasonStored"/> is included only when its count is at least 1.
    /// </summary>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <returns>The headline row count, the reason list, and the abstention tokens that occurred.</returns>
    public IncompleteGradeCounts Count(DateTimeOffset from, DateTimeOffset to)
    {
        if (!File.Exists(_database.DatabasePath))
            return Empty();

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectCommandText;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        command.Parameters.AddWithValue(parameterName: "$from", value: fromUtc.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$to", value: toUtc.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));

        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var abstained = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows++;
            var reason = reader.IsDBNull(0) ? null : reader.GetString(0);
            var key = string.IsNullOrEmpty(reason) ? NoReasonStored : reason;
            byReason[key] = byReason.GetValueOrDefault(key) + 1;
            CountTokens(tokens: reader.IsDBNull(1) ? null : reader.GetString(1), counts: abstained);
        }

        var reasons = new List<IncompleteReasonCount>(NamedReasons.Length + byReason.Count);
        foreach (var name in NamedReasons)
            reasons.Add(new IncompleteReasonCount(Reason: name, Count: byReason.GetValueOrDefault(name)));

        if (byReason.GetValueOrDefault(NoReasonStored) is var missing and > 0)
            reasons.Add(new IncompleteReasonCount(Reason: NoReasonStored, Count: missing));

        foreach (var pair in byReason.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key == NoReasonStored || NamedReasons.Contains(pair.Key, StringComparer.Ordinal))
                continue;
            if (pair.Value > 0)
                reasons.Add(new IncompleteReasonCount(Reason: pair.Key, Count: pair.Value));
        }

        var tokens = abstained
            .Where(pair => pair.Value > 0)
            .Select(pair => new AbstainedTokenCount(Token: pair.Key, Count: pair.Value))
            .ToList();
        return new IncompleteGradeCounts(IncompleteReceipts: rows, ByReason: reasons, Abstained: tokens);
    }

    /// <summary>The eight named reasons at 0, for a database file that is not there yet.</summary>
    private static IncompleteGradeCounts Empty()
    {
        return new IncompleteGradeCounts(
            0,
            ByReason: NamedReasons.Select(name => new IncompleteReasonCount(Reason: name, Count: 0)).ToList(),
            Abstained: []);
    }

    /// <summary>Splits <c>abstained_graders</c> on commas. Empty pieces are ignored. A piece with no colon still counts.</summary>
    private static void CountTokens(string? tokens, Dictionary<string, int> counts)
    {
        if (string.IsNullOrEmpty(tokens))
            return;

        foreach (var piece in tokens.Split(separator: ','))
        {
            if (piece.Length == 0 || piece.Trim().Length == 0)
                continue;
            var token = piece.Trim();
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }
    }
}
