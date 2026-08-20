using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// A minimal <see cref="IOptionsMonitor{TOptions}"/> test double: <see cref="CurrentValue"/> is settable
/// directly, and <see cref="Set"/> invokes every subscriber registered via <see cref="OnChange"/> -
/// standing in for the real options system's change-token machinery
/// (<see cref="TotallyHot.ArcRouter.Router.RouterSettingsReloadToken"/> in production) without needing a
/// live SQLite-backed <c>RouterSettingsStore</c> or a DI container in a unit test.
/// </summary>
/// <typeparam name="TOptions">The options type being monitored.</typeparam>
public sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly List<Action<TOptions, string?>> _listeners = [];

    /// <summary>Initializes a new instance of the <see cref="StaticOptionsMonitor{TOptions}"/> class.</summary>
    /// <param name="initialValue">The value <see cref="CurrentValue"/> starts at.</param>
    public StaticOptionsMonitor(TOptions initialValue)
    {
        ArgumentNullException.ThrowIfNull(initialValue);
        CurrentValue = initialValue;
    }

    /// <inheritdoc />
    public TOptions CurrentValue { get; private set; }

    /// <inheritdoc />
    public TOptions Get(string? name) => CurrentValue;

    /// <inheritdoc />
    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(() => _listeners.Remove(listener));
    }

    /// <summary>Sets a new <see cref="CurrentValue"/> and notifies every registered <see cref="OnChange"/> listener, mirroring a live options reload.</summary>
    /// <param name="value">The new value.</param>
    public void Set(TOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CurrentValue = value;

        foreach (var listener in _listeners.ToArray())
        {
            listener(value, null);
        }
    }

    /// <summary>Removes its owning listener from <see cref="_listeners"/> on disposal, mirroring the real <see cref="IOptionsMonitor{TOptions}.OnChange"/> subscription contract.</summary>
    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}
