using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Transcripts;

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
        var service = CreateService(store: store, embeddingClient: embeddingClient, memoryStore: memoryStore, true,
            true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: embeddingClient.EmbedCallCount);
        Assert.Equal(0, actual: memoryStore.AppendCallCount);
    }

    [Fact]
    public async Task CheckAndBackfillAsync_SuccessfulBackfill_CreatesMemoryEntryAndLinksTranscript()
    {
        var transcriptId = 42L;
        var embedding = new[] { 0.1f, 0.2f, 0.3f };
        var transcript = new TranscriptRecord(
            Id: transcriptId,
            CorrelationId: "test-correlation-123",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "model-a",
            RoutedModel: "model-b",
            Dimension: "code_quality",
            Difficulty: "medium",
            Language: "en",
            false,
            PromptText: "Test prompt",
            ResponseText: "Test response",
            0.85,
            0.01m,
            false,
            0.95,
            100,
            200,
            null);

        var store = new FakeTranscriptStore(
            unembeddedIds: [transcriptId],
            getTranscriptResult: transcript);
        var embeddingClient = new FakeEmbeddingClient(embedding);
        var memoryStore = new FakeMemoryEntryStore();
        var service = CreateService(store: store, embeddingClient: embeddingClient, memoryStore: memoryStore, true,
            true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: embeddingClient.EmbedCallCount);
        Assert.Equal(expected: "Test prompt", actual: embeddingClient.LastEmbeddedText);
        Assert.Equal(1, actual: memoryStore.AppendCallCount);
        Assert.Single(store.LinkedEntries);
        Assert.True(store.LinkedEntries.TryGetValue(key: transcriptId, value: out var linkedMemoryId));
        Assert.NotNull(memoryStore.LastPersistedEntry);
        Assert.Equal(expected: memoryStore.LastPersistedEntry.Id, actual: linkedMemoryId);
    }

    [Fact]
    public async Task CheckAndBackfillAsync_BackfillDisabled_NoOp()
    {
        var store = new FakeTranscriptStore(unembeddedIds: [1, 2, 3]);
        var embeddingClient = new FakeEmbeddingClient();
        var memoryStore = new FakeMemoryEntryStore();
        var service = CreateService(store: store, embeddingClient: embeddingClient, memoryStore: memoryStore, false,
            true);

        await service.CheckAndBackfillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: embeddingClient.EmbedCallCount);
        Assert.Equal(0, actual: memoryStore.AppendCallCount);
    }

    private static EmbeddingBackfillService CreateService(
        ITranscriptStore store,
        IEmbeddingClient embeddingClient,
        IMemoryEntryStore memoryStore,
        bool backfillEnabled,
        bool captureEnabled)
    {
        return new EmbeddingBackfillService(
            logger: NullLogger<EmbeddingBackfillService>.Instance,
            transcriptStore: store,
            embeddingClient: embeddingClient,
            memoryEntryStore: memoryStore,
            transcriptOptions: Options.Create(new TranscriptOptions
            { EnableEmbeddingBackfill = backfillEnabled, Enabled = captureEnabled }));
    }

    private sealed class FakeTranscriptStore(IReadOnlyList<long> unembeddedIds, TranscriptRecord? getTranscriptResult = null) : ITranscriptStore
    {

        public Dictionary<long, long> LinkedEntries { get; } = [];

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
            return Task.FromResult(unembeddedIds);
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(getTranscriptResult);
        }

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            LinkedEntries[transcriptId] = memoryEntryId;
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeEmbeddingClient(float[]? embedding = null) : IEmbeddingClient
    {
        private readonly float[] _defaultEmbedding = embedding ?? [0.1f, 0.2f, 0.3f];

        public int EmbedCallCount { get; private set; }
        public string? LastEmbeddedText { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            EmbedCallCount++;
            LastEmbeddedText = text;
            return Task.FromResult(new EmbeddingResult(Vector: _defaultEmbedding, 10));
        }
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        public int AppendCallCount { get; private set; }
        public MemoryEntry? LastPersistedEntry { get; private set; }

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

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

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}