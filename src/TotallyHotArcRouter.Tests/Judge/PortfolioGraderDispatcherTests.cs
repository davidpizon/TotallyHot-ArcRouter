using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PortfolioGraderDispatcher"/> (Phase Q3): it must enqueue one job per requested,
/// enabled grader key, accurately report which keys it actually dispatched, and never enqueue a key that
/// was not requested or is currently disabled.
/// </summary>
public class PortfolioGraderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_AllThreeEnabledAndRequested_EnqueuesAllThreeAndAcceptsAllThree()
    {
        var queue = new PortfolioGraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue, AllEnabled());

        var accepted = await dispatcher.DispatchAsync(
            result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race }, actual: accepted);

        var jobs = new List<PortfolioGraderJob>();
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            jobs.Add(job);
            if (jobs.Count == 3) break;
        }

        Assert.Equal(3, jobs.Count);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.CodeJudge);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.IceScore);
        Assert.Contains(jobs, j => j.GraderKey == GraderKeys.Race);
        Assert.All(jobs, j => Assert.Equal(expected: "corr-1", actual: j.CorrelationId));
    }

    [Fact]
    public async Task DispatchAsync_OnlyOneRequested_EnqueuesOnlyThatOne()
    {
        var queue = new PortfolioGraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue, AllEnabled());

        var accepted = await dispatcher.DispatchAsync(
            result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.CodeJudge }, actual: accepted);
    }

    [Fact]
    public async Task DispatchAsync_RequestedButDisabled_EnqueuesNothing()
    {
        var queue = new PortfolioGraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var options = new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
        { CodeJudgeEnabled = false, IceScoreEnabled = false, RaceEnabled = false });
        var dispatcher = CreateDispatcher(queue, options);

        var accepted = await dispatcher.DispatchAsync(
            result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_NoCorrelationId_EnqueuesNothing()
    {
        var queue = new PortfolioGraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        var dispatcher = CreateDispatcher(queue, AllEnabled());

        var result = new QualityResult { RequestCorrelationId = string.Empty };
        var accepted = await dispatcher.DispatchAsync(result: result,
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DispatchAsync_ChannelFull_DeclinesThatOneKeyPromptly()
    {
        var queue = new PortfolioGraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        var dispatcher = CreateDispatcher(queue, AllEnabled());

        await dispatcher.DispatchAsync(result: MakeResult("corr-1"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.CodeJudge },
            cancellationToken: TestContext.Current.CancellationToken);

        var accepted = await dispatcher.DispatchAsync(result: MakeResult("corr-2"),
            pendingGraderKeys: new HashSet<string> { GraderKeys.IceScore },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    private static PortfolioGraderDispatcher CreateDispatcher(PortfolioGraderQueue queue,
        StaticOptionsMonitor<PortfolioGraderOptions> options)
    {
        return new PortfolioGraderDispatcher(queue: queue, options: options,
            logger: NullLogger<PortfolioGraderDispatcher>.Instance);
    }

    private static StaticOptionsMonitor<PortfolioGraderOptions> AllEnabled()
    {
        return new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
        { CodeJudgeEnabled = true, IceScoreEnabled = true, RaceEnabled = true });
    }

    private static QualityResult MakeResult(string correlationId)
    {
        return new QualityResult { RequestCorrelationId = correlationId, Dimension = "algorithm" };
    }
}
