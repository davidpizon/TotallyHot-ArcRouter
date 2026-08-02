using System.Collections.Concurrent;
using TotallyHot.ArcRouter.Models;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// A circuit-breaker state, per <c>docs/router/agent-resilience-strategies.md</c>'s "Circuit Breaker with
/// Exponential Cooldown": <see cref="Closed"/> (healthy, routed normally), <see cref="Open"/> (unhealthy,
/// bypassed entirely with no network call), and <see cref="HalfOpen"/> (the cooldown expired; exactly one
/// probe request is let through to test recovery). Used at two granularities - see
/// <see cref="CircuitBreakerTargetKey"/> (one upstream model) and <see cref="ICircuitBreaker"/>'s
/// provider-wide members (every model on one provider, for failures like an invalid credential that
/// affect the whole provider, not just the model that happened to surface it).
/// </summary>
public enum CircuitState
{
    /// <summary>The target is healthy; requests are routed to it normally.</summary>
    Closed,

    /// <summary>The target is unhealthy; requests bypass it entirely until its cooldown expires.</summary>
    Open,

    /// <summary>The cooldown has expired; a single probe request decides whether to close or re-open.</summary>
    HalfOpen,
}

/// <summary>
/// Identifies the concrete upstream a resolved route calls - provider key, base URL, and provider model id
/// together - which is what the per-target circuit breaker protects (the same shape
/// <c>RequestInterceptor</c>'s former per-model <c>Fallbacks</c> dedup used to key on, before this replaced
/// it). Two different client-facing <c>ModelName</c>s that happen to resolve to the same provider, base
/// URL, and provider model id share one circuit; two distinct providers that happen to share a
/// <see cref="ProviderModelId"/> string do not.
/// </summary>
/// <param name="Provider">The provider key the route resolves to.</param>
/// <param name="BaseUrl">The upstream provider's absolute base URL.</param>
/// <param name="ProviderModelId">The upstream model identifier.</param>
public readonly record struct CircuitBreakerTargetKey(string Provider, string BaseUrl, string ProviderModelId)
{
    /// <summary>Builds the target key for a resolved route's concrete upstream.</summary>
    public static CircuitBreakerTargetKey FromRoute(ResolvedModelRoute route) =>
        new(route.Provider, route.UpstreamBaseUrl.ToString(), route.ProviderModelId);
}

/// <summary>
/// Tracks upstream health, at two granularities, so an unhealthy target can be bypassed instead of
/// repeatedly attempted. See <c>docs/router/agent-resilience-strategies.md</c> for the design this
/// implements.
/// <para>
/// <b>Per-target</b> (<see cref="CircuitBreakerTargetKey"/>): a single upstream model. Used for failures
/// that are plausibly specific to that one model/endpoint - timeouts, 5xx, 429.
/// </para>
/// <para>
/// <b>Per-provider</b> (a bare provider key string): every model configured on one provider. Used for
/// failures that indicate a provider-wide problem rather than a single-model one - e.g. a 401 almost
/// always means the provider's credential is invalid, which would identically affect every model on that
/// provider, not just the one the client happened to ask for. A successful per-target call also clears
/// that target's provider-wide state (see <see cref="RecordSuccess"/>): one model answering successfully
/// is evidence the provider's credential is fine again for all of them.
/// </para>
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// Read-only snapshot: is <paramref name="target"/> currently OPEN and still cooling down? Used to
    /// filter/rank candidates (e.g. <c>RequestInterceptor</c>'s next-best-model selection) without
    /// claiming a half-open probe slot - a target whose cooldown has just expired reports
    /// <see langword="false"/> here (it's a legitimate candidate again), even though the formal
    /// OPEN-&gt;HALF-OPEN transition only happens via <see cref="ShouldBypass"/>, which is reserved for the
    /// moment a candidate is actually about to be attempted.
    /// </summary>
    bool IsOpen(CircuitBreakerTargetKey target);

    /// <summary>
    /// Call immediately before actually attempting <paramref name="target"/>. Returns <see langword="true"/>
    /// if the attempt must be skipped entirely (still cooling down, or a half-open probe for this target is
    /// already in flight from a concurrent request) - no network call should be made. Returns
    /// <see langword="false"/> when the attempt should proceed; if this call itself just transitioned the
    /// target from OPEN to HALF-OPEN, the caller's upcoming attempt <em>is</em> the single probe, and its
    /// outcome must be reported via <see cref="RecordSuccess"/>/<see cref="RecordFailure"/>.
    /// </summary>
    bool ShouldBypass(CircuitBreakerTargetKey target);

    /// <summary>Records a successful (non-outage) response from <paramref name="target"/>: closes its circuit and resets its failure/trip counters, and also closes <paramref name="target"/>'s provider-wide circuit (see the type-level remarks).</summary>
    void RecordSuccess(CircuitBreakerTargetKey target);

    /// <summary>Records an outage-classified failure (timeout, 429, or 5xx) from <paramref name="target"/>, tripping its circuit to OPEN once the configured failure threshold is reached (or immediately, doubling the cooldown, if this failure was the half-open probe itself).</summary>
    void RecordFailure(CircuitBreakerTargetKey target);

    /// <summary>Provider-wide equivalent of <see cref="IsOpen"/>: is every model on <paramref name="provider"/> currently bypassed?</summary>
    bool IsProviderOpen(string provider);

    /// <summary>Provider-wide equivalent of <see cref="ShouldBypass"/>, called immediately before attempting a candidate on <paramref name="provider"/>.</summary>
    bool ShouldBypassProvider(string provider);

    /// <summary>Records a provider-wide failure (e.g. a 401 - an invalid credential affects every model on the provider, not just the one that surfaced it), tripping every model on <paramref name="provider"/> to OPEN immediately - unlike <see cref="RecordFailure"/>, this does not wait for repeated occurrences, since a single 401 is already decisive.</summary>
    void RecordProviderFailure(string provider);
}

/// <inheritdoc cref="ICircuitBreaker" />
public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<CircuitBreakerTargetKey, TargetState> _targetStates = new();
    private readonly ConcurrentDictionary<string, TargetState> _providerStates = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="options">Failure threshold and cooldown tuning; defaults to <see cref="CircuitBreakerOptions"/>'s own defaults when omitted.</param>
    /// <param name="timeProvider">Clock used for cooldown expiry; defaults to <see cref="TimeProvider.System"/>. Overridable for deterministic tests.</param>
    public CircuitBreaker(IOptions<CircuitBreakerOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _options = options?.Value ?? new CircuitBreakerOptions();
        _options.EnsureValid();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsOpen(CircuitBreakerTargetKey target) => IsOpenCore(_targetStates, target);

    /// <inheritdoc />
    public bool ShouldBypass(CircuitBreakerTargetKey target) => ShouldBypassCore(_targetStates, target);

    /// <inheritdoc />
    public void RecordSuccess(CircuitBreakerTargetKey target)
    {
        RecordSuccessCore(_targetStates, target);
        RecordSuccessCore(_providerStates, target.Provider);
    }

    /// <inheritdoc />
    public void RecordFailure(CircuitBreakerTargetKey target) => RecordFailureCore(_targetStates, target);

    /// <inheritdoc />
    public bool IsProviderOpen(string provider) => IsOpenCore(_providerStates, provider);

    /// <inheritdoc />
    public bool ShouldBypassProvider(string provider) => ShouldBypassCore(_providerStates, provider);

    /// <inheritdoc />
    public void RecordProviderFailure(string provider)
    {
        // Unlike RecordFailureCore's per-target threshold (a single 5xx/timeout is plausibly transient, so
        // several consecutive ones are required before trusting the signal), a provider-wide failure like a
        // 401 is decisive the first time it's seen - an invalid/expired credential does not "maybe" affect
        // the provider, it does. Trip immediately rather than waiting for FailureThreshold occurrences.
        var state = _providerStates.GetOrAdd(provider, static _ => new TargetState());

        lock (state)
        {
            state.ConsecutiveTrips++;
            Trip(state);
        }
    }

    private bool IsOpenCore<TKey>(ConcurrentDictionary<TKey, TargetState> states, TKey key)
        where TKey : notnull
    {
        if (!states.TryGetValue(key, out var state))
        {
            return false;
        }

        lock (state)
        {
            return state.State == CircuitState.Open && _timeProvider.GetUtcNow() < state.CooldownExpiresAt;
        }
    }

    private bool ShouldBypassCore<TKey>(ConcurrentDictionary<TKey, TargetState> states, TKey key)
        where TKey : notnull
    {
        var state = states.GetOrAdd(key, static _ => new TargetState());

        lock (state)
        {
            switch (state.State)
            {
                case CircuitState.Closed:
                    return false;

                case CircuitState.Open:
                    if (_timeProvider.GetUtcNow() < state.CooldownExpiresAt)
                    {
                        return true;
                    }

                    // Cooldown expired: transition to HALF-OPEN and let this caller through as the single
                    // probe. A concurrent caller sees ProbeInFlight below and keeps bypassing until the
                    // probe resolves via RecordSuccess/RecordFailure.
                    state.State = CircuitState.HalfOpen;
                    state.ProbeInFlight = true;
                    return false;

                default: // HalfOpen
                    if (state.ProbeInFlight)
                    {
                        return true;
                    }

                    // A prior probe resolved (RecordSuccess/RecordFailure clears ProbeInFlight) without
                    // moving out of HalfOpen - shouldn't normally happen, but claim the slot rather than
                    // let two probes race.
                    state.ProbeInFlight = true;
                    return false;
            }
        }
    }

    private static void RecordSuccessCore<TKey>(ConcurrentDictionary<TKey, TargetState> states, TKey key)
        where TKey : notnull
    {
        if (!states.TryGetValue(key, out var state))
        {
            // Never seen (or never failed) - already implicitly Closed; nothing to reset.
            return;
        }

        lock (state)
        {
            state.State = CircuitState.Closed;
            state.ConsecutiveFailures = 0;
            state.ConsecutiveTrips = 0;
            state.ProbeInFlight = false;
        }
    }

    private void RecordFailureCore<TKey>(ConcurrentDictionary<TKey, TargetState> states, TKey key)
        where TKey : notnull
    {
        var state = states.GetOrAdd(key, static _ => new TargetState());

        lock (state)
        {
            if (state.State == CircuitState.HalfOpen)
            {
                // The single probe failed: re-trip immediately (no threshold to re-cross) and double the
                // cooldown, per the design doc's step 5 ("the new cooldown doubles to 20 seconds").
                state.ConsecutiveTrips++;
                Trip(state);
                return;
            }

            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= _options.FailureThreshold)
            {
                state.ConsecutiveTrips++;
                Trip(state);
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="state"/> to OPEN with the exponentially-scaled cooldown for its current trip
    /// count: <c>min(MaxCooldown, BaseCooldown × 2^(ConsecutiveTrips - 1))</c>, so the first trip gets
    /// exactly <see cref="CircuitBreakerOptions.BaseCooldown"/> and each subsequent consecutive trip
    /// doubles it, capped at <see cref="CircuitBreakerOptions.MaxCooldown"/>. Caller must hold the lock on
    /// <paramref name="state"/>.
    /// </summary>
    private void Trip(TargetState state)
    {
        var exponent = Math.Max(0, state.ConsecutiveTrips - 1);
        var scaled = _options.BaseCooldown * Math.Pow(2, exponent);
        var cooldown = scaled > _options.MaxCooldown ? _options.MaxCooldown : scaled;

        state.State = CircuitState.Open;
        state.ConsecutiveFailures = 0;
        state.ProbeInFlight = false;
        state.CooldownExpiresAt = _timeProvider.GetUtcNow() + cooldown;
    }

    /// <summary>Mutable per-key state (per-target or per-provider), always accessed under its own lock (see each method above).</summary>
    private sealed class TargetState
    {
        public CircuitState State;
        public int ConsecutiveFailures;
        public int ConsecutiveTrips;
        public DateTimeOffset CooldownExpiresAt;
        public bool ProbeInFlight;
    }
}

