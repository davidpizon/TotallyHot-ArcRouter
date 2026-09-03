using TotallyHot.ArcRouter.Proxy.Concurrency;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// A provider's live budget state for the current month: its optional caps and the spend accumulated
/// against them. A <see langword="null"/> cap means that dimension is unbudgeted.
/// </summary>
/// <param name="DollarCap">The monthly USD cap, or <see langword="null"/> for no dollar budget.</param>
/// <param name="TokenCap">The monthly total-token cap, or <see langword="null"/> for no token budget.</param>
/// <param name="DollarSpent">USD spent this month.</param>
/// <param name="TokensUsed">
/// Total real tokens processed this month: prompt + completion + cache-creation + cache-read. This is a
/// deliberate widening from prompt+completion alone - cache tokens are real tokens the provider processed
/// and billed for, so a cache-heavy provider's token cap must account for them or the cap would
/// systematically under-count its actual load.
/// </param>
/// <param name="CacheTokensUsed">
/// Cache-creation plus cache-read tokens used this month, broken out from <see cref="TokensUsed"/> for
/// display (e.g. the GUI's "estimated from intercepted traffic" card section).
/// </param>
/// <param name="LastUsageAtUtc">
/// The UTC instant the most recent usage was recorded this month, or <see langword="null"/> if no usage has
/// been recorded this month yet.
/// </param>
/// <param name="WindowKind">
/// The persisted <see cref="BudgetWindow"/> discriminator this provider's cap resets on. Defaults to
/// "Monthly", matching the only behavior that existed before per-provider windows (§5.10).
/// </param>
/// <param name="WindowHours">
/// The block length in hours when <paramref name="WindowKind"/> is "RollingHours"; otherwise
/// <see langword="null"/>.
/// </param>
/// <param name="PeriodKey">The period key this state's spend was accumulated under, per <see cref="Window"/>.</param>
public readonly record struct ProviderBudgetState(
    decimal? DollarCap,
    long? TokenCap,
    decimal DollarSpent,
    long TokensUsed,
    long CacheTokensUsed = 0,
    DateTimeOffset? LastUsageAtUtc = null,
    string WindowKind = "Monthly",
    int? WindowHours = null,
    string PeriodKey = "")
{
    /// <summary>
    /// Gets whether either a set dollar cap or a set token cap has been met or exceeded this period. An
    /// unbudgeted dimension never breaches; a provider with no caps never breaches.
    /// </summary>
    public bool IsBreached =>
        (DollarCap is { } d && DollarSpent >= d) ||
        (TokenCap is { } t && TokensUsed >= t);

    /// <summary>The budget window this state's cap resets on.</summary>
    public BudgetWindow Window => BudgetWindowCodec.Decode(kind: WindowKind, hours: WindowHours);

    /// <summary>
    /// The UTC instant the current period ends and spend resets to zero - computed live from
    /// <see cref="Window"/> against the current time, not baked into the snapshot, so it keeps counting down
    /// between reloads.
    /// </summary>
    public DateTimeOffset NextResetUtc => Window.NextResetUtc(DateTimeOffset.UtcNow);
}

/// <summary>
/// Writable, thread-safe source of truth for per-provider budgets, backed by the <c>provider_budgets</c>
/// and <c>provider_spend</c> tables. Caps and a reset window are set from the Governance &gt; Providers
/// panel; spend is accumulated on the telemetry side-effect path
/// (<see cref="RecordUsageAsync"/>) and read back both by the budget bars and by routing enforcement
/// (<see cref="IsBreached"/>), which skips a provider whose cap is exhausted.
/// </summary>
/// <remarks>
/// Shaped after <see cref="PriceSourceToggleStore"/>: an immutable snapshot swapped via
/// <see cref="Proxy.Concurrency.SnapshotCache{T}"/> with a <see cref="Changed"/> event, so
/// <see cref="IsBreached"/> - called per candidate on the routing hot path - is a lock-free dictionary read.
/// Each provider carries its own <see cref="BudgetWindow"/> (§5.10 - caps
/// need not all reset on the same schedule) and the period key its slice of the snapshot was built for; a
/// rollover for any one provider is detected on the next access and triggers a full reload, which is what
/// makes auto-reset true without any scheduled job (the new period simply has no rows yet).
/// </remarks>
public sealed class ProviderBudgetStore : IBudgetEnforcer, IDisposable
{
    private readonly ProviderBudgetRepository _budgetRepository;

    // Rebuilt and swapped as a whole via SnapshotCache<T> (docs/router/code-smell-refactoring-plan.md M2),
    // so IsBreached's lock-free read on the routing hot path always sees a fully-formed map. The value is
    // the current-period state per provider key - each provider carries its own PeriodKey (§5.10: providers
    // can be on different BudgetWindow kinds), unlike the single global period this store used before
    // per-provider windows existed.
    private readonly SnapshotCache<IReadOnlyDictionary<string, ProviderBudgetState>> _cache =
        new(new Dictionary<string, ProviderBudgetState>(StringComparer.OrdinalIgnoreCase));

    private readonly ILogger<ProviderBudgetStore>? _logger;
    private readonly ProviderSpendRepository _spendRepository;

    // Serializes the read-modify-write in AddProviderSpend. The store runs in a single proxy process (the
    // sole writer of this DB), so serializing spend writes here is what keeps concurrent requests billing the
    // same provider from losing an update - the same guard SpendTracker uses for its file appends.
    private readonly SemaphoreSlim _spendWriteMutex = new(1, 1);
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ProviderBudgetStore"/> class with an empty snapshot.</summary>
    /// <remarks>
    /// Deliberately does not read the database - like <see cref="PriceSourceToggleStore"/>, this singleton is
    /// constructed before <see cref="PriceCatalogDatabase.EnsureCreated"/> has run, so the schema may not
    /// exist yet. The startup health check calls <see cref="Reload"/> right after creating the schema.
    /// </remarks>
    public ProviderBudgetStore(
        ProviderBudgetRepository budgetRepository,
        ProviderSpendRepository spendRepository,
        ILogger<ProviderBudgetStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(budgetRepository);
        ArgumentNullException.ThrowIfNull(spendRepository);
        _budgetRepository = budgetRepository;
        _spendRepository = spendRepository;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether <paramref name="providerKey"/> has met or exceeded a set cap this month. Lock-free read
    /// on the routing hot path.
    /// </summary>
    public bool IsBreached(string providerKey)
    {
        EnsureCurrentPeriod();
        return _cache.Current.TryGetValue(key: providerKey, value: out var state) && state.IsBreached;
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
        int? cacheCreationTokens,
        int? cacheReadTokens,
        DateTimeOffset usageAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return;

        // Resolve this provider's configured window from the current snapshot (a lock-free read, no DB
        // round-trip on this hot path); an unbudgeted or not-yet-seen provider defaults to Monthly.
        // BudgetWindowCodec.Decode already treats a null/unrecognized WindowKind as Monthly, so this reads
        // correctly whether or not providerKey has a snapshot entry yet.
        _cache.Current.TryGetValue(key: providerKey, value: out var existingState);
        var window = existingState.Window;
        var period = window.PeriodKey(DateTimeOffset.UtcNow);
        var cost = costUsd ?? 0m;
        var prompt = promptTokens ?? 0;
        var completion = completionTokens ?? 0;
        var cacheCreation = cacheCreationTokens ?? 0;
        var cacheRead = cacheReadTokens ?? 0;

        try
        {
            // Serialize the repository's read-modify-write of cost_usd so two requests billing the same
            // provider at once can't both read the old total and clobber each other (undercounted spend, which
            // would weaken budget enforcement). Token columns already accumulate via SQL '+', but cost doesn't,
            // hence the guard.
            await _spendWriteMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _spendRepository.AddProviderSpend(providerKey: providerKey, period: period, costUsd: cost,
                    promptTokens: prompt, completionTokens: completion, cacheCreationTokens: cacheCreation,
                    cacheReadTokens: cacheRead, usageAtUtc: usageAtUtc);
            }
            finally
            {
                _spendWriteMutex.Release();
            }

            // Done inside SnapshotCache<T>.Rebuild's own gate, so IsBreached never sees a half-updated map
            // and two concurrent recordings can't race each other's read-then-swap.
            _cache.Rebuild(() =>
            {
                var snapshot = _cache.Current;

                // TryGetValue on a miss yields default(ProviderBudgetState), whose PeriodKey is null (record
                // struct defaults skip primary-constructor default values) - IsNullOrEmpty, not a bare
                // .Length check, is what makes the "no snapshot entry yet" case not throw.
                snapshot.TryGetValue(key: providerKey, value: out var beforeUpdate);
                if (string.IsNullOrEmpty(beforeUpdate.PeriodKey) || !string.Equals(a: period, b: beforeUpdate.PeriodKey,
                        comparisonType: StringComparison.Ordinal))
                    // This provider's period rolled over (or it has no snapshot entry yet); rebuild rather
                    // than incrementing a stale tally.
                    return BuildSnapshot(DateTimeOffset.UtcNow);

                snapshot.TryGetValue(key: providerKey, value: out var current);
                var cacheTokens = cacheCreation + cacheRead;
                return new Dictionary<string, ProviderBudgetState>(collection: snapshot,
                    comparer: StringComparer.OrdinalIgnoreCase)
                {
                    [providerKey] = current with
                    {
                        DollarSpent = current.DollarSpent + cost,
                        TokensUsed = current.TokensUsed + prompt + completion + cacheTokens,
                        CacheTokensUsed = current.CacheTokensUsed + cacheTokens,
                        // Same monotonic-advance rule as AddProviderSpend's SQL MAX, so the in-memory
                        // snapshot this fast-path updates can't disagree with the database it mirrors
                        // when two requests for the same provider complete out of order.
                        LastUsageAtUtc = current.LastUsageAtUtc is { } existing && existing > usageAtUtc
                            ? existing
                            : usageAtUtc
                    }
                };
            });
        }
        catch (OperationCanceledException)
        {
            // The request was aborted (ProxyMiddleware passes the request token). Recording is a best-effort
            // post-response side-effect, so a cancellation here is expected - swallow it without warning noise.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(exception: ex, message: "Failed to record spend for provider {Provider}.",
                SanitizeForLog(providerKey));
        }
    }

    /// <summary>
    /// Disposes the spend-write semaphore. Invoked by the DI container at shutdown (this store is a
    /// registered singleton) and by any test that owns a store's lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _spendWriteMutex.Dispose();
    }

    /// <summary>Raised after a budget cap has been persisted and the snapshot swapped.</summary>
    public event Action? Changed;

    /// <summary>
    /// Rebuilds the current-period snapshot from the database - every budgeted provider's caps joined with
    /// its spend for its own configured period (§5.10: providers can be on different <see cref="BudgetWindow"/>
    /// kinds). Called at startup, after each cap write, and automatically on a period rollover.
    /// </summary>
    public void Reload()
    {
        _cache.Rebuild(() => BuildSnapshot(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Builds the current-period state per provider by joining each provider's caps and configured window
    /// with its spend for that window's period key as of <paramref name="now"/>.
    /// </summary>
    // Every budgeted provider computes its own period key from its own window, so spend is fetched in
    // batches grouped by distinct period string (not per-provider) to keep this O(distinct periods) rather
    // than O(providers) queries. Cross-provider collisions on the period string are not a correctness risk:
    // Monthly/Weekly/RollingHours produce disjoint formats ("yyyy-MM" vs "yyyy-Www" vs "R{h}H-..."), so a
    // provider's own spend rows only ever carry a period string its own window would have produced.
    private Dictionary<string, ProviderBudgetState> BuildSnapshot(DateTimeOffset now)
    {
        var budgets = _budgetRepository.GetProviderBudgets()
            .ToDictionary(keySelector: b => b.ProviderKey, elementSelector: b => b,
                comparer: StringComparer.OrdinalIgnoreCase);

        var defaultMonthlyPeriod = new BudgetWindow.Monthly().PeriodKey(now);
        var periodByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, budget) in budgets)
        {
            var window = BudgetWindowCodec.Decode(kind: budget.WindowKind, hours: budget.WindowHours);
            periodByProvider[key] = window.PeriodKey(now);
        }

        var spend = new Dictionary<string, ProviderSpendRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var period in periodByProvider.Values.Distinct(StringComparer.Ordinal)
                     .Append(defaultMonthlyPeriod))
        foreach (var row in _spendRepository.GetProviderSpend(period))
        {
            // A budgeted provider's spend only counts if it matches that provider's own current period;
            // an unbudgeted provider (no entry in periodByProvider) is assumed Monthly, the only window
            // that ever existed before this feature, so its historical spend rows are always under
            // defaultMonthlyPeriod.
            var expectedPeriod = periodByProvider.TryGetValue(key: row.ProviderKey, value: out var p)
                ? p
                : defaultMonthlyPeriod;
            if (string.Equals(a: expectedPeriod, b: period, comparisonType: StringComparison.Ordinal))
                spend[row.ProviderKey] = row;
        }

        var next = new Dictionary<string, ProviderBudgetState>(StringComparer.OrdinalIgnoreCase);

        // A provider appears in the snapshot if it has a cap and/or spend this period.
        foreach (var key in budgets.Keys.Concat(spend.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            budgets.TryGetValue(key: key, value: out var budget);
            spend.TryGetValue(key: key, value: out var s);
            var cacheTokens = (s?.CacheCreationTokens ?? 0) + (s?.CacheReadTokens ?? 0);
            next[key] = new ProviderBudgetState(
                DollarCap: budget?.DollarCap,
                TokenCap: budget?.TokenCap,
                DollarSpent: s?.CostUsd ?? 0m,
                TokensUsed: (s?.PromptTokens ?? 0) + (s?.CompletionTokens ?? 0) + cacheTokens,
                CacheTokensUsed: cacheTokens,
                LastUsageAtUtc: s?.LastUsageAtUtc,
                WindowKind: budget?.WindowKind ?? "Monthly",
                WindowHours: budget?.WindowHours,
                PeriodKey: periodByProvider.TryGetValue(key: key, value: out var pk) ? pk : defaultMonthlyPeriod);
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
        return _cache.Current.TryGetValue(key: providerKey, value: out var state) ? state : default;
    }

    /// <summary>
    /// Persists a provider's caps (null clears a dimension; both null removes the budget) and reset window,
    /// refreshes the snapshot, and raises <see cref="Changed"/>.
    /// </summary>
    /// <param name="providerKey">The provider to set the budget for.</param>
    /// <param name="dollarCap">The USD cap, or <see langword="null"/> to clear it.</param>
    /// <param name="tokenCap">The token cap, or <see langword="null"/> to clear it.</param>
    /// <param name="window">
    /// The window the caps reset on; defaults to <see cref="BudgetWindow.Monthly"/> when omitted, preserving
    /// today's behavior for a caller that doesn't yet know about windows. Ignored when both caps are null.
    /// </param>
    public void SetBudget(string providerKey, decimal? dollarCap, long? tokenCap, BudgetWindow? window = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        // Reject negative caps at the store boundary, not just at the admin endpoint: a negative cap would
        // read as immediately breached (spend >= cap) and silently block all traffic to the provider. As an
        // ArgumentException this maps to the endpoint's 400 path.
        if (dollarCap is < 0m)
            throw new ArgumentOutOfRangeException(paramName: nameof(dollarCap), actualValue: dollarCap,
                message: "A budget cap cannot be negative.");

        if (tokenCap is < 0)
            throw new ArgumentOutOfRangeException(paramName: nameof(tokenCap), actualValue: tokenCap,
                message: "A budget cap cannot be negative.");

        try
        {
            _budgetRepository.SetProviderBudget(providerKey: providerKey, dollarCap: dollarCap, tokenCap: tokenCap,
                window: window);
            Reload();
        }
        catch (Exception ex)
        {
            // Persistence/reload failures otherwise surface only as a generic 500 at the API - log the
            // context here so an operator can diagnose it, then rethrow for the endpoint to translate.
            _logger?.LogError(exception: ex, message: "Failed to persist budget for provider {Provider}.",
                SanitizeForLog(providerKey));
            throw;
        }

        // Log the caps as structured fields (null = no cap for that dimension) rather than a pre-formatted
        // string - avoids InvariantCulture's generic "¤" currency glyph and keeps the values queryable.
        _logger?.LogInformation(
            message:
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
    private static string SanitizeForLog(string value)
    {
        return value.Replace(oldValue: "\r", newValue: " ").Replace(oldValue: "\n", newValue: " ");
    }

    /// <summary>Detects a per-provider period rollover since the last read and rebuilds the snapshot so spend resets.</summary>
    // Detects a rollover on a read and rebuilds so spends reset to the new period's (empty) tally. Each
    // provider can be on its own BudgetWindow (§5.10), so this compares every snapshot entry's stored
    // PeriodKey against a freshly computed one from that same entry's own Window - an O(providers) scan
    // (typically a handful of configured providers), replacing the old single global string compare that
    // was possible only because every provider shared one monthly period.
    private void EnsureCurrentPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var state in _cache.Current.Values)
            if (!string.Equals(a: state.PeriodKey, b: state.Window.PeriodKey(now),
                    comparisonType: StringComparison.Ordinal))
            {
                Reload();
                return;
            }
    }
}