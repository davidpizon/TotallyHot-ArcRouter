using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeShadowScoreQueue"/>'s bounded, non-blocking, drop-on-full behavior
/// (docs/router/geval-shadow-scoring-plan.md's ground rule: the routing hot path never blocks on
/// judging).
/// </summary>
public class JudgeShadowScoreQueueTests
{
    [Fact]
    public void TryEnqueue_UnderCapacity_Succeeds()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 2 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ReturnsFalseAndCountsAsDropped_WithoutThrowing()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 1 }));

        Assert.True(queue.TryEnqueue(MakeJob("corr-1")));

        var enqueued = queue.TryEnqueue(MakeJob("corr-2"));

        Assert.False(enqueued);
        Assert.Equal(1, actual: queue.DroppedCount);
    }

    [Fact]
    public async Task DequeueAllAsync_YieldsEnqueuedJobsInOrder()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
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

    private static JudgeShadowScoringJob MakeJob(string correlationId)
    {
        return new JudgeShadowScoringJob(CorrelationId: correlationId, Dimension: "algorithm", Model: "model-a", 0.5);
    }
}