using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeShadowScoreDispatcher"/> (docs/router/geval-shadow-scoring-plan.md §1c;
/// docs/router/judge-join-deadlock-fix-plan.md): it must enqueue and return immediately, never call
/// anything else inline, shed - not block - when the channel is full, and accurately report which grader
/// keys it actually dispatched.
/// </summary>
public class JudgeShadowScoreDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ValidResult_EnqueuesOneJobAndAcceptsJudgeKey()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75
        };

        var accepted = await dispatcher.DispatchAsync(result: result,
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.Judge }, actual: accepted);

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
    public async Task DispatchAsync_JudgeKeyNotRequested_EnqueuesNothingAndAcceptsNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string>(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_NoCorrelationId_EnqueuesNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        var result = new QualityResult { RequestCorrelationId = string.Empty, Model = "claude-opus-4-6" };

        var accepted = await dispatcher.DispatchAsync(result: result,
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_ChannelFull_DeclinesPromptlyWithoutThrowing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: EnabledJudge(),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        // The second dispatch finds the single-capacity channel already full; it must decline rather
        // than block the caller or throw.
        var completed = dispatcher.DispatchAsync(result: MakeResult("corr-2"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.IsCompletedSuccessfully);

        var accepted = await completed;
        Assert.Empty(accepted);
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    /// <summary>
    /// docs/router/geval-shadow-scoring-plan.md: the dispatcher is started unconditionally and gates
    /// itself, so a live toggle-off has to stop enqueuing without a restart. This is the test that would
    /// fail if the gate were ever moved back to construction time.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_JudgeDisabledAfterConstruction_EnqueuesNothing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var options = EnabledJudge();
        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: options,
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        options.Set(new JudgeOptions { Enabled = false });
        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        // Neither queued, counted as shed, nor accepted - the dispatch was never attempted at all.
        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
        Assert.False(queue.DequeueAllAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken)
            .MoveNextAsync()
            .IsCompleted);
    }

    /// <summary>
    /// Carried over from the deleted <c>JudgeShadowScoringExitCriteriaTests</c>: unlike the invariant that
    /// test file was named for (shadow scoring must never influence memory, superseded once the judge was
    /// promoted - docs/router/judge-join-deadlock-fix-plan.md), this assertion is still live. The judge
    /// being switched off must mean no queue or cache activity at all, whichever seam starts the dispatch.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_JudgeDisabled_NoQueueActivity()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = new JudgeShadowScoreDispatcher(
            queue: queue,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = false }),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
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
    /// An options monitor reporting the judge as switched on - the precondition for every test above bar the
    /// disabled ones.
    /// </summary>
    private static StaticOptionsMonitor<JudgeOptions> EnabledJudge()
    {
        return new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true });
    }
}
