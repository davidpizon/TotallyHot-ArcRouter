using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded <see cref="Channel{T}"/>-backed <see cref="IPortfolioGraderQueue"/>, sized by
/// <see cref="JudgeOptions.QueueCapacity"/> - the same bound the shadow judge's own queue uses, since both
/// exist for the same reason (never let a slow grader backbone back-pressure the routing hot path). Mirrors
/// <see cref="JudgeShadowScoreQueue"/> exactly.
/// </summary>
public sealed class PortfolioGraderQueue : IPortfolioGraderQueue
{
    private readonly Channel<PortfolioGraderJob> _channel;
    private long _droppedCount;

    /// <summary>Initializes a new instance of the <see cref="PortfolioGraderQueue"/> class.</summary>
    /// <param name="options">The judge options carrying the shared queue capacity.</param>
    public PortfolioGraderQueue(IOptions<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = Math.Max(1, val2: options.Value.QueueCapacity);
        _channel = Channel.CreateBounded<PortfolioGraderJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc/>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc/>
    public bool TryEnqueue(PortfolioGraderJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_channel.Writer.TryWrite(job)) return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<PortfolioGraderJob> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
