using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Extraction;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="QualityRescanService.SweepAsync"/> - the sweep that grades saved transcript rows
/// rather than in-flight responses - called directly rather than through the
/// <see cref="PeriodicTimer"/> loop, matching <see cref="EmbeddingBackfillServiceTests"/>'s shape.
/// </summary>
/// <remarks>
/// The load-bearing assertions here are the two that protect invariants a wrong implementation would
/// break invisibly: that a row is stamped exactly once per scorer version (so the bounded oldest-first
/// sweep makes progress instead of re-grading the same head of the queue forever), and that the prompt
/// actually reaches the grader (the whole reason the rescan reads saved data).
/// </remarks>
public class QualityRescanServiceTests
{
    [Fact]
    public async Task SweepAsync_NoPendingRows_GradesNothing()
    {
        var store = new FakeStore();
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: grader.CallCount);
        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task SweepAsync_GradesTheSavedRowAndStampsTheScoreAndVersion()
    {
        var store = new FakeStore(pending: [7], record: MakeRecord(7));
        var grader = new RecordingGrader(score: 0.75);
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: grader.CallCount);
        var mark = Assert.Single(store.Marks);
        Assert.Equal(7, actual: mark.TranscriptId);
        Assert.Equal(expected: "v-test", actual: mark.ScorerVersion);
        Assert.Equal(0.75, actual: mark.Score!.Value, 6);
    }

    [Fact]
    public async Task SweepAsync_PassesTheSavedPromptToTheGrader()
    {
        // The reason the rescan reads saved data at all: the live ingress consumed the prompt for
        // dimension inference and dropped it, leaving both graders judging an answer without its question.
        var store = new FakeStore(pending: [7], record: MakeRecord(7) with { PromptText = "sort a list in place" });
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: "sort a list in place", actual: grader.LastRequest!.Prompt);
    }

    [Fact]
    public async Task SweepAsync_NullPromptText_PassesEmptyRatherThanFailing()
    {
        var store = new FakeStore(pending: [7], record: MakeRecord(7) with { PromptText = null });
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: string.Empty, actual: grader.LastRequest!.Prompt);
    }

    [Fact]
    public async Task SweepAsync_RecoversTheSessionIdFromTheCorrelationId()
    {
        // ProxyMiddleware builds every correlation id as $"{sessionId}:{turnNumber}", and
        // request_transcripts stores no session id of its own, so the prefix is recovered rather than
        // invented. The split is on the last colon because a session id may contain one and a turn
        // number may not.
        var store = new FakeStore(pending: [7], record: MakeRecord(7) with { CorrelationId = "sess:with:colons:12" });
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: "sess:with:colons", actual: grader.LastRequest!.SessionId);
        Assert.Equal(expected: "sess:with:colons:12", actual: grader.LastRequest.CorrelationId);
    }

    [Fact]
    public async Task SweepAsync_ResponseWithNoCodeBlock_StampsWithNullScoreSoTheRowStopsBeingRetried()
    {
        // Leaving an ungradable row unstamped would return it at the head of every subsequent sweep;
        // because the sweep is bounded and ordered oldest-first, a run of prose-only rows would then
        // consume every batch and no gradable row would ever be reached.
        var store = new FakeStore(pending: [7], record: MakeRecord(7) with { ResponseText = "no fenced code here" });
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: grader.CallCount);
        var mark = Assert.Single(store.Marks);
        Assert.Equal(expected: "v-test", actual: mark.ScorerVersion);
        Assert.Null(mark.Score);
    }

    [Fact]
    public async Task SweepAsync_MissingRow_StampsNothing()
    {
        // Selected on `response_text IS NOT NULL`, so a null read means retention deleted the row between
        // the select and this read. Stamping an id that no longer exists would be a silent no-op write.
        var store = new FakeStore(pending: [7], null);
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: grader.CallCount);
        Assert.Empty(store.Marks);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task SweepAsync_AnyGateOff_IsANoOp(bool rescanEnabled, bool captureEnabled, bool qualityEnabled)
    {
        var store = new FakeStore(pending: [7], record: MakeRecord(7));
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader, rescanEnabled: rescanEnabled,
            captureEnabled: captureEnabled, qualityEnabled: qualityEnabled);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: grader.CallCount);
        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task SweepAsync_RequestsOnlyTheConfiguredBatchSize()
    {
        var store = new FakeStore(pending: [7], record: MakeRecord(7));
        var grader = new RecordingGrader();
        var service = CreateService(store: store, grader: grader, batchSize: 25);

        await service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(25, actual: store.LastRequestedLimit);
        Assert.Equal(expected: "v-test", actual: store.LastRequestedScorerVersion);
    }

    private static QualityRescanService CreateService(
        ITranscriptStore store,
        IQualityGrader grader,
        bool rescanEnabled = true,
        bool captureEnabled = true,
        bool qualityEnabled = true,
        int batchSize = 100)
    {
        var qualityOptions = new QualityOptions { Enabled = qualityEnabled, ScorerVersion = "v-test" };

        return new QualityRescanService(
            logger: NullLogger<QualityRescanService>.Instance,
            transcriptStore: store,
            extractor: new CodeBlockSignalExtractor(
                dimensionInferrer: new KeywordDimensionInferrer(),
                options: Options.Create(qualityOptions),
                logger: NullLogger<CodeBlockSignalExtractor>.Instance),
            grader: grader,
            transcriptOptions: Options.Create(new TranscriptOptions
            {
                Enabled = captureEnabled,
                EnableQualityRescan = rescanEnabled,
                QualityRescanBatchSize = batchSize
            }),
            qualityOptions: Options.Create(qualityOptions));
    }

    private static TranscriptRecord MakeRecord(long id)
    {
        return new TranscriptRecord(
            Id: id,
            CorrelationId: "sess-1:3",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "auto",
            RoutedModel: "kimi-k2.5",
            Dimension: "code_generation",
            Difficulty: "medium",
            Language: "python",
            false,
            PromptText: "write a sort",
            ResponseText: "```python\ndef s(x):\n    return sorted(x)\n```",
            null,
            0.001m,
            false,
            0.95,
            10,
            20,
            null);
    }

    /// <summary>Records what the service asked for and what it stamped back.</summary>
    private sealed class FakeStore(IReadOnlyList<long>? pending = null, TranscriptRecord? record = null)
        : ITranscriptStore
    {
        private readonly List<(long TranscriptId, string ScorerVersion, double? Score)> _marks = [];

        public IReadOnlyList<(long TranscriptId, string ScorerVersion, double? Score)> Marks => _marks;

        public int LastRequestedLimit { get; private set; }

        public string? LastRequestedScorerVersion { get; private set; }

        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            LastRequestedLimit = limit;
            LastRequestedScorerVersion = scorerVersion;
            return Task.FromResult(pending ?? []);
        }

        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            _marks.Add((transcriptId, scorerVersion, score));
            return Task.CompletedTask;
        }

        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(record);
        }

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

        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
    }

    /// <summary>Captures the request the service built, so the prompt plumbing can be asserted.</summary>
    private sealed class RecordingGrader(double score = 0.5) : IQualityGrader
    {
        public int CallCount { get; private set; }

        public QualityRequest? LastRequest { get; private set; }

        public Task<QualityResult> GradeAsync(QualityRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            return Task.FromResult(new QualityResult
            {
                RequestCorrelationId = request.CorrelationId,
                SessionId = request.SessionId,
                Dimension = request.Dimension,
                Model = request.Model,
                Language = request.Language.ToString(),
                SyntaxValid = true,
                SyntaxAuthoritative = true,
                UnifiedScore = score
            });
        }
    }
}