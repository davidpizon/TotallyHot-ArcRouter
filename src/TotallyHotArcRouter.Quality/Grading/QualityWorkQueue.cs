using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// A bounded <see cref="Channel{T}"/>-backed work queue. <see cref="TryEnqueue"/> uses a non-blocking
/// <see cref="ChannelWriter{T}.TryWrite"/>; when the channel is full the write fails and the request is
/// counted as dropped rather than queued unboundedly.
/// </summary>
public sealed class QualityWorkQueue : IQualityQueue
{
    private readonly Channel<QualityRequest> _channel;
    private long _droppedCount;

    /// <summary>Initializes a new instance of the <see cref="QualityWorkQueue"/> class.</summary>
    /// <param name="options">The quality options carrying the queue capacity.</param>
    public QualityWorkQueue(IOptions<QualityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = Math.Max(1, val2: options.Value.QueueCapacity);
        // Wait mode makes TryWrite return false (without blocking) when the channel is full, which is what
        // drop-on-full accounting needs. The Drop* modes would instead return true while silently discarding
        // an item, hiding the drop from DroppedCount.
        _channel = Channel.CreateBounded<QualityRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc/>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc/>
    public bool TryEnqueue(QualityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_channel.Writer.TryWrite(request)) return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<QualityRequest> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}