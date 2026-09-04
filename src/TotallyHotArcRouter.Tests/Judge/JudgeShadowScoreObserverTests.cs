using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeShadowScoreObserver"/> (docs/router/geval-shadow-scoring-plan.md §1c): it must
/// enqueue and return immediately, never call anything else inline, and shed - not block - when the
/// channel is full.
/// </summary>
public class JudgeShadowScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_ValidResult_EnqueuesOneJobWithSnapshottedFields()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var observer = new JudgeShadowScoreObserver(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreObserver>.Instance);

        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75
        };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        var jobs = new List<JudgeShadowScoringJob>();
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            jobs.Add(job);
            break;
        }

        var enqueued = Assert.Single(jobs);
        Assert.Equal(expected: "corr-1", actual: enqueued.CorrelationId);
        Assert.Equal(expected: "algorithm", actual: enqueued.Dimension);
        Assert.Equal(expected: "claude-opus-4-6", actual: enqueued.Model);
        Assert.Equal(0.75, actual: enqueued.StaticScore);
    }

    [Fact]
    public async Task ObserveAsync_NoCorrelationId_EnqueuesNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var observer = new JudgeShadowScoreObserver(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreObserver>.Instance);

        var result = new QualityResult { RequestCorrelationId = string.Empty, Model = "claude-opus-4-6" };

        await observer.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task ObserveAsync_ChannelFull_ReturnsPromptlyWithoutThrowing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        var observer = new JudgeShadowScoreObserver(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreObserver>.Instance);

        await observer.ObserveAsync(result: MakeResult("corr-1"),
            cancellationToken: TestContext.Current.CancellationToken);

        // The second observation finds the single-capacity channel already full; it must shed the job
        // rather than block the caller or throw.
        var completed = observer.ObserveAsync(result: MakeResult("corr-2"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.IsCompletedSuccessfully);

        await completed;
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    /// <summary>
    /// docs/router/geval-shadow-scoring-plan.md: the observer joins the fan-out unconditionally and gates
    /// itself, so a live toggle-off has to stop enqueuing without a restart. This is the test that would
    /// fail if the gate were ever moved back to construction time.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_JudgeDisabledAfterConstruction_EnqueuesNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var options = EnabledJudge();
        var observer = new JudgeShadowScoreObserver(queue: queue, options: options,
            logger: NullLogger<JudgeShadowScoreObserver>.Instance);

        options.Set(new JudgeOptions { Enabled = false });
        await observer.ObserveAsync(result: MakeResult("corr-1"),
            cancellationToken: TestContext.Current.CancellationToken);

        // Neither queued nor counted as shed - the observation was never attempted at all.
        Assert.Equal(0, actual: queue.DroppedCount);
        Assert.False(queue.DequeueAllAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken)
            .MoveNextAsync()
            .IsCompleted);
    }

    private static QualityResult MakeResult(string correlationId)
    {
        return new QualityResult
        {
            RequestCorrelationId = correlationId,
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.5
        };
    }

    /// <summary>
    /// An options monitor reporting the judge as switched on - the precondition for every test above bar the disabled
    /// one.
    /// </summary>
    private static StaticOptionsMonitor<JudgeOptions> EnabledJudge()
    {
        return new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true });
    }
}