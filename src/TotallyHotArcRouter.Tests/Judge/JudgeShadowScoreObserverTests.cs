using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var observer = new JudgeShadowScoreObserver(queue, EnabledJudge(), NullLogger<JudgeShadowScoreObserver>.Instance);

        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var jobs = new List<JudgeShadowScoringJob>();
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            jobs.Add(job);
            break;
        }

        var enqueued = Assert.Single(jobs);
        Assert.Equal("corr-1", enqueued.CorrelationId);
        Assert.Equal("algorithm", enqueued.Dimension);
        Assert.Equal("claude-opus-4-6", enqueued.Model);
        Assert.Equal(0.75, enqueued.StaticScore);
    }

    [Fact]
    public async Task ObserveAsync_NoCorrelationId_EnqueuesNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var observer = new JudgeShadowScoreObserver(queue, EnabledJudge(), NullLogger<JudgeShadowScoreObserver>.Instance);

        var result = new QualityResult { RequestCorrelationId = string.Empty, Model = "claude-opus-4-6" };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public async Task ObserveAsync_ChannelFull_ReturnsPromptlyWithoutThrowing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        var observer = new JudgeShadowScoreObserver(queue, EnabledJudge(), NullLogger<JudgeShadowScoreObserver>.Instance);

        await observer.ObserveAsync(MakeResult("corr-1"), TestContext.Current.CancellationToken);

        // The second observation finds the single-capacity channel already full; it must shed the job
        // rather than block the caller or throw.
        var completed = observer.ObserveAsync(MakeResult("corr-2"), TestContext.Current.CancellationToken);
        Assert.True(completed.IsCompletedSuccessfully);

        await completed;
        Assert.Equal(1, queue.DroppedCount);
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
        var observer = new JudgeShadowScoreObserver(queue, options, NullLogger<JudgeShadowScoreObserver>.Instance);

        options.Set(new JudgeOptions { Enabled = false });
        await observer.ObserveAsync(MakeResult("corr-1"), TestContext.Current.CancellationToken);

        // Neither queued nor counted as shed - the observation was never attempted at all.
        Assert.Equal(0, queue.DroppedCount);
        Assert.False(queue.DequeueAllAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken)
            .MoveNextAsync()
            .IsCompleted);
    }

    private static QualityResult MakeResult(string correlationId) => new()
    {
        RequestCorrelationId = correlationId,
        Dimension = "algorithm",
        Model = "claude-opus-4-6",
        UnifiedScore = 0.5,
    };

    /// <summary>An options monitor reporting the judge as switched on - the precondition for every test above bar the disabled one.</summary>
    private static StaticOptionsMonitor<JudgeOptions> EnabledJudge() => new(new JudgeOptions { Enabled = true });
}
