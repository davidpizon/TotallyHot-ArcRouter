using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderDispatcher"/>: it enqueues one job per requested, enabled LLM grader key,
/// snapshots G2 fields for the judge, sheds rather than blocking when the channel is full, and reports
/// which keys it actually dispatched.
/// </summary>
public class GraderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_JudgeRequestedAndEnabled_EnqueuesOneJobWithG2Fields()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue);

        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
            SyntaxAuthoritative = true
        };

        var accepted = await dispatcher.DispatchAsync(result: result,
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.Judge }, actual: accepted);

        var job = await DequeueOneAsync(queue);
        Assert.Equal(expected: "corr-1", actual: job.CorrelationId);
        Assert.Equal(expected: GraderKeys.Judge, actual: job.GraderKey);
        Assert.Equal(expected: "algorithm", actual: job.Dimension);
        Assert.Equal(expected: "claude-opus-4-6", actual: job.Model);
        Assert.Equal(0.75, actual: job.StaticScore);
        Assert.True(job.SyntaxAuthoritative);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchAsync_SnapshotsSyntaxAuthoritativeFromTheResult(bool syntaxAuthoritative)
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue);

        await dispatcher.DispatchAsync(
            result: new QualityResult
            {
                RequestCorrelationId = "corr-1",
                Dimension = "algorithm",
                Model = "claude-opus-4-6",
                UnifiedScore = 0.75,
                SyntaxAuthoritative = syntaxAuthoritative
            },
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: syntaxAuthoritative, actual: (await DequeueOneAsync(queue)).SyntaxAuthoritative);
    }

    [Fact]
    public async Task DispatchAsync_AllThreePortfolioEnabledAndRequested_EnqueuesAllThree()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue);

        var accepted = await dispatcher.DispatchAsync(
            result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race }, actual: accepted);

        var jobs = new List<GraderScoringJob>();
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            jobs.Add(job);
            if (jobs.Count == 3) break;
        }

        Assert.Equal(3, jobs.Count);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.CodeJudge);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.IceScore);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.Race);
    }

    [Fact]
    public async Task DispatchAsync_JudgeDisabled_EnqueuesNothing()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue, judgeEnabled: false);

        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_NoCorrelationId_EnqueuesNothing()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue);

        var accepted = await dispatcher.DispatchAsync(
            result: new QualityResult { RequestCorrelationId = string.Empty, Model = "claude-opus-4-6" },
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge, GraderKeys.CodeJudge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_ChannelFull_DeclinesPromptlyWithoutThrowing()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        var dispatcher = CreateDispatcher(queue);

        await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        var completed = dispatcher.DispatchAsync(result: MakeResult("corr-2"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.IsCompletedSuccessfully);

        var accepted = await completed;
        Assert.Empty(accepted);
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_JudgeDisabledAfterConstruction_EnqueuesNothing()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var judgeOptions = new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true });
        var dispatcher = CreateDispatcher(queue, judgeOptions);

        judgeOptions.Set(new JudgeOptions { Enabled = false });
        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    private static GraderDispatcher CreateDispatcher(GraderQueue queue, bool judgeEnabled = true)
    {
        return CreateDispatcher(queue, new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = judgeEnabled }));
    }

    private static GraderDispatcher CreateDispatcher(GraderQueue queue, StaticOptionsMonitor<JudgeOptions> judgeOptions)
    {
        return new GraderDispatcher(
            queue: queue,
            judgeOptions: judgeOptions,
            portfolioOptions: new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
            { CodeJudgeEnabled = true, IceScoreEnabled = true, RaceEnabled = true }),
            logger: NullLogger<GraderDispatcher>.Instance);
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

    private static async Task<GraderScoringJob> DequeueOneAsync(IGraderQueue queue)
    {
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken)) return job;
        throw new InvalidOperationException("queue was empty");
    }
}
