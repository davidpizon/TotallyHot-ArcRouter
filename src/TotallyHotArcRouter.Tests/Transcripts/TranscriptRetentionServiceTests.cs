using TotallyHot.ArcRouter.Transcripts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeTranscriptStore(rowCount: 60_000);
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        // Should delete by overage and age
        Assert.True(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
        Assert.Equal(10_000, store.LastDeleteOldestArgument);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_Disabled_NoOp()
    {
        var store = new FakeTranscriptStore(rowCount: 100_000);
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: false);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, store.DeleteOldestCount);
        Assert.Equal(0, store.DeleteBeforeCount);
    }

    private static TranscriptRetentionService CreateService(
        ITranscriptStore store,
        int retentionDays,
        int maxRows,
        bool enabled)
    {
        return new TranscriptRetentionService(
            NullLogger<TranscriptRetentionService>.Instance,
            store,
            Options.Create(new TranscriptOptions
            {
                Enabled = enabled,
                RetentionDays = retentionDays,
                MaxRows = maxRows
            }));
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        private readonly int _rowCount;

        public int DeleteOldestCount { get; private set; }
        public int LastDeleteOldestArgument { get; private set; }
        public int DeleteBeforeCount { get; private set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public FakeTranscriptStore(int rowCount)
        {
            _rowCount = rowCount;
        }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_rowCount);

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
    }
}
