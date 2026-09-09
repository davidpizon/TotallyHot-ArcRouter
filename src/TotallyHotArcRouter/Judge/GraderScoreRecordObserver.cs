using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Writes one <see cref="GraderScoreRecord"/> row per grader that actually contributed to a final
/// <see cref="QualityResult"/> (docs/router/grader-reliability-plan.md, Phase Q4) - never one row per
/// request, and never a row for a grader that abstained. Registered unconditionally in
/// <see cref="Router.CompositeRouterScoreObserver"/>'s fan-out, mirroring
/// <see cref="Router.RouterMemoryScoreObserver"/>: it is as cheap as that observer and does not depend on
/// transcript capture being enabled.
/// </summary>
public sealed class GraderScoreRecordObserver : IQualityScoreObserver
{
    private readonly ILogger<GraderScoreRecordObserver> _logger;
    private readonly PendingGraderBackboneCache _pendingGraderBackboneCache;
    private readonly PendingResponseLengthCache _pendingResponseLengthCache;
    private readonly IGraderScoreStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="GraderScoreRecordObserver"/> class.</summary>
    /// <param name="store">Where each grader's row is persisted.</param>
    /// <param name="pendingResponseLengthCache">Supplies the graded response's character length, if captured.</param>
    /// <param name="pendingGraderBackboneCache">Supplies each async grader's resolved backbone model, if any completed.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp each row's <see cref="GraderScoreRecord.CreatedAtUtc"/>; defaults to
    /// <see cref="TimeProvider.System"/>. Overridable for deterministic tests.
    /// </param>
    public GraderScoreRecordObserver(
        IGraderScoreStore store,
        PendingResponseLengthCache pendingResponseLengthCache,
        PendingGraderBackboneCache pendingGraderBackboneCache,
        ILogger<GraderScoreRecordObserver> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pendingResponseLengthCache);
        ArgumentNullException.ThrowIfNull(pendingGraderBackboneCache);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _pendingResponseLengthCache = pendingResponseLengthCache;
        _pendingGraderBackboneCache = pendingGraderBackboneCache;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Quality result has no model attribution; skipping grader-score recording.");
            return;
        }

        // Consumed exactly once here, on the aggregator's single final write per request - not TryPeek,
        // unlike the response-text/prompt caches every grader independently reads while scoring is still in
        // flight. By the time ObserveAsync runs, nothing will look for either value again. Both caches key
        // on a non-empty correlation id, so a result written without one (IQualityScoreAggregator.SubmitAsync's
        // immediate-write path) simply carries no length or backbone rather than probing either cache.
        var hasCorrelationId = !string.IsNullOrEmpty(result.RequestCorrelationId);
        var responseLength = hasCorrelationId &&
            _pendingResponseLengthCache.TryTake(correlationId: result.RequestCorrelationId, length: out var length)
            ? (int?)length
            : null;
        IReadOnlyDictionary<string, string> backboneByGraderKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasCorrelationId)
            _pendingGraderBackboneCache.TryTake(correlationId: result.RequestCorrelationId,
                backboneByGraderKey: out backboneByGraderKey);

        var createdAtUtc = _timeProvider.GetUtcNow();
        var written = 0;

        if (result.AnalysisScore is { } analysisScore)
        {
            await WriteRowAsync(result: result, graderKey: GraderKeys.Analysis, score: analysisScore,
                backboneByGraderKey: backboneByGraderKey, responseLength: responseLength,
                createdAtUtc: createdAtUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
            written++;
        }

        if (result.JudgeScore is { } judgeScore)
        {
            await WriteRowAsync(result: result, graderKey: GraderKeys.Judge, score: judgeScore,
                backboneByGraderKey: backboneByGraderKey, responseLength: responseLength,
                createdAtUtc: createdAtUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
            written++;
        }

        foreach (var (graderKey, score) in result.GraderScores)
        {
            await WriteRowAsync(result: result, graderKey: graderKey, score: score,
                backboneByGraderKey: backboneByGraderKey, responseLength: responseLength,
                createdAtUtc: createdAtUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
            written++;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                message: "Recorded {RowCount} grader-score row(s) for correlation {CorrelationId}.",
                written,
                result.RequestCorrelationId);
    }

    /// <summary>Writes one grader's row, resolving its backbone from the accumulated map when one exists.</summary>
    private Task WriteRowAsync(
        QualityResult result,
        string graderKey,
        double score,
        IReadOnlyDictionary<string, string> backboneByGraderKey,
        int? responseLength,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        backboneByGraderKey.TryGetValue(key: graderKey, value: out var backboneModel);

        return _store.InsertAsync(
            record: new GraderScoreRecord(
                0,
                CorrelationId: result.RequestCorrelationId,
                CreatedAtUtc: createdAtUtc,
                Dimension: result.Dimension,
                Model: result.Model,
                GraderKey: graderKey,
                Score: Math.Clamp(value: score, 0.0, 1.0),
                GraderBackboneModel: backboneModel,
                ResponseLengthChars: responseLength),
            cancellationToken: cancellationToken);
    }
}
