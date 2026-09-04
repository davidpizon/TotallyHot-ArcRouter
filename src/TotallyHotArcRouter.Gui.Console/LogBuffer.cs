namespace TotallyHot.ArcRouter.Gui.Console;

/// <summary>
/// A thread-safe, bounded FIFO buffer of <see cref="LogLineDto"/>s backing the Console tab's
/// viewport. Bounded so a long-running session's log stream can't grow the GUI's memory usage
/// unboundedly; oldest lines are dropped once <see cref="Capacity"/> is exceeded. <see cref="Clear"/>
/// implements the toolbar's "Clear Buffer" action.
/// </summary>
public sealed class LogBuffer
{
    private readonly Queue<LogLineDto> _lines = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LogBuffer"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of lines retained. Must be positive.</param>
    public LogBuffer(int capacity = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: capacity, 0);
        Capacity = capacity;
    }

    /// <summary>Maximum number of lines this buffer retains before dropping the oldest.</summary>
    public int Capacity { get; }

    /// <summary>Appends a line, dropping the oldest line first if the buffer is already at capacity.</summary>
    public void Add(LogLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
        }
    }

    /// <summary>Empties the buffer. Implements the toolbar's "Clear Buffer" action.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }

    /// <summary>A point-in-time, oldest-first snapshot of the buffer's current contents.</summary>
    public IReadOnlyList<LogLineDto> Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }
}