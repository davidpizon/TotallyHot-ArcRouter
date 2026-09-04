using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TranscriptScoreObserver"/> - the second of the transcript store's two writes
/// (docs/router/self-organizing-classification-plan.md Phase T1b), backing onto a fake
/// <see cref="ITranscriptStore"/> so the observer's own dispatch logic is tested without SQLite.
/// </summary>
public class TranscriptScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_ResultWithCorrelationId_BackfillsClampedScore()
    {
        var store = new FakeTranscriptStore();
        var observer = new TranscriptScoreObserver(store: store, logger: NullLogger<TranscriptScoreObserver>.Instance);
        var result = new QualityResult { RequestCorrelationId = "corr-1", Model = "kimi-k2.5", UnifiedScore = 0.83 };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        var (correlationId, score) = Assert.Single(store.Updates);
        Assert.Equal(expected: "corr-1", actual: correlationId);
        Assert.Equal(0.83, actual: score);
    }

    [Fact]
    public async Task ObserveAsync_ScoreOutsideUnitInterval_IsClamped()
    {
        var store = new FakeTranscriptStore();
        var observer = new TranscriptScoreObserver(store: store, logger: NullLogger<TranscriptScoreObserver>.Instance);
        var result = new QualityResult { RequestCorrelationId = "corr-1", Model = "kimi-k2.5", UnifiedScore = 5.0 };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.0, actual: Assert.Single(store.Updates).Score);
    }

    [Fact]
    public async Task ObserveAsync_EmptyCorrelationId_DoesNotCallTheStore()
    {
        var store = new FakeTranscriptStore();
        var observer = new TranscriptScoreObserver(store: store, logger: NullLogger<TranscriptScoreObserver>.Instance);
        var result = new QualityResult { RequestCorrelationId = string.Empty, Model = "kimi-k2.5", UnifiedScore = 0.5 };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Updates);
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        private readonly List<(string CorrelationId, double? Score)> _updates = [];

        public IReadOnlyList<(string CorrelationId, double? Score)> Updates => _updates;

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<long?>(1);
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            _updates.Add((correlationId, score));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TranscriptRecord?>(null);
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ModelTokenAverage>>(
                new Dictionary<string, ModelTokenAverage>());
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}