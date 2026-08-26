using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// A minimal <see cref="IOptionsMonitor{TOptions}"/> that never changes - <see cref="OrchestratorArmFactory"/>
/// wires the real <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/> for offline replay, which
/// requires an <see cref="IOptionsMonitor{TOptions}"/> even though the harness never reloads options mid-run.
/// <see cref="OnChange"/> is a no-op subscription (never invoked) rather than throwing, since
/// <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/>'s constructor does not call it, but a future
/// caller subscribing should not be surprised by an exception either.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
public sealed class FrozenOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    /// <summary>Initializes a new instance of the <see cref="FrozenOptionsMonitor{TOptions}"/> class.</summary>
    /// <param name="value">The fixed value <see cref="CurrentValue"/> always returns.</param>
    public FrozenOptionsMonitor(TOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CurrentValue = value;
    }

    /// <inheritdoc />
    public TOptions CurrentValue { get; }

    /// <inheritdoc />
    public TOptions Get(string? name) => CurrentValue;

    /// <inheritdoc />
    /// <returns>A disposable that does nothing - this monitor's value never changes, so no listener is ever invoked.</returns>
    public IDisposable OnChange(Action<TOptions, string?> listener) => NoopDisposable.Instance;

    /// <summary>A shared no-op <see cref="IDisposable"/>, since <see cref="OnChange"/> never needs to unsubscribe anything.</summary>
    private sealed class NoopDisposable : IDisposable
    {
        /// <summary>The single shared instance.</summary>
        public static readonly NoopDisposable Instance = new();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
