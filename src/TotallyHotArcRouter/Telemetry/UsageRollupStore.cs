using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One aggregated bucket read back from <c>usage_rollup</c>. <see cref="BucketStartUtc"/> is only
/// meaningful when the query grouped by <c>"day"</c> - for a <c>"model"</c> or <c>"provider"</c> grouping
/// it is the query's <c>from</c> bound, since the row already sums across the whole requested range.
/// </summary>
public sealed record UsageRollupBucket(
    DateTimeOffset BucketStartUtc,
    string BucketWidth,
    string GroupKey,
    long Requests,
    long UnpricedRequests,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal CostUsd);

/// <summary>Totals over a date range, for the header ticker and summary tiles.</summary>
public sealed record UsageSummary(
    long Requests,
    long UnpricedRequests,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal CostUsd);

/// <summary>
/// Maintains and reads the <c>usage_rollup</c> pre-aggregated buckets (Phase 4, §5.3), sourced from
/// <c>usage_ledger</c>.
/// </summary>
public interface IUsageRollupStore
{
    /// <summary>
    /// Pins <see cref="StorageOptions.RollupTimezone"/> into <c>rollup_metadata</c> if no timezone has been
    /// pinned yet; a no-op on every subsequent call (tokscale's reproducible-bucket rule - see the type
    /// remarks on <see cref="UsageRollupStore"/>). Safe to call redundantly; <see cref="RollForwardAsync"/>
    /// also does this defensively, so an explicit call is only needed to control exactly when the timezone
    /// is decided (e.g. at startup, before any traffic).
    /// </summary>
    void EnsureBucketTimezone();

    /// <summary>
    /// Applies every <c>usage_ledger</c> entry newer than the last processed checkpoint to
    /// <c>usage_rollup</c>, then advances the checkpoint. Idempotent and safe to call after every ledger
    /// write ("increment the current bucket") and again at startup ("back-fill for buckets missed while
    /// down") - both are the same operation: catch the checkpoint up to the ledger's current end.
    /// </summary>
    /// <returns>The number of ledger entries applied.</returns>
    Task<int> RollForwardAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads aggregated buckets over <c>[from, to)</c> at the given ISO-8601 duration
    /// <paramref name="bucketWidth"/> (<c>"PT30M"</c>, <c>"PT1H"</c>, or <c>"P1D"</c>), grouped by
    /// <paramref name="groupBy"/> (<c>"model"</c>, <c>"provider"</c>, or <c>"day"</c>). Never includes a
    /// bucket that has not fully elapsed yet (honeycomb's never-publish-the-in-progress-bucket rule).
    /// </summary>
    IReadOnlyList<UsageRollupBucket> Query(DateTimeOffset from, DateTimeOffset to, string bucketWidth, string groupBy);

    /// <summary>Totals over <c>[from, to)</c> at daily granularity, excluding any bucket still in progress.</summary>
    UsageSummary Summary(DateTimeOffset from, DateTimeOffset to);
}

/// <inheritdoc cref="IUsageRollupStore" />
/// <remarks>
/// Every write (the per-entry increment and the checkpoint advance) is serialized through a single mutex,
/// mirroring <see cref="ProviderBudgetStore"/>'s spend-write guard: without it, two <see cref="RollForwardAsync"/>
/// calls racing from concurrent <c>UsageLedger.RecordAsync</c> completions could both read the same
/// checkpoint, both apply an overlapping slice of entries, and one could advance the checkpoint past an
/// entry the other has not yet incremented - a permanent, silent under-count with no self-healing path,
/// since a later backfill trusts the checkpoint. Serializing means the same call that increments a bucket
/// is the only one that can move the checkpoint past it.
/// </remarks>
public sealed class UsageRollupStore : IUsageRollupStore, IDisposable
{
    // Round-trip UTC ISO 8601, matching PriceCatalogRepositoryBase.TimestampFormat and UsageLedger's - fixed
    // width and lexicographically ordered, so the range filter in ReadRawBuckets needs no date parsing.
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private static readonly string[] RollupWidths = ["PT30M", "PT1H", "P1D"];

    private readonly PriceCatalogDatabase _database;
    private readonly string _configuredTimezoneId;
    private readonly ILogger<UsageRollupStore>? _logger;
    private readonly SemaphoreSlim _rollForwardMutex = new(1, 1);
    private bool _disposed;

    // Cached after first resolution within a process lifetime: the pinned id in rollup_metadata cannot
    // change (see the type remarks), so re-resolving on every write would be a wasted read plus a redundant
    // TimeZoneInfo lookup for no benefit.
    private TimeZoneInfo? _cachedTimeZone;

    /// <summary>Initializes a new instance of the <see cref="UsageRollupStore"/> class.</summary>
    public UsageRollupStore(
        PriceCatalogDatabase database,
        IOptions<StorageOptions> storageOptions,
        ILogger<UsageRollupStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(storageOptions);
        _database = database;
        _configuredTimezoneId = storageOptions.Value.RollupTimezone;
        _logger = logger;
    }

    /// <inheritdoc />
    public void EnsureBucketTimezone()
    {
        try
        {
            using var connection = _database.OpenConnection();
            PinTimezoneIfMissing(connection);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to pin the usage-rollup bucket timezone.");
        }
    }

    /// <inheritdoc />
    public async Task<int> RollForwardAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        await _rollForwardMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = _database.OpenConnection();
            PinTimezoneIfMissing(connection);

            var checkpoint = ReadCheckpoint(connection);
            var pending = ReadPendingEntries(connection, checkpoint);
            if (pending.Count == 0)
            {
                return 0;
            }

            var tz = ResolveTimeZone(connection);
            foreach (var (_, entry) in pending)
            {
                ApplyIncrement(connection, entry, tz);
            }

            WriteCheckpoint(connection, pending[^1].EntryId);
            return pending.Count;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Usage-rollup roll-forward failed; will retry on the next write or startup.");
            return 0;
        }
        finally
        {
            _rollForwardMutex.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageRollupBucket> Query(DateTimeOffset from, DateTimeOffset to, string bucketWidth, string groupBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketWidth);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupBy);

        var rows = ReadRawBuckets(from, to, bucketWidth);

        if (string.Equals(groupBy, "day", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .GroupBy(r => r.BucketStartUtc)
                .OrderBy(g => g.Key)
                .Select(g => Summarize(g.Key, bucketWidth, g.Key.ToString("O", CultureInfo.InvariantCulture), g))
                .ToList();
        }

        if (string.Equals(groupBy, "model", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .GroupBy(r => r.Model)
                .Select(g => Summarize(from, bucketWidth, g.Key, g))
                .OrderByDescending(b => b.CostUsd)
                .ToList();
        }

        if (string.Equals(groupBy, "provider", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .GroupBy(r => r.Provider)
                .Select(g => Summarize(from, bucketWidth, g.Key, g))
                .OrderByDescending(b => b.CostUsd)
                .ToList();
        }

        throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, "groupBy must be 'model', 'provider', or 'day'.");
    }

    /// <inheritdoc />
    public UsageSummary Summary(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = ReadRawBuckets(from, to, "P1D");
        return new UsageSummary(
            Requests: rows.Sum(r => r.Requests),
            UnpricedRequests: rows.Sum(r => r.UnpricedRequests),
            PromptTokens: rows.Sum(r => r.PromptTokens),
            CompletionTokens: rows.Sum(r => r.CompletionTokens),
            CacheCreationTokens: rows.Sum(r => r.CacheCreationTokens),
            CacheReadTokens: rows.Sum(r => r.CacheReadTokens),
            CostUsd: rows.Aggregate(0m, (acc, r) => acc + r.CostUsd));
    }

    /// <summary>Aggregates a group of raw rollup rows into a single <see cref="UsageRollupBucket"/> by summing every numeric column.</summary>
    /// <param name="bucketStartUtc">The bucket start to report on the result (the group's own key for a day grouping, otherwise the query's <c>from</c> bound).</param>
    /// <param name="bucketWidth">The rollup width the rows were read at.</param>
    /// <param name="groupKey">The grouping key to report (a model name, provider name, or ISO timestamp).</param>
    /// <param name="rows">The raw rows to sum.</param>
    /// <returns>The summed bucket.</returns>
    private static UsageRollupBucket Summarize(DateTimeOffset bucketStartUtc, string bucketWidth, string groupKey, IEnumerable<RawRow> rows) =>
        new(
            bucketStartUtc,
            bucketWidth,
            groupKey,
            Requests: rows.Sum(r => r.Requests),
            UnpricedRequests: rows.Sum(r => r.UnpricedRequests),
            PromptTokens: rows.Sum(r => r.PromptTokens),
            CompletionTokens: rows.Sum(r => r.CompletionTokens),
            CacheCreationTokens: rows.Sum(r => r.CacheCreationTokens),
            CacheReadTokens: rows.Sum(r => r.CacheReadTokens),
            CostUsd: rows.Aggregate(0m, (acc, r) => acc + r.CostUsd));

    // Raw row shape read back from usage_rollup before grouping - deliberately not a public record, since
    // its BucketStartUtc/Provider/Model triple is an implementation detail of how Query groups results.
    /// <summary>Raw row shape read back from <c>usage_rollup</c> before grouping.</summary>
    /// <param name="BucketStartUtc">The bucket's UTC start instant.</param>
    /// <param name="Provider">The provider key the bucket's requests were served by.</param>
    /// <param name="Model">The resolved model the bucket's requests were served by.</param>
    /// <param name="Requests">Total request count in the bucket.</param>
    /// <param name="UnpricedRequests">Count of requests in the bucket with no known price.</param>
    /// <param name="PromptTokens">Summed prompt/input tokens.</param>
    /// <param name="CompletionTokens">Summed completion/output tokens.</param>
    /// <param name="CacheCreationTokens">Summed cache-creation input tokens.</param>
    /// <param name="CacheReadTokens">Summed cache-read input tokens.</param>
    /// <param name="CostUsd">Summed estimated USD cost.</param>
    private readonly record struct RawRow(
        DateTimeOffset BucketStartUtc,
        string Provider,
        string Model,
        long Requests,
        long UnpricedRequests,
        long PromptTokens,
        long CompletionTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        decimal CostUsd);

    /// <summary>
    /// Reads every stored bucket of <paramref name="bucketWidth"/> in <c>[from, to)</c>, excluding any
    /// bucket that has not fully elapsed yet in the pinned rollup timezone.
    /// </summary>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <param name="bucketWidth">The rollup width to read.</param>
    /// <returns>The matching raw rows.</returns>
    private List<RawRow> ReadRawBuckets(DateTimeOffset from, DateTimeOffset to, string bucketWidth)
    {
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.OpenConnection();
        var tz = ResolveTimeZone(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bucket_start_utc, provider, model, requests, unpriced_requests,
                   prompt_tokens, completion_tokens, cache_creation_tokens, cache_read_tokens, cost_usd
            FROM usage_rollup
            WHERE bucket_width = $width AND bucket_start_utc >= $from AND bucket_start_utc < $to;
            """;
        command.Parameters.AddWithValue("$width", bucketWidth);
        command.Parameters.AddWithValue("$from", from.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        var rows = new List<RawRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bucketStart = ParseTimestamp(reader.GetString(0));

            // Honeycomb's rule: a bucket that has not fully elapsed yet would return a partial figure a
            // later read would contradict, so it is excluded from every query rather than published early.
            if (BucketEndUtc(bucketStart, tz, bucketWidth) > now)
            {
                continue;
            }

            rows.Add(new RawRow(
                bucketStart,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>Converts a supported ISO-8601 bucket width string to its fixed <see cref="TimeSpan"/> duration.</summary>
    /// <param name="width">One of <c>"PT30M"</c>, <c>"PT1H"</c>, or <c>"P1D"</c>.</param>
    /// <returns>The corresponding duration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> is not one of the supported values.</exception>
    private static TimeSpan WidthToDuration(string width) => width switch
    {
        "PT30M" => TimeSpan.FromMinutes(30),
        "PT1H" => TimeSpan.FromHours(1),
        "P1D" => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(width), width, "Unknown bucket width; expected 'PT30M', 'PT1H', or 'P1D'."),
    };

    // For P1D this is NOT bucketStartUtc + 24h: BucketStartUtc floors to local midnight in the pinned
    // timezone, and a local calendar day can be 23h or 25h long across a DST transition, so a fixed 24h
    // duration can call a still-in-progress bucket "elapsed" (spring-forward) or hold a genuinely finished
    // one back an extra hour (fall-back). Walking to the next local midnight and converting that back to
    // UTC - the same wall-clock math BucketStartUtc itself uses - gives the true end of that calendar day
    // regardless of its actual UTC-duration. PT30M/PT1H buckets don't span a local calendar day, so a fixed
    // duration is exact for them.
    /// <summary>Computes the UTC instant a bucket that started at <paramref name="bucketStartUtc"/> ends, honoring DST transitions for day-width buckets.</summary>
    /// <param name="bucketStartUtc">The bucket's UTC start instant.</param>
    /// <param name="tz">The pinned rollup timezone.</param>
    /// <param name="width">The bucket width.</param>
    /// <returns>The UTC instant the bucket ends (exclusive).</returns>
    private static DateTimeOffset BucketEndUtc(DateTimeOffset bucketStartUtc, TimeZoneInfo tz, string width)
    {
        if (width != "P1D")
        {
            return bucketStartUtc + WidthToDuration(width);
        }

        var localStart = TimeZoneInfo.ConvertTime(bucketStartUtc, tz).DateTime;
        var nextLocalMidnight = localStart.Date.AddDays(1);
        if (tz.IsInvalidTime(nextLocalMidnight))
        {
            nextLocalMidnight = nextLocalMidnight.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, tz);
    }

    // INSERT OR IGNORE, not an upsert: once a row exists at id=1 it is never modified except for
    // last_rolled_up_entry_id (via WriteCheckpoint), because the timezone itself is write-once (see the
    // type remarks).
    /// <summary>Pins the configured rollup timezone into <c>rollup_metadata</c> if no row exists yet; a no-op once pinned, since the timezone is write-once.</summary>
    /// <param name="connection">The open database connection to write through.</param>
    private void PinTimezoneIfMissing(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO rollup_metadata (id, bucket_timezone_iana_id, bucket_timezone_pinned_at_utc, last_rolled_up_entry_id)
            VALUES (1, $tz, $pinnedAt, 0);
            """;
        command.Parameters.AddWithValue("$tz", _configuredTimezoneId);
        command.Parameters.AddWithValue("$pinnedAt", DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Resolves the pinned rollup timezone, caching the result for the process lifetime since it cannot
    /// change once pinned. Falls back to UTC (with a warning) if the pinned IANA id is not resolvable on
    /// this host.
    /// </summary>
    /// <param name="connection">The open database connection to read <c>rollup_metadata</c> from.</param>
    /// <returns>The resolved timezone.</returns>
    private TimeZoneInfo ResolveTimeZone(SqliteConnection connection)
    {
        if (_cachedTimeZone is { } cached)
        {
            return cached;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bucket_timezone_iana_id FROM rollup_metadata WHERE id = 1;";
        var id = command.ExecuteScalar() as string ?? "UTC";

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger?.LogWarning(ex, "Pinned rollup timezone {TimezoneId} could not be resolved on this host; falling back to UTC.", id);
            tz = TimeZoneInfo.Utc;
        }

        _cachedTimeZone = tz;
        return tz;
    }

    /// <summary>Reads the last <c>usage_ledger</c> entry id already rolled up, or 0 if roll-forward has never run.</summary>
    /// <param name="connection">The open database connection to read from.</param>
    /// <returns>The checkpoint entry id.</returns>
    private static long ReadCheckpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_rolled_up_entry_id FROM rollup_metadata WHERE id = 1;";
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Advances the roll-forward checkpoint to <paramref name="entryId"/>, marking every entry up to and including it as applied.</summary>
    /// <param name="connection">The open database connection to write through.</param>
    /// <param name="entryId">The highest <c>usage_ledger</c> entry id that has now been rolled up.</param>
    private static void WriteCheckpoint(SqliteConnection connection, long entryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rollup_metadata SET last_rolled_up_entry_id = $entryId WHERE id = 1;";
        command.Parameters.AddWithValue("$entryId", entryId);
        command.ExecuteNonQuery();
    }

    /// <summary>Reads every <c>usage_ledger</c> entry with an id greater than <paramref name="checkpoint"/>, in ascending id order.</summary>
    /// <param name="connection">The open database connection to read from.</param>
    /// <param name="checkpoint">The last entry id already rolled up.</param>
    /// <returns>The pending entries paired with their entry ids, oldest first.</returns>
    private static List<(long EntryId, UsageLedgerEntry Entry)> ReadPendingEntries(SqliteConnection connection, long checkpoint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry_id, session_id, turn_number, provider, requested_model, resolved_model,
                   prompt_tokens, completion_tokens, cache_creation_tokens, cache_read_tokens,
                   estimated_cost_usd, cost_confidence, occurred_at_utc
            FROM usage_ledger
            WHERE entry_id > $checkpoint
            ORDER BY entry_id;
            """;
        command.Parameters.AddWithValue("$checkpoint", checkpoint);

        var pending = new List<(long, UsageLedgerEntry)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entryId = reader.GetInt64(0);
            var entry = new UsageLedgerEntry(
                SessionId: reader.GetString(1),
                TurnNumber: reader.GetInt32(2),
                Provider: reader.GetString(3),
                RequestedModel: reader.GetString(4),
                ResolvedModel: reader.GetString(5),
                PromptTokens: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                CompletionTokens: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                CacheCreationTokens: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                CacheReadTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                EstimatedCostUsd: reader.IsDBNull(10) ? null : decimal.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                CostConfidence: Enum.Parse<CostConfidence>(reader.GetString(11)),
                OccurredAtUtc: ParseTimestamp(reader.GetString(12)));
            pending.Add((entryId, entry));
        }

        return pending;
    }

    /// <summary>Increments every rollup width's bucket (30-minute, hourly, and daily) for one ledger entry.</summary>
    /// <param name="connection">The open database connection to write through.</param>
    /// <param name="entry">The ledger entry to fold into the rollup.</param>
    /// <param name="tz">The pinned rollup timezone, used to compute each bucket's start.</param>
    private void ApplyIncrement(SqliteConnection connection, UsageLedgerEntry entry, TimeZoneInfo tz)
    {
        var unpriced = entry.EstimatedCostUsd is null ? 1 : 0;
        var cost = entry.EstimatedCostUsd ?? 0m;
        var prompt = entry.PromptTokens ?? 0;
        var completion = entry.CompletionTokens ?? 0;
        var cacheCreation = entry.CacheCreationTokens ?? 0;
        var cacheRead = entry.CacheReadTokens ?? 0;

        foreach (var width in RollupWidths)
        {
            DateTime bucketStartUtc;
            try
            {
                bucketStartUtc = BucketStartUtc(entry.OccurredAtUtc, tz, width);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Failed to compute a {Width} rollup bucket for session {SessionId}; skipping that bucket.",
                    width,
                    SanitizeForLog(entry.SessionId));
                continue;
            }

            UpsertBucket(connection, bucketStartUtc, width, entry.Provider, entry.ResolvedModel, unpriced, prompt, completion, cacheCreation, cacheRead, cost);
        }
    }

    // Floors occurredAtUtc to a bucket boundary in the pinned wall-clock timezone, then converts that
    // boundary back to UTC for storage - the "wall-clock day, stored as UTC" shape the analysis's
    // reproducible-bucket rule describes. IsInvalidTime handles the one-hour spring-forward gap (a floored
    // local time that never occurred) by nudging forward past it; SQLite storage and range queries only
    // ever see the resulting UTC instant, never the local wall-clock value.
    /// <summary>Floors <paramref name="occurredAtUtc"/> to a bucket boundary in the pinned wall-clock timezone, then converts that boundary back to UTC for storage.</summary>
    /// <param name="occurredAtUtc">The request's UTC completion timestamp.</param>
    /// <param name="tz">The pinned rollup timezone.</param>
    /// <param name="width">The bucket width to floor to.</param>
    /// <returns>The bucket's start instant, in UTC.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> is not one of the supported values.</exception>
    private static DateTime BucketStartUtc(DateTimeOffset occurredAtUtc, TimeZoneInfo tz, string width)
    {
        var local = TimeZoneInfo.ConvertTime(occurredAtUtc, tz).DateTime;
        var floored = width switch
        {
            "PT30M" => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute - (local.Minute % 30), 0, DateTimeKind.Unspecified),
            "PT1H" => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified),
            "P1D" => new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified),
            _ => throw new ArgumentOutOfRangeException(nameof(width), width, "Unknown bucket width; expected 'PT30M', 'PT1H', or 'P1D'."),
        };

        if (tz.IsInvalidTime(floored))
        {
            floored = floored.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(floored, tz);
    }

    /// <summary>
    /// Inserts or increments one <c>usage_rollup</c> bucket for one ledger entry, transactionally
    /// read-modify-writing the decimal <c>cost_usd</c> column (stored as TEXT) rather than relying on SQL
    /// arithmetic, which would lose decimal precision.
    /// </summary>
    /// <param name="connection">The open database connection to write through.</param>
    /// <param name="bucketStartUtc">The bucket's UTC start instant.</param>
    /// <param name="bucketWidth">The bucket width being incremented.</param>
    /// <param name="provider">The provider key.</param>
    /// <param name="model">The resolved model.</param>
    /// <param name="unpriced">1 if the entry had no known price; otherwise 0.</param>
    /// <param name="promptTokens">Prompt tokens to add.</param>
    /// <param name="completionTokens">Completion tokens to add.</param>
    /// <param name="cacheCreationTokens">Cache-creation tokens to add.</param>
    /// <param name="cacheReadTokens">Cache-read tokens to add.</param>
    /// <param name="cost">Estimated USD cost to add.</param>
    private void UpsertBucket(
        SqliteConnection connection,
        DateTime bucketStartUtc,
        string bucketWidth,
        string provider,
        string model,
        int unpriced,
        long promptTokens,
        long completionTokens,
        long cacheCreationTokens,
        long cacheReadTokens,
        decimal cost)
    {
        var bucketStartText = bucketStartUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        using var transaction = connection.BeginTransaction();

        // Read-modify-write the decimal cost under this transaction, same pattern as
        // ProviderSpendRepository.AddProviderSpend: SQL '+' is fine for the integer counters below, but a
        // money column stored as TEXT needs true decimal addition, not SQL arithmetic on a string.
        decimal existingCost = 0m;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT cost_usd FROM usage_rollup
                WHERE bucket_start_utc = $b AND bucket_width = $w AND provider = $p AND model = $m;
                """;
            read.Parameters.AddWithValue("$b", bucketStartText);
            read.Parameters.AddWithValue("$w", bucketWidth);
            read.Parameters.AddWithValue("$p", provider);
            read.Parameters.AddWithValue("$m", model);
            if (read.ExecuteScalar() is string existing)
            {
                existingCost = decimal.Parse(existing, CultureInfo.InvariantCulture);
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO usage_rollup (
                    bucket_start_utc, bucket_width, provider, model, requests, unpriced_requests,
                    prompt_tokens, completion_tokens, cache_creation_tokens, cache_read_tokens, cost_usd)
                VALUES ($b, $w, $p, $m, 1, $unpriced, $prompt, $completion, $cacheCreation, $cacheRead, $cost)
                ON CONFLICT(bucket_start_utc, bucket_width, provider, model) DO UPDATE SET
                    requests               = requests + 1,
                    unpriced_requests      = unpriced_requests + excluded.unpriced_requests,
                    prompt_tokens          = prompt_tokens + excluded.prompt_tokens,
                    completion_tokens      = completion_tokens + excluded.completion_tokens,
                    cache_creation_tokens  = cache_creation_tokens + excluded.cache_creation_tokens,
                    cache_read_tokens      = cache_read_tokens + excluded.cache_read_tokens,
                    cost_usd               = excluded.cost_usd;
                """;
            upsert.Parameters.AddWithValue("$b", bucketStartText);
            upsert.Parameters.AddWithValue("$w", bucketWidth);
            upsert.Parameters.AddWithValue("$p", provider);
            upsert.Parameters.AddWithValue("$m", model);
            upsert.Parameters.AddWithValue("$unpriced", unpriced);
            upsert.Parameters.AddWithValue("$prompt", promptTokens);
            upsert.Parameters.AddWithValue("$completion", completionTokens);
            upsert.Parameters.AddWithValue("$cacheCreation", cacheCreationTokens);
            upsert.Parameters.AddWithValue("$cacheRead", cacheReadTokens);
            upsert.Parameters.AddWithValue("$cost", (existingCost + cost).ToString(CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Parses a stored round-trip UTC timestamp back into a <see cref="DateTimeOffset"/>.</summary>
    /// <param name="value">The stored timestamp text, in <see cref="TimestampFormat"/>.</param>
    /// <returns>The parsed UTC instant.</returns>
    private static DateTimeOffset ParseTimestamp(string value) =>
        new(
            DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            TimeSpan.Zero);

    /// <summary>Strips CR/LF from a client-controlled session id so it cannot forge additional log lines.</summary>
    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");

    /// <summary>Disposes the roll-forward mutex.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rollForwardMutex.Dispose();
    }
}
