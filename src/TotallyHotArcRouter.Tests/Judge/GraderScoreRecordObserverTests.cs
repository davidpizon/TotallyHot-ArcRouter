using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderScoreRecordObserver"/>: one row per populated axis, never a row for an axis the
/// grader abstained on, and the cached response length/backbone attached when present
/// (docs/router/grader-reliability-plan.md, Phase Q4).
/// </summary>
public class GraderScoreRecordObserverTests
{
    [Fact]
    public async Task ObserveAsync_AllThreeAxesPopulated_WritesOneRowPerAxis()
    {
        var store = new FakeGraderScoreStore();
        var observer = CreateObserver(store);
        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "bug_fixing",
            Model = "claude-opus-4-6",
            AnalysisScore = 0.9,
            JudgeScore = 0.8,
            GraderScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [GraderKeys.CodeJudge] = 0.7
            }
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(3, actual: store.Inserted.Count);
        Assert.Contains(store.Inserted, r => r.GraderKey == GraderKeys.Analysis && r.Score == 0.9);
        Assert.Contains(store.Inserted, r => r.GraderKey == GraderKeys.Judge && r.Score == 0.8);
        Assert.Contains(store.Inserted, r => r.GraderKey == GraderKeys.CodeJudge && r.Score == 0.7);
        Assert.All(store.Inserted, r => Assert.Equal(expected: "corr-1", actual: r.CorrelationId));
        Assert.All(store.Inserted, r => Assert.Equal(expected: "bug_fixing", actual: r.Dimension));
        Assert.All(store.Inserted, r => Assert.Equal(expected: "claude-opus-4-6", actual: r.Model));
    }

    [Fact]
    public async Task ObserveAsync_OnlyAnalysisPopulated_WritesExactlyOneRow()
    {
        var store = new FakeGraderScoreStore();
        var observer = CreateObserver(store);
        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "bug_fixing",
            Model = "claude-opus-4-6",
            AnalysisScore = 0.5
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Inserted);
        Assert.Equal(expected: GraderKeys.Analysis, actual: row.GraderKey);
    }

    [Fact]
    public async Task ObserveAsync_NoModel_WritesNothing()
    {
        var store = new FakeGraderScoreStore();
        var observer = CreateObserver(store);
        var result = new QualityResult { RequestCorrelationId = "corr-1", AnalysisScore = 0.5 };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Empty(store.Inserted);
    }

    [Fact]
    public async Task ObserveAsync_ResponseLengthCached_AttachesItToEveryRow()
    {
        var store = new FakeGraderScoreStore();
        var lengthCache = new PendingResponseLengthCache(Options.Create(new JudgeOptions()));
        lengthCache.Set(correlationId: "corr-1", length: 256);
        var observer = CreateObserver(store, lengthCache: lengthCache);
        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "bug_fixing",
            Model = "claude-opus-4-6",
            AnalysisScore = 0.5,
            JudgeScore = 0.6
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.All(store.Inserted, r => Assert.Equal(256, actual: r.ResponseLengthChars));
    }

    [Fact]
    public async Task ObserveAsync_BackboneCached_AttachesItToTheMatchingGraderOnly()
    {
        var store = new FakeGraderScoreStore();
        var backboneCache = new PendingGraderBackboneCache(Options.Create(new JudgeOptions()));
        backboneCache.Set(correlationId: "corr-1", graderKey: GraderKeys.Judge, backboneModel: "judge-backbone");
        var observer = CreateObserver(store, backboneCache: backboneCache);
        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "bug_fixing",
            Model = "claude-opus-4-6",
            AnalysisScore = 0.5,
            JudgeScore = 0.6
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var analysisRow = store.Inserted.Single(r => r.GraderKey == GraderKeys.Analysis);
        var judgeRow = store.Inserted.Single(r => r.GraderKey == GraderKeys.Judge);
        Assert.Null(analysisRow.GraderBackboneModel);
        Assert.Equal(expected: "judge-backbone", actual: judgeRow.GraderBackboneModel);
    }

    [Fact]
    public async Task ObserveAsync_NoCorrelationId_DoesNotThrowAndWritesWithoutLengthOrBackbone()
    {
        var store = new FakeGraderScoreStore();
        var observer = CreateObserver(store);
        var result = new QualityResult
        {
            RequestCorrelationId = string.Empty,
            Dimension = "bug_fixing",
            Model = "claude-opus-4-6",
            AnalysisScore = 0.5
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Inserted);
        Assert.Null(row.ResponseLengthChars);
        Assert.Null(row.GraderBackboneModel);
    }

    private static GraderScoreRecordObserver CreateObserver(
        FakeGraderScoreStore store,
        PendingResponseLengthCache? lengthCache = null,
        PendingGraderBackboneCache? backboneCache = null)
    {
        return new GraderScoreRecordObserver(
            store: store,
            pendingResponseLengthCache: lengthCache ?? new PendingResponseLengthCache(Options.Create(new JudgeOptions())),
            pendingGraderBackboneCache: backboneCache ?? new PendingGraderBackboneCache(Options.Create(new JudgeOptions())),
            logger: NullLogger<GraderScoreRecordObserver>.Instance);
    }

    private sealed class FakeGraderScoreStore : IGraderScoreStore
    {
        public List<GraderScoreRecord> Inserted { get; } = [];

        public Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default)
        {
            Inserted.Add(record);
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Inserted.Count);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GraderScoreRecord>>(Inserted);
        }
    }
}
