using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Extensible classification of why a <c>LiveTraffic</c> failure was recorded
/// (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md), so the Providers
/// tab can word its warning precisely instead of a bare "failed."
/// </summary>
public enum ProviderInteractionKind
{
    /// <summary>No specific classification - the default for the <c>AdminAction</c> track, which predates this enum.</summary>
    None = 0,

    /// <summary>The provider's account is out of credits/quota/billing.</summary>
    OutOfCredits,
}

/// <summary>
/// The outcome of one recorded interaction with a provider - either an admin-initiated action
/// ("refresh from endpoint", an explicit capability scan, or model discovery) or, since
/// docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md, a classified
/// live-traffic outcome from the hot request path. See <see cref="IProviderInteractionStatusStore"/>'s
/// remarks for which track each comes from.
/// </summary>
/// <param name="Ok">Whether the interaction succeeded.</param>
/// <param name="Operation">A short label for the interaction (e.g. <c>"Refresh from endpoint"</c> or <c>"Live traffic"</c>).</param>
/// <param name="Message">A human-readable failure reason, or <see langword="null"/> when <paramref name="Ok"/> is set.</param>
/// <param name="AtUtc">When the interaction completed.</param>
/// <param name="Kind">
/// The classification of a live-traffic failure (<see cref="ProviderInteractionKind.OutOfCredits"/>), or
/// <see cref="ProviderInteractionKind.None"/> for every <c>AdminAction</c>-track record, which predates
/// this classification.
/// </param>
public sealed record ProviderInteractionStatus(
    bool Ok,
    string Operation,
    string? Message,
    DateTimeOffset AtUtc,
    ProviderInteractionKind Kind = ProviderInteractionKind.None);

/// <summary>
/// Tracks the outcome of the most recent interaction with each provider, in two independently-maintained
/// tracks so a live-traffic success can never erase an admin-recorded failure or vice versa
/// (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md):
/// <list type="bullet">
/// <item>
/// <description>
/// <b>AdminAction</b> (<see cref="RecordSuccess"/>/<see cref="RecordFailure"/>/<see cref="Get"/>): the
/// most recent operator-triggered management-API action - "refresh from endpoint", a capability scan, or
/// discovery. Unchanged since before ADR-0004.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>LiveTraffic</b> (<see cref="RecordLiveTrafficSuccess"/>/<see cref="RecordLiveTrafficFailure"/>/
/// <see cref="GetLiveTraffic"/>): the most recent classified outcome from the hot request path (e.g. an
/// out-of-credits response), independent of the circuit breaker's own live-traffic health tracking - this
/// store exists to make that state visible on the Providers tab, not to duplicate circuit-breaker logic.
/// </description>
/// </item>
/// </list>
/// so the GUI can show up to two distinct warnings on the same provider at once, answering two different
/// questions: does live traffic work, and did the last admin action work.
/// </summary>
public interface IProviderInteractionStatusStore
{
    /// <summary>Records that an admin-triggered interaction with <paramref name="providerKey"/> succeeded, clearing any prior AdminAction failure.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="operation">A short label for the interaction that succeeded.</param>
    void RecordSuccess(string providerKey, string operation);

    /// <summary>Records that an admin-triggered interaction with <paramref name="providerKey"/> failed.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="operation">A short label for the interaction that failed.</param>
    /// <param name="message">A human-readable reason.</param>
    void RecordFailure(string providerKey, string operation, string message);

    /// <summary>Reads back the most recent AdminAction outcome for <paramref name="providerKey"/>, or <see langword="null"/> if none has been recorded.</summary>
    /// <param name="providerKey">The provider key.</param>
    ProviderInteractionStatus? Get(string providerKey);

    /// <summary>Records a live-traffic failure classified from the hot request path.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="kind">The classification of the failure.</param>
    /// <param name="message">A human-readable reason, drawn from the upstream response.</param>
    void RecordLiveTrafficFailure(string providerKey, ProviderInteractionKind kind, string message);

    /// <summary>Records a live-traffic success, clearing any prior LiveTraffic failure - the provider-wide self-clearing signal.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="operation">A short label for the successful interaction (e.g. <c>"Live traffic"</c>).</param>
    void RecordLiveTrafficSuccess(string providerKey, string operation);

    /// <summary>Reads back the most recent LiveTraffic outcome for <paramref name="providerKey"/>, or <see langword="null"/> if none has been recorded.</summary>
    /// <param name="providerKey">The provider key.</param>
    ProviderInteractionStatus? GetLiveTraffic(string providerKey);

    /// <summary>Clears any recorded outcome - both tracks - for <paramref name="providerKey"/> - called when the provider is removed, so a later re-added provider of the same key starts clean.</summary>
    /// <param name="providerKey">The provider key.</param>
    void Remove(string providerKey);
}

/// <inheritdoc cref="IProviderInteractionStatusStore" />
public sealed class ProviderInteractionStatusStore : IProviderInteractionStatusStore
{
    private readonly ConcurrentDictionary<string, ProviderInteractionStatus> _adminActionStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderInteractionStatus> _liveTrafficStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ProviderInteractionStatusStore"/> class.</summary>
    /// <param name="timeProvider">Clock used to timestamp recorded outcomes; defaults to <see cref="TimeProvider.System"/>.</param>
    public ProviderInteractionStatusStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void RecordSuccess(string providerKey, string operation) =>
        _adminActionStatuses[providerKey] = new ProviderInteractionStatus(true, operation, null, _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public void RecordFailure(string providerKey, string operation, string message) =>
        _adminActionStatuses[providerKey] = new ProviderInteractionStatus(false, operation, message, _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public ProviderInteractionStatus? Get(string providerKey) =>
        _adminActionStatuses.TryGetValue(providerKey, out var status) ? status : null;

    /// <inheritdoc />
    public void RecordLiveTrafficFailure(string providerKey, ProviderInteractionKind kind, string message) =>
        _liveTrafficStatuses[providerKey] = new ProviderInteractionStatus(false, "Live traffic", message, _timeProvider.GetUtcNow(), kind);

    /// <inheritdoc />
    public void RecordLiveTrafficSuccess(string providerKey, string operation) =>
        _liveTrafficStatuses[providerKey] = new ProviderInteractionStatus(true, operation, null, _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public ProviderInteractionStatus? GetLiveTraffic(string providerKey) =>
        _liveTrafficStatuses.TryGetValue(providerKey, out var status) ? status : null;

    /// <inheritdoc />
    public void Remove(string providerKey)
    {
        _adminActionStatuses.TryRemove(providerKey, out _);
        _liveTrafficStatuses.TryRemove(providerKey, out _);
    }
}
