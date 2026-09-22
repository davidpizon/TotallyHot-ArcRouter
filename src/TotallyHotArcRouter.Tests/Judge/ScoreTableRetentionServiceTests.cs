using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="ScoreTableRetentionService.CheckAndPurgeAsync"/> for both table policies: the
/// shadow-judge instance no-ops while disabled; the grader-scores instance always purges.
/// </summary>
public class ScoreTableRetentionServiceTests
{
    [Fact]
    public async Task CheckAndPurgeAsync_UnderBothLimits_NoOverageDeletes()
    {
        var store = new FakeRetentionStore(rowCount: 30_000);
        var service = CreateShadowService(store, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeRetentionStore(rowCount: 60_000);
        var service = CreateShadowService(store, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.True(store.DeleteOldestWasCalled);
        Assert.Equal(10_000, actual: store.LastDeleteOldestArgument);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_Disabled_NoOp()
    {
        var store = new FakeRetentionStore(rowCount: 100_000);
        var service = CreateShadowService(store, maxRows: 50_000, enabled: false);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: store.DeleteOldestCount);
        Assert.Equal(0, actual: store.DeleteBeforeCount);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_GraderScoresAlwaysRuns()
    {
        var store = new FakeRetentionStore(rowCount: 250_000);
        var service = new ScoreTableRetentionService(
            logger: NullLogger<ScoreTableRetentionService>.Instance,
            store: store,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            { Enabled = false, GraderScoreMaxRows = 200_000, GraderScoreRetentionDays = 30 }),
            isEnabled: static _ => true,
            maxRows: static o => o.GraderScoreMaxRows,
            retentionDays: static o => o.GraderScoreRetentionDays,
            tableLabel: "Grader-score");

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.True(store.DeleteOldestWasCalled);
        Assert.Equal(50_000, actual: store.LastDeleteOldestArgument);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    private static ScoreTableRetentionService CreateShadowService(FakeRetentionStore store, int maxRows, bool enabled)
    {
        return new ScoreTableRetentionService(
            logger: NullLogger<ScoreTableRetentionService>.Instance,
            store: store,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            {
                Enabled = enabled,
                RetentionDays = 30,
                MaxRows = maxRows
            }),
            isEnabled: static o => o.Enabled,
            maxRows: static o => o.MaxRows,
            retentionDays: static o => o.RetentionDays,
            tableLabel: "Shadow judge");
    }

    private sealed class FakeRetentionStore(int rowCount) : IScoreRetentionStore
    {
        public int DeleteOldestCount { get; private set; }
        public int LastDeleteOldestArgument { get; private set; }
        public int DeleteBeforeCount { get; private set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(rowCount);
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
    }
}
