using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderScoreRetentionService.CheckAndPurgeAsync"/>
/// (docs/router/grader-reliability-plan.md's retention purge), mirroring
/// <see cref="JudgeShadowScoreRetentionServiceTests"/> - except there is no "disabled" case, since this
/// service has no enabled gate.
/// </summary>
public class GraderScoreRetentionServiceTests
{
    [Fact]
    public async Task CheckAndPurgeAsync_UnderBothLimits_NoOverageDeletes()
    {
        var store = new FakeGraderScoreStore(rowCount: 100_000);
        var service = CreateService(store: store, retentionDays: 30, maxRows: 200_000);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeGraderScoreStore(rowCount: 250_000);
        var service = CreateService(store: store, retentionDays: 30, maxRows: 200_000);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.True(store.DeleteOldestWasCalled);
        Assert.Equal(50_000, actual: store.LastDeleteOldestArgument);
    }

    private static GraderScoreRetentionService CreateService(IGraderScoreStore store, int retentionDays, int maxRows)
    {
        return new GraderScoreRetentionService(
            logger: NullLogger<GraderScoreRetentionService>.Instance,
            store: store,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            {
                GraderScoreRetentionDays = retentionDays,
                GraderScoreMaxRows = maxRows
            }));
    }

    private sealed class FakeGraderScoreStore(int rowCount) : IGraderScoreStore
    {
        private int DeleteOldestCount { get; set; }
        public int LastDeleteOldestArgument { get; private set; }
        private int DeleteBeforeCount { get; set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

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

        public Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GraderScoreRecord>>([]);
        }
    }
}
