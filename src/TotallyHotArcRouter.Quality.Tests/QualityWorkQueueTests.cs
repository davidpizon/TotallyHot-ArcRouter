using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the bounded work queue's enqueue and drop-on-full behavior.</summary>
public class QualityWorkQueueTests
{
    private static QualityRequest Request() =>
        new("code", CodeLanguage.Python, "write code", "code_generation", "gpt-5.4", "corr", "sess");

    [Fact]
    public void TryEnqueue_WithinCapacity_Succeeds()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 4 }));

        Assert.True(queue.TryEnqueue(Request()));
        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public void TryEnqueue_BeyondCapacity_DropsAndCounts()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 2 }));

        Assert.True(queue.TryEnqueue(Request()));
        Assert.True(queue.TryEnqueue(Request()));
        Assert.False(queue.TryEnqueue(Request()));
        Assert.False(queue.TryEnqueue(Request()));

        Assert.Equal(2, queue.DroppedCount);
    }

    [Fact]
    public void TryEnqueue_UnderConcurrentFlood_NeverThrowsAndAccountsForEveryItem()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 16 }));
        const int total = 4000;
        var accepted = 0;

        Parallel.For(0, total, _ =>
        {
            if (queue.TryEnqueue(Request()))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        // Every item is either accepted or counted as dropped - nothing is lost or double-counted, and no
        // enqueue blocked or threw under load (the proxy hot path stays non-blocking).
        Assert.Equal(total, accepted + queue.DroppedCount);
        Assert.True(accepted <= 16);
    }

    [Fact]
    public async Task DequeueAllAsync_YieldsEnqueuedItems()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 4 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());

        var drained = 0;
        await foreach (var _ in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            drained++;
            if (drained == 2)
            {
                break;
            }
        }

        Assert.Equal(2, drained);
    }
}

