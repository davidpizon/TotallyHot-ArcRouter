namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock and timers move only when a test says so, so an expiry that takes five
/// minutes in production is tested without waiting. Unlike <see cref="ManualTimeProvider"/> it supports
/// <see cref="CreateTimer"/>, which <c>Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)</c> needs.
/// </summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<SteppingTimer> _timers = [];
    private DateTimeOffset _now;

    /// <summary>Initializes a new instance of the <see cref="SteppingTimeProvider"/> class at <paramref name="start"/>.</summary>
    /// <param name="start">The clock's first reading.</param>
    public SteppingTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Moves the clock forward and fires every live timer that has come due.</summary>
    /// <param name="by">How far to move the clock.</param>
    public void Advance(TimeSpan by)
    {
        List<SteppingTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => !timer.Disposed && timer.DueAt <= _now)];
            foreach (var timer in due) timer.Disposed = true;
        }

        foreach (var timer in due) timer.Callback(timer.State);
    }

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new SteppingTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class SteppingTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset DueAt { get; } = dueAt;

        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
