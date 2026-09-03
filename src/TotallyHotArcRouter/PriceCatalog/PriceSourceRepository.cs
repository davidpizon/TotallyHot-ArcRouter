using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One source's row in <c>aggregator_sources</c>, joined with a summary of the price rows it owns. Feed
/// metadata only - no price values, per D5.
/// </summary>
/// <param name="Name">The source's registry name.</param>
/// <param name="Enabled">Whether the source is polled and served (D6).</param>
/// <param name="PriorityScore">Rank used to arbitrate contested cells. [FUTURE: multi-source]</param>
/// <param name="PriceCount">How many price rows this source owns; 0 for a seeded source that never polled.</param>
public sealed record PriceSourceState(
    string Name,
    bool Enabled,
    int PriorityScore,
    int PriceCount);

/// <summary>
/// Thin ADO.NET wrapper over the source-toggle CRUD tables (<c>aggregator_sources</c>). Split out of the
/// former monolithic <c>PriceCatalogRepository</c> (docs/router/code-smell-refactoring-plan.md M3) - the
/// other five concerns that repository once mixed together each now have their own type in this namespace.
/// </summary>
public sealed class PriceSourceRepository : PriceCatalogRepositoryBase
{
    /// <summary>Initializes a new instance of the <see cref="PriceSourceRepository"/> class.</summary>
    /// <param name="database">The catalog database.</param>
    public PriceSourceRepository(PriceCatalogDatabase database)
        : base(database)
    {
    }

    /// <summary>
    /// Counts price rows whose newest update is within <paramref name="maxAge"/> of now, ignoring rows
    /// owned by a disabled source. Used by the startup check's zero-fresh-prices condition (D4).
    /// </summary>
    /// <remarks>
    /// The <c>enabled</c> join is what keeps this honest with D6: a disabled source's rows are excluded from
    /// the resolved catalog, so counting them here would let a source the operator switched off suppress the
    /// zero-fresh-prices Error - reporting a healthy feed while nothing usable is being served.
    /// </remarks>
    public int CountFreshPrices(TimeSpan maxAge)
    {
        var cutoff = FormatCutoff(maxAge);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM model_prices mp
                              JOIN aggregator_sources s ON s.source_id = mp.aggregator_source_id
                              WHERE mp.last_updated_utc >= $cutoff
                                AND s.enabled = 1;
                              """;
        command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff);

        return Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns one row per source in <c>aggregator_sources</c>, with its toggle state, rank, and how many
    /// price rows it owns. Backs the Governance → Price Sources panel.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> filtered by <c>enabled</c>: this describes the sources themselves, and a
    /// disabled source still has to be listed - otherwise it could never be switched back on. That is the
    /// opposite of <c>PriceRepository.GetFreshPrice</c>, which describes the resolved catalog and must
    /// exclude it.
    /// <para>
    /// Carries no price values, only a count - see D5. The GUI renders this straight onto the wire, so a
    /// price column added here would leave the machine.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PriceSourceState> GetSourceStates()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // LEFT JOIN, not JOIN: a seeded source that has never polled owns no price rows, and it must still
        // appear (with a count of 0) so the operator can see it exists and toggle it.
        command.CommandText = """
                              SELECT s.source_name, s.enabled, s.priority_score,
                                     COUNT(mp.price_id)
                              FROM aggregator_sources s
                              LEFT JOIN model_prices mp ON mp.aggregator_source_id = s.source_id
                              GROUP BY s.source_id, s.source_name, s.enabled, s.priority_score
                              ORDER BY s.priority_score DESC, s.source_name;
                              """;

        var states = new List<PriceSourceState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            states.Add(new PriceSourceState(
                Name: reader.GetString(0),
                Enabled: reader.GetInt32(1) != 0,
                PriorityScore: reader.GetInt32(2),
                PriceCount: reader.GetInt32(3)));

        return states;
    }

    /// <summary>
    /// Sets a source's <c>enabled</c> flag.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no source of that name exists, so a caller can answer NotFound rather
    /// than silently reporting success for a toggle that changed nothing.
    /// </returns>
    public bool SetSourceEnabled(string sourceName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE aggregator_sources SET enabled = $enabled WHERE source_name = $name;";
        command.Parameters.AddWithValue(parameterName: "$enabled", value: enabled ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$name", value: sourceName);

        var rowsAffected = command.ExecuteNonQuery();
        if (rowsAffected > 0)
        {
            transaction.Commit();
            return true;
        }

        transaction.Rollback();
        return false;
    }

    /// <summary>
    /// Rewrites every source's <c>priority_score</c> from <paramref name="namesInPriorityOrder"/>'s position:
    /// the first name gets the highest score, the last gets the lowest, contiguously. Backs the Governance
    /// panel's reorder control.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> without writing anything when <paramref name="namesInPriorityOrder"/> does not
    /// name every existing source exactly once - a partial or padded reorder would leave an unlisted source's
    /// rank stale relative to ranks that just moved, which is a worse failure than simply rejecting the call.
    /// </returns>
    public bool ReorderSources(IReadOnlyList<string> namesInPriorityOrder)
    {
        ArgumentNullException.ThrowIfNull(namesInPriorityOrder);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = "SELECT source_name FROM aggregator_sources;";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = existingCommand.ExecuteReader())
            {
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            var requested = new HashSet<string>(collection: namesInPriorityOrder,
                comparer: StringComparer.OrdinalIgnoreCase);
            if (requested.Count != namesInPriorityOrder.Count || !requested.SetEquals(existing))
            {
                // Rolling back an otherwise-empty transaction is a no-op; explicit for readability at the
                // point every other return path here does write.
                transaction.Rollback();
                return false;
            }
        }

        // Highest priority first in the list, so the first entry gets the largest score. count-1 down to 0
        // keeps scores contiguous and non-negative regardless of what the previous ranking happened to be -
        // a fresh, deterministic ordering rather than shuffling old values.
        for (var index = 0; index < namesInPriorityOrder.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE aggregator_sources SET priority_score = $priority WHERE source_name = $name;";
            update.Parameters.AddWithValue(parameterName: "$priority", value: namesInPriorityOrder.Count - index - 1);
            update.Parameters.AddWithValue(parameterName: "$name", value: namesInPriorityOrder[index]);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }
}