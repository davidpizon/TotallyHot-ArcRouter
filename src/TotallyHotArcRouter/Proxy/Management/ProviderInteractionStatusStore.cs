using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The outcome of the most recent admin-initiated interaction with one provider - "refresh from
/// endpoint", an explicit capability scan, or model discovery. Deliberately narrower than the
/// <see cref="TotallyHot.ArcRouter.Proxy.CircuitBreaker"/>'s live-traffic health: this reflects only
/// operator-triggered actions against the management API, not the outcome of forwarded requests.
/// </summary>
/// <param name="Ok">Whether the interaction succeeded.</param>
/// <param name="Operation">A short label for the interaction (e.g. <c>"Refresh from endpoint"</c>).</param>
/// <param name="Message">A human-readable failure reason, or <see langword="null"/> when <paramref name="Ok"/> is set.</param>
/// <param name="AtUtc">When the interaction completed.</param>
public sealed record ProviderInteractionStatus(bool Ok, string Operation, string? Message, DateTimeOffset AtUtc);

/// <summary>
/// Tracks the outcome of the most recent admin-initiated interaction with each provider, so the GUI can
/// show a persistent warning on a provider whose last "Refresh from endpoint" (or capability scan, or
/// discovery) failed - e.g. an expired API key - even though the request that triggered it still returns
/// <c>200 OK</c> with the (unchanged) provider list.
/// </summary>
public interface IProviderInteractionStatusStore
{
    /// <summary>Records that an interaction with <paramref name="providerKey"/> succeeded, clearing any prior failure.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="operation">A short label for the interaction that succeeded.</param>
    void RecordSuccess(string providerKey, string operation);

    /// <summary>Records that an interaction with <paramref name="providerKey"/> failed.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="operation">A short label for the interaction that failed.</param>
    /// <param name="message">A human-readable reason.</param>
    void RecordFailure(string providerKey, string operation, string message);

    /// <summary>Reads back the most recent interaction outcome for <paramref name="providerKey"/>, or <see langword="null"/> if none has been recorded.</summary>
    /// <param name="providerKey">The provider key.</param>
    ProviderInteractionStatus? Get(string providerKey);

    /// <summary>Clears any recorded outcome for <paramref name="providerKey"/> - called when the provider is removed, so a later re-added provider of the same key starts clean.</summary>
    /// <param name="providerKey">The provider key.</param>
    void Remove(string providerKey);
}

/// <inheritdoc cref="IProviderInteractionStatusStore" />
public sealed class ProviderInteractionStatusStore : IProviderInteractionStatusStore
{
    private readonly ConcurrentDictionary<string, ProviderInteractionStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ProviderInteractionStatusStore"/> class.</summary>
    /// <param name="timeProvider">Clock used to timestamp recorded outcomes; defaults to <see cref="TimeProvider.System"/>.</param>
    public ProviderInteractionStatusStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void RecordSuccess(string providerKey, string operation) =>
        _statuses[providerKey] = new ProviderInteractionStatus(true, operation, null, _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public void RecordFailure(string providerKey, string operation, string message) =>
        _statuses[providerKey] = new ProviderInteractionStatus(false, operation, message, _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public ProviderInteractionStatus? Get(string providerKey) =>
        _statuses.TryGetValue(providerKey, out var status) ? status : null;

    /// <inheritdoc />
    public void Remove(string providerKey) => _statuses.TryRemove(providerKey, out _);
}
