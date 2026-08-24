using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.False(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_ExceedsMaxRows_DeletesOldestFirst()
    {
        var store = new FakeJudgeShadowScoreStore(rowCount: 60_000);
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: true);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.True(store.DeleteOldestWasCalled);
        Assert.True(store.DeleteBeforeWasCalled);
        Assert.Equal(10_000, store.LastDeleteOldestArgument);
    }

    [Fact]
    public async Task CheckAndPurgeAsync_Disabled_NoOp()
    {
        var store = new FakeJudgeShadowScoreStore(rowCount: 100_000);
        var service = CreateService(store, retentionDays: 30, maxRows: 50_000, enabled: false);

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, store.DeleteOldestCount);
        Assert.Equal(0, store.DeleteBeforeCount);
    }

    private static JudgeShadowScoreRetentionService CreateService(
        IJudgeShadowScoreStore store,
        int retentionDays,
        int maxRows,
        bool enabled)
    {
        return new JudgeShadowScoreRetentionService(
            NullLogger<JudgeShadowScoreRetentionService>.Instance,
            store,
            new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            {
                Enabled = enabled,
                RetentionDays = retentionDays,
                MaxRows = maxRows,
            }));
    }

    private sealed class FakeJudgeShadowScoreStore : IJudgeShadowScoreStore
    {
        private readonly int _rowCount;

        public int DeleteOldestCount { get; private set; }
        public int LastDeleteOldestArgument { get; private set; }
        public int DeleteBeforeCount { get; private set; }
        public bool DeleteOldestWasCalled => DeleteOldestCount > 0;
        public bool DeleteBeforeWasCalled => DeleteBeforeCount > 0;

        public FakeJudgeShadowScoreStore(int rowCount)
        {
            _rowCount = rowCount;
        }

        public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(_rowCount);

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
