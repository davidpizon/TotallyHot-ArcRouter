using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeShadowScoreRetentionService.CheckAndPurgeAsync"/>
/// (docs/router/geval-shadow-scoring-plan.md §1d's retention purge), mirroring
/// <see cref="TotallyHot.ArcRouter.Tests.Transcripts.TranscriptRetentionServiceTests"/> exactly.
/// </summary>
public class JudgeShadowScoreRetentionServiceTests
{
    [Fact]
    public async Task CheckAndPurgeAsync_UnderBothLimits_NoDeletes()
    {
        var store = new FakeJudgeShadowScoreStore(rowCount: 30_000);
        var service = CreateService(store: store, 30, 50_000, true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeJudgeShadowScoreStore(rowCount: 60_000);
        var service = CreateService(store: store, 30, 50_000, true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.True(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
        Assert.Equal(10_000, actual: store.LastDeleteOldestArgument);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_Disabled_NoOp()
    {
        var store = new FakeJudgeShadowScoreStore(rowCount: 100_000);
        var service = CreateService(store: store, 30, 50_000, false);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: store.DeleteOldestCount);
        Assert.Equal(0, actual: store.DeleteBeforeCount);
    }

    private static JudgeShadowScoreRetentionService CreateService(
        IJudgeShadowScoreStore store,
        int retentionDays,
        int maxRows,
        bool enabled)
    {
        return new JudgeShadowScoreRetentionService(
            logger: NullLogger<JudgeShadowScoreRetentionService>.Instance,
            store: store,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            {
                Enabled = enabled,
                RetentionDays = retentionDays,
                MaxRows = maxRows
            }));
    }

    private sealed class FakeJudgeShadowScoreStore(int rowCount) : IJudgeShadowScoreStore
    {

        public int DeleteOldestCount { get; private set; }
        public int LastDeleteOldestArgument { get; private set; }
        public int DeleteBeforeCount { get; private set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default)
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
    }
}