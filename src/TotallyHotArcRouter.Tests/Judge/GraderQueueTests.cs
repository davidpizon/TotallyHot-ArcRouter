using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderQueue"/>'s bounded, non-blocking, drop-on-full behavior - the routing hot path
/// never blocks on judging - its per-grader lanes, and the single capacity bound shared across them.
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

    /// <summary>
    /// The capacity bounds the combined backlog, not each lane: jobs spread across graders still hit the
    /// same ceiling, so worst-case memory does not grow with the number of registered graders.
    /// </summary>
    [Fact]
    public void TryEnqueue_CapacityIsSharedAcrossLanes()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 2 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));
        Assert.True(queue.TryEnqueue(MakeJob("corr-2", GraderKeys.CodeJudge)));

        Assert.False(queue.TryEnqueue(MakeJob("corr-3", GraderKeys.Race)));
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DequeueAllAsync_ReleasesCapacityForTheNextJob()
    {
        var queue = new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));
        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));
        Assert.False(queue.TryEnqueue(MakeJob("corr-2", GraderKeys.CodeJudge)));

        await foreach (var _ in queue.DequeueAllAsync(GraderKeys.Judge, TestContext.Current.CancellationToken)) break;

        Assert.True(queue.TryEnqueue(MakeJob("corr-3", GraderKeys.CodeJudge)));
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
