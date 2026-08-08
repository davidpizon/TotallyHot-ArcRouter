using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// The durable, restart-surviving record of every request's usage
/// (<c>docs/router/token-tracking-implementation-plan.md</c> Phase 2, §5.2). Distinct from
/// <see cref="ITelemetryPublisher"/>, which only fans a request out to connected dashboards for the
/// process's lifetime: this is what a report generated after a restart, or a query spanning "last month",
/// reads from.
/// </summary>
public interface IUsageLedger
{
    /// <summary>
    /// Records one request's usage. Best-effort and never throws: a storage failure is logged and
    /// swallowed, matching every other telemetry side-effect on the request path (see
    /// <c>ProxyMiddleware.PublishTelemetryAsync</c>). Idempotent under replay - two calls describing the
    /// same request (the same upstream request id, or, absent one, the same composite of session, turn,
    /// provider, model, token counts, and second-truncated timestamp) write at most one row.
    /// </summary>
    /// <param name="entry">The usage to record.</param>
    /// <param name="cancellationToken">Cancels waiting on the write; does not roll back a write already in flight.</param>
    Task RecordAsync(UsageLedgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the highest <see cref="UsageLedgerEntry.TurnNumber"/> ever recorded for
    /// <paramref name="sessionId"/>, or 0 if none has been recorded. Used by
    /// <see cref="PersistentConversationTurnTracker"/> to seed its in-memory counter on first sight of a
    /// session, so a turn number survives a process restart.
    /// </summary>
    int GetMaxTurnNumber(string sessionId);

    /// <summary>
    /// Deletes every row whose <see cref="UsageLedgerEntry.OccurredAtUtc"/> is strictly before
    /// <paramref name="cutoffUtc"/>. Used by the startup health check's retention sweep
    /// (<c>Storage:UsageLedgerRetentionDays</c>).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    int DeleteOlderThan(DateTimeOffset cutoffUtc);
}

/// <inheritdoc cref="IUsageLedger" />
public sealed class UsageLedger : IUsageLedger
{
    // Same round-trip UTC ISO 8601 layout as PriceCatalogRepository.TimestampFormat, so lexicographic TEXT
    // comparison agrees with chronological order for the retention sweep and the occurred_at index.
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    // Composite dedup keys hash a timestamp truncated to the second, not the full round-trip value: two
    // otherwise-identical publishes of the same request (e.g. a retried telemetry write) that happen to
    // observe DateTimeOffset.UtcNow a few ticks apart must still collide on dedup, or the validation gate's
    // whole reason for existing - replay must not double-count - is defeated by sub-second jitter.
    private const string CompositeTimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    // A request timestamped further into the future than ordinary clock skew is a signal something
    // upstream of this call has a bad clock or a corrupted timestamp - reject rather than accept and let a
    // future-dated row skew every "since" query and the retention sweep's boundary.
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);

    private readonly PriceCatalogDatabase _database;
    private readonly IUsageRollupStore? _rollupStore;
    private readonly ILogger<UsageLedger>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsageLedger"/> class.
    /// </summary>
    /// <param name="database">The shared price-catalog database this ledger's table lives in.</param>
    /// <param name="rollupStore">
    /// The Phase 4 rollup maintainer to roll a freshly recorded entry into <c>usage_rollup</c>
    /// ("increment the current bucket" - §5.3). Optional and defaults to <see langword="null"/> so every
    /// existing caller/test that constructs a ledger without one keeps compiling; when absent, entries are
    /// still durably recorded but rollups only catch up on the next startup backfill.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public UsageLedger(PriceCatalogDatabase database, IUsageRollupStore? rollupStore = null, ILogger<UsageLedger>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _rollupStore = rollupStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(UsageLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (cancellationToken.IsCancellationRequested)
        {
            // The request was aborted (ProxyMiddleware passes the request token to callers that choose to
            // observe it). Recording is a best-effort post-response side-effect, so a cancellation here is
            // expected, not an error - mirrors ProviderBudgetStore.RecordUsageAsync's OperationCanceledException
            // handling.
            return;
        }

        if (!TryValidate(entry, out var rejectionReason))
        {
            _logger?.LogWarning(
                "Dropped usage-ledger entry for session {SessionId} turn {TurnNumber}: {Reason}.",
                SanitizeForLog(entry.SessionId),
                entry.TurnNumber,
                rejectionReason);
            return;
        }

        var inserted = false;
        try
        {
            var dedupKey = BuildDedupKey(entry);
            var occurredAt = entry.OccurredAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO usage_ledger (
                    dedup_key, session_id, turn_number, provider, requested_model, resolved_model,
                    prompt_tokens, completion_tokens, cache_creation_tokens, cache_read_tokens,
                    estimated_cost_usd, cost_confidence, occurred_at_utc)
                VALUES (
                    $dedupKey, $sessionId, $turnNumber, $provider, $requestedModel, $resolvedModel,
                    $promptTokens, $completionTokens, $cacheCreationTokens, $cacheReadTokens,
                    $estimatedCostUsd, $costConfidence, $occurredAt)
                ON CONFLICT(dedup_key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$dedupKey", dedupKey);
            command.Parameters.AddWithValue("$sessionId", entry.SessionId);
            command.Parameters.AddWithValue("$turnNumber", entry.TurnNumber);
            command.Parameters.AddWithValue("$provider", entry.Provider);
            command.Parameters.AddWithValue("$requestedModel", entry.RequestedModel);
            command.Parameters.AddWithValue("$resolvedModel", entry.ResolvedModel);
            command.Parameters.AddWithValue("$promptTokens", (object?)entry.PromptTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$completionTokens", (object?)entry.CompletionTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$cacheCreationTokens", (object?)entry.CacheCreationTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$cacheReadTokens", (object?)entry.CacheReadTokens ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$estimatedCostUsd",
                entry.EstimatedCostUsd is { } cost ? cost.ToString(CultureInfo.InvariantCulture) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$costConfidence", entry.CostConfidence.ToString());
            command.Parameters.AddWithValue("$occurredAt", occurredAt);

            // A DO NOTHING conflict returns 0 rows affected, distinguishing a fresh row (roll it into
            // usage_rollup) from a replay of an already-recorded request (already rolled up the first time).
            inserted = command.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to record usage-ledger entry for session {SessionId} turn {TurnNumber}.",
                SanitizeForLog(entry.SessionId),
                entry.TurnNumber);
            return;
        }

        if (inserted && _rollupStore is not null)
        {
            // Best-effort and self-contained: RollForwardAsync already logs and swallows its own failures,
            // so a rollup hiccup never turns a successful ledger write into a warning about the write itself.
            await _rollupStore.RollForwardAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public int GetMaxTurnNumber(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(turn_number) FROM usage_ledger WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public int DeleteOlderThan(DateTimeOffset cutoffUtc)
    {
        var cutoff = cutoffUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM usage_ledger WHERE occurred_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    // The §5.4 validation gate: reject negative token/cost figures and implausibly future-dated entries at
    // ingest, rather than let a translator regression silently corrupt the durable ledger. Logged at
    // Warning (not Error - this is an expected defensive check, not an operational failure) and dropped.
    private static bool TryValidate(UsageLedgerEntry entry, out string reason)
    {
        if (entry.PromptTokens is < 0 || entry.CompletionTokens is < 0 ||
            entry.CacheCreationTokens is < 0 || entry.CacheReadTokens is < 0)
        {
            reason = "a token count was negative";
            return false;
        }

        if (entry.EstimatedCostUsd is < 0m)
        {
            reason = "estimated cost was negative";
            return false;
        }

        if (entry.OccurredAtUtc - DateTimeOffset.UtcNow > FutureClockTolerance)
        {
            reason = "the timestamp was too far in the future";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Builds the dedup key per §5.4: the upstream request id when the entry carries one (namespaced by
    /// provider, since request ids are only unique within one provider's own id space), else a SHA-256
    /// composite over the request's identity - session, turn, provider, requested model, all four token
    /// counts, and a second-truncated timestamp - so a retried publish of the same request collides with
    /// its own earlier write instead of appending a duplicate.
    /// </summary>
    private static string BuildDedupKey(UsageLedgerEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.RequestId))
        {
            return FormattableString.Invariant($"req:{entry.Provider}:{entry.RequestId}");
        }

        var secondTruncated = entry.OccurredAtUtc.UtcDateTime.ToString(CompositeTimestampFormat, CultureInfo.InvariantCulture);
        var composite = FormattableString.Invariant($"""
            {entry.SessionId}|{entry.TurnNumber}|{entry.Provider}|{entry.RequestedModel}|
            {entry.PromptTokens}|{entry.CompletionTokens}|{entry.CacheCreationTokens}|{entry.CacheReadTokens}|
            {secondTruncated}
            """);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
        return FormattableString.Invariant($"composite:{Convert.ToHexString(hash)}");
    }

    /// <summary>Strips CR/LF from a client-controlled session id so it cannot forge additional log lines.</summary>
    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}
