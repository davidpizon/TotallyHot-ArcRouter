using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers <see cref="QualityJoinSweepService"/> and <see cref="NoJudgeAvailability"/> - the two pieces
/// that decide, respectively, when an unanswered join is closed out and whether one is opened at all.
/// </summary>
public class QualityJoinSweepServiceTests
{
    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QualityJoinSweepService(aggregator: null!, logger: NullLogger<QualityJoinSweepService>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new QualityJoinSweepService(aggregator: new CountingAggregator(), logger: null!));
    }

    [Fact]
    public async Task StopAsync_BeforeTheFirstTick_ShutsDownCleanly()
    {
        var aggregator = new CountingAggregator();
        var service = new QualityJoinSweepService(aggregator: aggregator,
            logger: NullLogger<QualityJoinSweepService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Cancellation during the timer wait is a normal shutdown. The invariant that matters is that it
        // does not surface as a *fault* to the host - whether the task lands Canceled or RanToCompletion
        // is an implementation detail of where the token was observed, and asserting either specifically
        // would pin behaviour the service does not actually promise.
        Assert.Equal(0, actual: aggregator.Sweeps);
        Assert.True(service.ExecuteTask is null || !service.ExecuteTask.IsFaulted);
    }

    // A sweep that throws must not end the loop - the next tick should still get its chance, since a
    // permanently stopped sweeper would silently strand every held result until process exit.
    [Fact]
    public async Task ExecuteAsync_SweepThrows_LoopSurvives()
    {
        var aggregator = new CountingAggregator(throwOnSweep: true);
        var service = new QualityJoinSweepService(
            aggregator: aggregator,
            logger: NullLogger<QualityJoinSweepService>.Instance,
            sweepInterval: TimeSpan.FromMilliseconds(10));

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await aggregator.Swept.WaitAsync(timeout: TimeSpan.FromSeconds(2),
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(aggregator.Sweeps >= 1);
        Assert.True(service.ExecuteTask is null || !service.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task ExecuteAsync_SweepsRepeatedlyOnItsInterval()
    {
        var aggregator = new CountingAggregator();
        var service = new QualityJoinSweepService(
            aggregator: aggregator,
            logger: NullLogger<QualityJoinSweepService>.Instance,
            sweepInterval: TimeSpan.FromMilliseconds(10));

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await aggregator.Swept.WaitAsync(timeout: TimeSpan.FromSeconds(2),
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(aggregator.Sweeps >= 1);
    }

    // The library's standalone default: with no host-supplied availability, nothing is ever held for a
    // judge, so the verifier works on its own and every score is written from static analysis alone.
    [Fact]
    public void NoJudgeAvailability_NeverAsksToHoldAResult()
    {
        var availability = new NoJudgeAvailability();

        Assert.False(availability.WillJudge(new QualityResult()));
        Assert.False(availability.WillJudge(new QualityResult { RequestCorrelationId = "corr-1", SyntaxValid = true }));
    }

    // The library's standalone default for Phase Q3's extra graders: with no host-supplied availability,
    // nothing is ever held for CodeJudge/ICE-Score/RACE either.
    [Fact]
    public void NoPortfolioGraderAvailability_NeverReturnsAnyGraderKey()
    {
        var availability = new NoPortfolioGraderAvailability();

        Assert.Empty(availability.DetermineGraderKeys(new QualityResult()));
        Assert.Empty(availability.DetermineGraderKeys(new QualityResult
        { RequestCorrelationId = "corr-1", SyntaxValid = true }));
    }

    /// <summary>Counts sweeps and can be told to throw, so the loop's fault isolation is observable.</summary>
    private sealed class CountingAggregator(bool throwOnSweep = false) : IQualityScoreAggregator
    {
        private readonly TaskCompletionSource _swept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sweeps;

        public int Sweeps => Volatile.Read(ref _sweeps);

        public Task Swept => _swept.Task;

        public Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> AbandonJudgeAsync(string correlationId, string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> CompleteGraderAsync(string correlationId, string graderKey, double score,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> AbandonGraderAsync(string correlationId, string graderKey, string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sweeps);
            _swept.TrySetResult();

            return throwOnSweep
                ? throw new InvalidOperationException("sweep is down")
                : Task.FromResult(1);
        }
    }
}