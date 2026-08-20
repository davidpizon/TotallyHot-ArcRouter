using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Transcripts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="EmbeddingBackfillService.CheckAndBackfillAsync"/> (docs/router/self-organizing-
/// classification-plan.md Phase T1d's embedding backfill), called directly rather than through
/// <see cref="EmbeddingBackfillService.ExecuteAsync"/>'s <see cref="PeriodicTimer"/> loop.
/// </summary>
public class EmbeddingBackfillServiceTests
{
    [Fact]
    public async Task CheckAndBackfillAsync_NoUnembeddedRows_ReturnsEarly()
    {
        var store = new FakeTranscriptStore(unembeddedIds: []);
        var embeddingClient = new FakeEmbeddingClient();
        var memoryStore = new FakeMemoryEntryStore();
        var service = CreateService(store, embeddingClient, memoryStore, backfillEnabled: true, captureEnabled: true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, embeddingClient.EmbedCallCount);
        Assert.Equal(0, memoryStore.AppendCallCount);
    }

    [Fact]
    public async Task CheckAndBackfillAsync_SuccessfulBackfill_CreatesMemoryEntryAndLinksTranscript()
    {
        var transcriptId = 42L;
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var transcript = new TranscriptRecord(
            Id: transcriptId,
            CorrelationId: "test-correlation-123",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "model-a",
            RoutedModel: "model-b",
            Dimension: "code_quality",
            Difficulty: "medium",
            Language: "en",
            IsUtility: false,
            PromptText: "Test prompt",
            ResponseText: "Test response",
            Score: 0.85,
            Cost: 0.01m,
            IsExploratory: false,
            Propensity: 0.95,
            InputTokens: 100,
            OutputTokens: 200,
            MemoryEntryId: null);

        var store = new FakeTranscriptStore(
            unembeddedIds: [transcriptId],
            getTranscriptResult: transcript);
        var embeddingClient = new FakeEmbeddingClient(embedding);
        var memoryStore = new FakeMemoryEntryStore();
        var service = CreateService(store, embeddingClient, memoryStore, backfillEnabled: true, captureEnabled: true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, embeddingClient.EmbedCallCount);
        Assert.Equal("Test prompt", embeddingClient.LastEmbeddedText);
        Assert.Equal(1, memoryStore.AppendCallCount);
        Assert.Single(store.LinkedEntries);
        Assert.True(store.LinkedEntries.TryGetValue(transcriptId, out var linkedMemoryId));
        Assert.NotNull(memoryStore.LastPersistedEntry);
        Assert.Equal(memoryStore.LastPersistedEntry.Id, linkedMemoryId);
    }

    [Fact]
    public async Task CheckAndBackfillAsync_BackfillDisabled_NoOp()
    {
        var store = new FakeTranscriptStore(unembeddedIds: [1, 2, 3]);
        var embeddingClient = new FakeEmbeddingClient();
        var memoryStore = new FakeMemoryEntryStore();
        var service = CreateService(store, embeddingClient, memoryStore, backfillEnabled: false, captureEnabled: true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, embeddingClient.EmbedCallCount);
        Assert.Equal(0, memoryStore.AppendCallCount);
    }

    private static EmbeddingBackfillService CreateService(
        ITranscriptStore store,
        IEmbeddingClient embeddingClient,
        IMemoryEntryStore memoryStore,
        bool backfillEnabled,
        bool captureEnabled)
    {
        return new EmbeddingBackfillService(
            NullLogger<EmbeddingBackfillService>.Instance,
            store,
            embeddingClient,
            memoryStore,
            Options.Create(new TranscriptOptions { EnableEmbeddingBackfill = backfillEnabled, Enabled = captureEnabled }),
            Options.Create(new RoutingOptions { EmbeddingBudgetMs = 250 }));
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        private readonly IReadOnlyList<long> _unembeddedIds;
        private readonly TranscriptRecord? _getTranscriptResult;

        public Dictionary<long, long> LinkedEntries { get; } = new();

        public FakeTranscriptStore(IReadOnlyList<long> unembeddedIds, TranscriptRecord? getTranscriptResult = null)
        {
            _unembeddedIds = unembeddedIds;
            _getTranscriptResult = getTranscriptResult;
        }

        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(_unembeddedIds);

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_getTranscriptResult);

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default)
        {
            LinkedEntries[transcriptId] = memoryEntryId;
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        private readonly float[] _defaultEmbedding;

        public int EmbedCallCount { get; private set; }
        public string? LastEmbeddedText { get; private set; }

        public FakeEmbeddingClient(float[]? embedding = null)
        {
            _defaultEmbedding = embedding ?? [0.1f, 0.2f, 0.3f];
        }

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            EmbedCallCount++;
            LastEmbeddedText = text;
            return Task.FromResult(new EmbeddingResult(_defaultEmbedding, TokenCount: 10));
        }
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        public int AppendCallCount { get; private set; }
        public MemoryEntry? LastPersistedEntry { get; private set; }

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            AppendCallCount++;
            var persisted = new MemoryEntry(
                Id: AppendCallCount,
                TaskEmbedding: entry.TaskEmbedding,
                ChosenModel: entry.ChosenModel,
                Score: entry.Score,
                Cost: entry.Cost,
                VerifierTrace: entry.VerifierTrace,
                CreatedAtUtc: entry.CreatedAtUtc,
                IsExploratory: entry.IsExploratory,
                Propensity: entry.Propensity);
            LastPersistedEntry = persisted;
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
