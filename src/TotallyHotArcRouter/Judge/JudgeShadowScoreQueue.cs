using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded <see cref="Channel{T}"/>-backed <see cref="IJudgeShadowScoreQueue"/>. <see cref="TryEnqueue"/>
/// uses a non-blocking <see cref="ChannelWriter{T}.TryWrite"/>; when the channel is full the write fails
/// and the job is counted as dropped rather than queued unboundedly. Mirrors
/// <see cref="Quality.Grading.QualityWorkQueue"/> exactly, sized by
/// <see cref="JudgeOptions.QueueCapacity"/> instead of <c>QualityOptions.QueueCapacity</c>.
/// </summary>
public sealed class JudgeShadowScoreQueue : IJudgeShadowScoreQueue
{
    private readonly Channel<JudgeShadowScoringJob> _channel;
    private long _droppedCount;

    /// <summary>Initializes a new instance of the <see cref="JudgeShadowScoreQueue"/> class.</summary>
    /// <param name="options">The judge options carrying the queue capacity.</param>
    public JudgeShadowScoreQueue(IOptions<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = Math.Max(1, options.Value.QueueCapacity);
        // BoundedChannelFullMode.Wait makes TryWrite return false (without blocking) when the channel is
        // full, which is what drop-on-full accounting needs - the Drop* modes would instead return true
        // while silently discarding an item, hiding the drop from DroppedCount.
        _channel = Channel.CreateBounded<JudgeShadowScoringJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc />
    public bool TryEnqueue(JudgeShadowScoringJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_channel.Writer.TryWrite(job))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JudgeShadowScoringJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
