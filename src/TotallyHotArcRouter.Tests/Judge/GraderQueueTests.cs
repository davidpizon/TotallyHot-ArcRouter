using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderQueue"/>'s bounded, non-blocking, drop-on-full behavior - the routing hot path
/// never blocks on judging - and its per-grader lanes, which keep one grader's backlog from shedding or
/// delaying another's jobs.
/// </summary>
public class GraderQueueTests
{
    [Fact]
    public void TryEnqueue_UnderCapacity_Succeeds()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 2 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ReturnsFalseAndCountsAsDropped_WithoutThrowing()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));

        var enqueued = queue.TryEnqueue(MakeJob("corr-2"));

        Assert.False(enqueued);
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DequeueAllAsync_YieldsEnqueuedJobsInOrder()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        queue.TryEnqueue(MakeJob("corr-1"));
        queue.TryEnqueue(MakeJob("corr-2"));

        using var cts = new CancellationTokenSource();
        var results = new List<string>();
        await foreach (var job in queue.DequeueAllAsync(GraderKeys.Judge, cts.Token))
        {
            results.Add(job.CorrelationId);
            if (results.Count == 2) break;
        }

        Assert.Equal(expected: ["corr-1", "corr-2"], actual: results);
    }

    [Fact]
    public void TryEnqueue_OneLaneFull_StillAcceptsAnotherGradersJob()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));
        Assert.False(queue.TryEnqueue(MakeJob("corr-2")));

        Assert.True(queue.TryEnqueue(MakeJob("corr-3", GraderKeys.CodeJudge)));
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DequeueAllAsync_YieldsOnlyThatGradersJobs()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        queue.TryEnqueue(MakeJob("corr-1"));
        queue.TryEnqueue(MakeJob("corr-2", GraderKeys.CodeJudge));

        await foreach (var job in queue.DequeueAllAsync(GraderKeys.CodeJudge, TestContext.Current.CancellationToken))
        {
            Assert.Equal(expected: "corr-2", actual: job.CorrelationId);
            break;
        }
    }

    private static GraderScoringJob MakeJob(string correlationId, string graderKey = GraderKeys.Judge)
    {
        return new GraderScoringJob(CorrelationId: correlationId, GraderKey: graderKey,
            Dimension: "algorithm", Model: "model-a", StaticScore: 0.5, SyntaxAuthoritative: true);
    }
}
