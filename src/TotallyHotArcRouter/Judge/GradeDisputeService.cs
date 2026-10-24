using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Applies a counted user signal to a grade receipt and, on contradiction, marks it disputed while
/// deleting learning rows in one transaction (issue #304 phase 4).
/// </summary>
public sealed class GradeDisputeService
{
    private readonly RouterMemoryDatabase _database;
    private readonly ILogger<GradeDisputeService> _logger;
    private readonly EmbeddingMemory _memory;

    /// <summary>Initializes a new instance of the <see cref="GradeDisputeService"/> class.</summary>
    /// <param name="database">The router-memory database holding receipts, vectors, and kNN rows.</param>
    /// <param name="memory">In-memory kNN working set; kept in sync when a persisted neighbor is deleted.</param>
    /// <param name="logger">The logger.</param>
    public GradeDisputeService(
        RouterMemoryDatabase database,
        EmbeddingMemory memory,
        ILogger<GradeDisputeService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _memory = memory;
        _logger = logger;
    }

    /// <summary>
    /// Records <paramref name="userSignal"/> on the receipt. When <paramref name="contradicts"/> is true,
    /// sets status to <see cref="GradeStatus.Incomplete"/> with reason <see cref="IncompleteReasons.Disputed"/>,
    /// always deletes the <c>response_vectors</c> row, and deletes the kNN row only when one exists — all in
    /// one transaction keyed by <paramref name="correlationId"/>.
    /// </summary>
    /// <param name="correlationId">The graded turn's correlation id.</param>
    /// <param name="userSignal"><see cref="UserSignals.AgentSwitch"/> or <see cref="UserSignals.Thanks"/>.</param>
    /// <param name="contradicts">Whether the signal contradicts the grade of record.</param>
    /// <returns>A summary of which learning rows were deleted.</returns>
    public DisputeApplyResult ApplySignal(string correlationId, string userSignal, bool contradicts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSignal);

        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var tx = connection.BeginTransaction();

        var deletedVector = false;
        var deletedKnn = false;

        if (contradicts)
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                                     UPDATE grade_receipts
                                     SET grade_status = $status,
                                         incomplete_reason = $reason,
                                         user_signal = $signal
                                     WHERE correlation_id = $correlationId;
                                     """;
                update.Parameters.AddWithValue("$status", nameof(GradeStatus.Incomplete));
                update.Parameters.AddWithValue("$reason", IncompleteReasons.Disputed);
                update.Parameters.AddWithValue("$signal", userSignal);
                update.Parameters.AddWithValue("$correlationId", correlationId);
                update.ExecuteNonQuery();
            }

            using (var deleteVector = connection.CreateCommand())
            {
                deleteVector.Transaction = tx;
                deleteVector.CommandText = "DELETE FROM response_vectors WHERE correlation_id = $correlationId;";
                deleteVector.Parameters.AddWithValue("$correlationId", correlationId);
                deletedVector = deleteVector.ExecuteNonQuery() > 0;
            }

            using (var deleteKnn = connection.CreateCommand())
            {
                deleteKnn.Transaction = tx;
                deleteKnn.CommandText = "DELETE FROM memory_entries WHERE correlation_id = $correlationId;";
                deleteKnn.Parameters.AddWithValue("$correlationId", correlationId);
                deletedKnn = deleteKnn.ExecuteNonQuery() > 0;
            }
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                                 UPDATE grade_receipts
                                 SET user_signal = $signal
                                 WHERE correlation_id = $correlationId;
                                 """;
            update.Parameters.AddWithValue("$signal", userSignal);
            update.Parameters.AddWithValue("$correlationId", correlationId);
            update.ExecuteNonQuery();
        }

        tx.Commit();

        if (deletedKnn)
            _memory.RemoveByCorrelationId(correlationId);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                message:
                "Applied user signal {UserSignal} for correlation {CorrelationId}; disputed={Disputed}; response_vector_deleted={VectorDeleted}; knn_deleted={KnnDeleted}.",
                userSignal,
                correlationId,
                contradicts,
                deletedVector,
                deletedKnn);

        return new DisputeApplyResult(
            CorrelationId: correlationId,
            UserSignal: userSignal,
            Disputed: contradicts,
            ResponseVectorDeleted: deletedVector,
            KnnDeleted: deletedKnn);
    }
}

/// <summary>Outcome of <see cref="GradeDisputeService.ApplySignal"/>.</summary>
/// <param name="CorrelationId">The receipt that was updated.</param>
/// <param name="UserSignal">The signal written onto the receipt.</param>
/// <param name="Disputed">Whether the receipt was marked disputed.</param>
/// <param name="ResponseVectorDeleted">Whether a <c>response_vectors</c> row was removed.</param>
/// <param name="KnnDeleted">Whether a <c>memory_entries</c> row was removed.</param>
public sealed record DisputeApplyResult(
    string CorrelationId,
    string UserSignal,
    bool Disputed,
    bool ResponseVectorDeleted,
    bool KnnDeleted);
