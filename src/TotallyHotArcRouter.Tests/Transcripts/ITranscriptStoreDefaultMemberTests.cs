using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="ITranscriptStore.ListSessionsAsync"/>'s default implementation: it exists so the
/// pre-existing <see cref="ITranscriptStore"/> test fakes that don't override session listing don't need
/// a matching stub, and returns an empty list rather than throwing.
/// </summary>
public class ITranscriptStoreDefaultMemberTests
{
    [Fact]
    public async Task ListSessionsAsync_NotOverridden_ReturnsEmptyList()
    {
        ITranscriptStore store = new StoreWithoutSessionListingOverride();

        var result = await store.ListSessionsAsync(limit: 10, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    private sealed class StoreWithoutSessionListingOverride : ITranscriptStore
    {
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<long?>(null);
        }

        public Task UpdateOutcomeAsync(string correlationId, double? score, bool isJudgeScored = false,
            CancellationToken cancellationToken = default)
        {
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
    }
}
