using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderQueue"/>'s bounded, non-blocking, drop-on-full behavior: the routing hot path
/// never blocks on judging.
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
        await foreach (var job in queue.DequeueAllAsync(cts.Token))
        {
            results.Add(job.CorrelationId);
            if (results.Count == 2) break;
        }

        Assert.Equal(expected: ["corr-1", "corr-2"], actual: results);
    }

    private static GraderScoringJob MakeJob(string correlationId)
    {
        return new GraderScoringJob(CorrelationId: correlationId, GraderKey: GraderKeys.Judge,
            Dimension: "algorithm", Model: "model-a", StaticScore: 0.5, SyntaxAuthoritative: true);
    }
}
