using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TranscriptRetentionService.CheckAndPurgeAsync"/> (docs/router/self-organizing-
/// classification-plan.md Phase T1e's retention purge), called directly rather than through
/// <see cref="TranscriptRetentionService.ExecuteAsync"/>'s <see cref="PeriodicTimer"/> loop.
/// </summary>
public class TranscriptRetentionServiceTests
{
    [Fact]
    public async Task CheckAndPurgeAsync_UnderBothLimits_NoDeletes()
    {
        var store = new FakeTranscriptStore(rowCount: 30_000);
        var service = CreateService(store: store, 30, 50_000, true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeTranscriptStore(rowCount: 60_000);
        var service = CreateService(store: store, 30, 50_000, true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        // Should delete by overage and age
        Assert.True(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
        Assert.Equal(10_000, actual: store.LastDeleteOldestArgument);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_Disabled_NoOp()
    {
        var store = new FakeTranscriptStore(rowCount: 100_000);
        var service = CreateService(store: store, 30, 50_000, false);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: store.DeleteOldestCount);
        Assert.Equal(0, actual: store.DeleteBeforeCount);
    }

    private static TranscriptRetentionService CreateService(
        ITranscriptStore store,
        int retentionDays,
        int maxRows,
        bool enabled)
    {
        return new TranscriptRetentionService(
            logger: NullLogger<TranscriptRetentionService>.Instance,
            transcriptStore: store,
            options: Options.Create(new TranscriptOptions
            {
                Enabled = enabled,
                RetentionDays = retentionDays,
                MaxRows = maxRows
            }));
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        private readonly int _rowCount;

        public FakeTranscriptStore(int rowCount)
        {
            _rowCount = rowCount;
        }

        public int DeleteOldestCount { get; private set; }
        public int LastDeleteOldestArgument { get; private set; }
        public int DeleteBeforeCount { get; private set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_rowCount);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            DeleteOldestCount++;
            LastDeleteOldestArgument = count;
            return Task.FromResult(count);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            DeleteBeforeCount++;
            return Task.FromResult(1000);
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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