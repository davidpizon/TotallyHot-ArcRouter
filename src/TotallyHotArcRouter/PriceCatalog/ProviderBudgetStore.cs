using System.Globalization;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// A provider's live budget state for the current month: its optional caps and the spend accumulated
/// against them. A <see langword="null"/> cap means that dimension is unbudgeted.
/// </summary>
/// <param name="DollarCap">The monthly USD cap, or <see langword="null"/> for no dollar budget.</param>
/// <param name="TokenCap">The monthly total-token cap, or <see langword="null"/> for no token budget.</param>
/// <param name="DollarSpent">USD spent this month.</param>
/// <param name="TokensUsed">Total (prompt + completion) tokens used this month.</param>
public readonly record struct ProviderBudgetState(decimal? DollarCap, long? TokenCap, decimal DollarSpent, long TokensUsed)
{
    /// <summary>
    /// Gets whether either a set dollar cap or a set token cap has been met or exceeded this month. An
    /// unbudgeted dimension never breaches; a provider with no caps never breaches.
    /// </summary>
    public bool IsBreached =>
        (DollarCap is { } d && DollarSpent >= d) ||
        (TokenCap is { } t && TokensUsed >= t);
}

/// <summary>
/// Writable, thread-safe source of truth for per-provider monthly budgets, backed by the
/// <c>provider_budgets</c> and <c>provider_spend</c> tables. Caps are set from the Governance &gt; Providers
/// panel; spend is accumulated on the telemetry side-effect path
/// (<see cref="RecordUsageAsync"/>) and read back both by the budget bars and by routing enforcement
/// (<see cref="IsBreached"/>), which skips a provider whose cap is exhausted.
/// </summary>
/// <remarks>
/// Shaped after <see cref="PriceSourceToggleStore"/>: an immutable snapshot swapped under a gate with a
/// <see cref="Changed"/> event, so <see cref="IsBreached"/> - called per candidate on the routing hot path -
/// is a lock-free dictionary read. The snapshot is built for a specific <c>YYYY-MM</c> period; a month
/// rollover mid-process is detected on the next access and triggers a reload, which is what makes
/// "monthly auto-reset" true without any scheduled job (the new period simply has no rows yet).
/// </remarks>
public sealed class ProviderBudgetStore : IBudgetEnforcer, IDisposable
{
    private readonly PriceCatalogRepository _repository;
    private readonly ILogger<ProviderBudgetStore>? _logger;
    private readonly object _gate = new();
    private bool _disposed;

    // Serializes the read-modify-write in AddProviderSpend. The store runs in a single proxy process (the
    // sole writer of this DB), so serializing spend writes here is what keeps concurrent requests billing the
    // same provider from losing an update - the same guard SpendTracker uses for its file appends.
    private readonly SemaphoreSlim _spendWriteMutex = new(1, 1);

    // Swapped as a whole under _gate; volatile so IsBreached's lock-free read observes the swap. The value
    // is the current-month state per provider key.
    private volatile IReadOnlyDictionary<string, ProviderBudgetState> _snapshot =
        new Dictionary<string, ProviderBudgetState>(StringComparer.OrdinalIgnoreCase);

    // The YYYY-MM the snapshot was built for; a differing CurrentPeriod means the month rolled over and the
    // snapshot must be rebuilt (spends reset, caps carry forward).
    private volatile string _period = CurrentPeriod();

    /// <summary>Initializes a new instance of the <see cref="ProviderBudgetStore"/> class with an empty snapshot.</summary>
    /// <remarks>
    /// Deliberately does not read the database - like <see cref="PriceSourceToggleStore"/>, this singleton is
    /// constructed before <see cref="PriceCatalogDatabase.EnsureCreated"/> has run, so the schema may not
    /// exist yet. The startup health check calls <see cref="Reload"/> right after creating the schema.
    /// </remarks>
    public ProviderBudgetStore(PriceCatalogRepository repository, ILogger<ProviderBudgetStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Raised after a budget cap has been persisted and the snapshot swapped.</summary>
    public event Action? Changed;

    /// <summary>The current calendar month as a <c>YYYY-MM</c> period key (UTC).</summary>
    private static string CurrentPeriod() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rebuilds the current-month snapshot from the database - every budgeted provider's caps joined with its
    /// spend for the current period. Called at startup, after each cap write, and automatically on a month
    /// rollover.
    /// </summary>
    public void Reload()
    {
        var period = CurrentPeriod();
        var next = BuildSnapshot(period);

        lock (_gate)
        {
            _snapshot = next;
            _period = period;
        }
    }

    /// <summary>Builds the current-month budget state per provider by joining caps with spend for <paramref name="period"/>.</summary>
    // Builds the current-month state per provider from the database: every budgeted provider's caps joined
    // with its spend for <paramref name="period"/>. Both sides are indexed into dictionaries first, so the
    // join over the union of keys is O(n) rather than a FirstOrDefault scan per key.
    private Dictionary<string, ProviderBudgetState> BuildSnapshot(string period)
    {
        var budgets = _repository.GetProviderBudgets()
            .ToDictionary(b => b.ProviderKey, b => b, StringComparer.OrdinalIgnoreCase);
        var spend = _repository.GetProviderSpend(period)
            .ToDictionary(s => s.ProviderKey, s => s, StringComparer.OrdinalIgnoreCase);

        var next = new Dictionary<string, ProviderBudgetState>(StringComparer.OrdinalIgnoreCase);

        // A provider appears in the snapshot if it has a cap and/or spend this month.
        foreach (var key in budgets.Keys.Concat(spend.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            budgets.TryGetValue(key, out var budget);
            spend.TryGetValue(key, out var s);
            next[key] = new ProviderBudgetState(
                DollarCap: budget?.DollarCap,
                TokenCap: budget?.TokenCap,
                DollarSpent: s?.CostUsd ?? 0m,
                TokensUsed: (s?.PromptTokens ?? 0) + (s?.CompletionTokens ?? 0));
        }

        return next;
    }

    /// <summary>
    /// Gets a provider's current-month budget state. An unknown or unbudgeted provider returns an all-zero,
    /// no-cap state (never breached).
    /// </summary>
    public ProviderBudgetState GetStatus(string providerKey)
    {
        EnsureCurrentPeriod();
        return _snapshot.TryGetValue(providerKey, out var state) ? state : default;
    }

    /// <summary>
    /// Gets whether <paramref name="providerKey"/> has met or exceeded a set cap this month. Lock-free read
    /// on the routing hot path.
    /// </summary>
    public bool IsBreached(string providerKey)
    {
        EnsureCurrentPeriod();
        return _snapshot.TryGetValue(providerKey, out var state) && state.IsBreached;
    }

    /// <summary>
    /// Records one request's usage against the provider that served it, for the current month. Best-effort:
    /// a database error is logged and swallowed, like every other telemetry side-effect on the proxy path,
    /// so a spend-ledger hiccup never fails a request that already succeeded upstream. Null cost/tokens
    /// contribute zero.
    /// </summary>
    public async Task RecordUsageAsync(
        string providerKey,
        decimal? costUsd,
        int? promptTokens,
        int? completionTokens,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return;
        }

        var period = CurrentPeriod();
        var cost = costUsd ?? 0m;
        var prompt = promptTokens ?? 0;
        var completion = completionTokens ?? 0;

        try
        {
            // Serialize the repository's read-modify-write of cost_usd so two requests billing the same
            // provider at once can't both read the old total and clobber each other (undercounted spend, which
            // would weaken budget enforcement). Token columns already accumulate via SQL '+', but cost doesn't,
            // hence the guard.
            await _spendWriteMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _repository.AddProviderSpend(providerKey, period, cost, prompt, completion);
            }
            finally
            {
                _spendWriteMutex.Release();
            }

            lock (_gate)
            {
                if (!string.Equals(period, _period, StringComparison.Ordinal))
                {
                    // Month rolled over since the snapshot was built; rebuild rather than incrementing a stale
                    // (previous-month) tally. Done under the gate so IsBreached never sees a half-updated map.
                    _snapshot = BuildSnapshot(period);
                    _period = period;
                }
                else
                {
                    _snapshot.TryGetValue(providerKey, out var current);
                    var next = new Dictionary<string, ProviderBudgetState>(_snapshot, StringComparer.OrdinalIgnoreCase)
                    {
                        [providerKey] = current with
                        {
                            DollarSpent = current.DollarSpent + cost,
                            TokensUsed = current.TokensUsed + prompt + completion,
                        },
                    };
                    _snapshot = next;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The request was aborted (ProxyMiddleware passes the request token). Recording is a best-effort
            // post-response side-effect, so a cancellation here is expected - swallow it without warning noise.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record spend for provider {Provider}.", SanitizeForLog(providerKey));
        }
    }

    /// <summary>
    /// Persists a provider's monthly caps (null clears a dimension; both null removes the budget), refreshes
    /// the snapshot, and raises <see cref="Changed"/>.
    /// </summary>
    public void SetBudget(string providerKey, decimal? dollarCap, long? tokenCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        // Reject negative caps at the store boundary, not just at the admin endpoint: a negative cap would
        // read as immediately breached (spend >= cap) and silently block all traffic to the provider. As an
        // ArgumentException this maps to the endpoint's 400 path.
        if (dollarCap is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(dollarCap), dollarCap, "A budget cap cannot be negative.");
        }

        if (tokenCap is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCap), tokenCap, "A budget cap cannot be negative.");
        }

        try
        {
            _repository.SetProviderBudget(providerKey, dollarCap, tokenCap);
            Reload();
        }
        catch (Exception ex)
        {
            // Persistence/reload failures otherwise surface only as a generic 500 at the API - log the
            // context here so an operator can diagnose it, then rethrow for the endpoint to translate.
            _logger?.LogError(ex, "Failed to persist budget for provider {Provider}.", SanitizeForLog(providerKey));
            throw;
        }

        // Log the caps as structured fields (null = no cap for that dimension) rather than a pre-formatted
        // string - avoids InvariantCulture's generic "¤" currency glyph and keeps the values queryable.
        _logger?.LogInformation(
            "Budget for provider {Provider} set from the Governance panel: dollar cap {DollarCap}, token cap {TokenCap}.",
            SanitizeForLog(providerKey),
            dollarCap,
            tokenCap);

        Changed?.Invoke();
    }

    /// <summary>Strips CR/LF from a client-controlled provider key so it cannot forge additional log lines.</summary>
    // Strips CR/LF from a client-controlled provider key before it enters a log template, so a crafted key
    // can't forge additional log lines (CodeQL: log forging, CWE-117). Chained Replace directly on the
    // tainted value is the sanitizer shape CodeQL's data-flow analysis recognizes - mirrors ProxyMiddleware's
    // and RequestInterceptor's own SanitizeForLog.
    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");

    /// <summary>Detects a month rollover since the last read and rebuilds the snapshot so spend resets for the new month.</summary>
    // Detects a month rollover on a read and rebuilds so spends reset to the new month's (empty) tally. Cheap
    // string compare on the common path; a reload only on the boundary.
    private void EnsureCurrentPeriod()
    {
        if (!string.Equals(_period, CurrentPeriod(), StringComparison.Ordinal))
        {
            Reload();
        }
    }

    /// <summary>
    /// Disposes the spend-write semaphore. Invoked by the DI container at shutdown (this store is a
    /// registered singleton) and by any test that owns a store's lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spendWriteMutex.Dispose();
    }
}

