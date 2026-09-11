using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Base for the gRPC-backed Governance and System Settings singleton view-model stores. Owns the
/// "singleton + <see cref="Changed"/> event + best-effort, reachability-tolerant" shape that eleven of them
/// re-implemented identically: the three status properties, the change notification, disposal of whatever
/// the store built itself, and — the part most worth having in one place — the rule that a load swallows
/// its failure while a mutation records and rethrows.
/// </summary>
/// <remarks>
/// <para>
/// The load/mutation split is deliberate and load-bearing. A load failing means "we could not show you
/// this", which the unreachable state covers; a mutation failing means "the thing you just asked for did
/// not happen", which the user has to be told inline. See <see cref="LoadGuardedAsync"/> versus
/// <see cref="RecordFailure"/>.
/// </para>
/// <para>
/// <b>Three stores deliberately do not derive from this, and should not be made to.</b>
/// <c>RoutingGateStore</c> polls continuously on a background loop behind its own lock, derives
/// <c>IsReachable</c> from a three-valued connection state, is <see cref="IAsyncDisposable"/>, and raises
/// <c>Changed</c> only on an actual change — it shares the name "Store" but none of the shape.
/// <c>ProviderAdminStore</c> and <c>UsageStore</c> speak HTTP, not gRPC (see
/// <see href="../../../docs/adr/0007-provider-admin-client-stays-on-http.md">ADR-0007</see>): their
/// <c>ProviderAdminException</c> carries no unavailable-versus-rejected distinction for
/// <see cref="GrpcAdminException.IsUnavailable"/> to key off, and they surface failures through
/// <c>ToastService</c> rather than <see cref="LastError"/>. Bending this base to fit them would mean
/// adding a toast hook and an exception-typed failure callback for two callers, which is how a useful
/// base class turns into a burden.
/// </para>
/// </remarks>
/// <typeparam name="TClient">The gRPC admin client this store wraps.</typeparam>
public abstract class AdminStoreBase<TClient> : IDisposable
    where TClient : class
{
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    // Whether IsReachable == false is currently because an operation actually failed to reach the router,
    // as opposed to nothing having succeeded yet (the virgin state is also IsReachable == false). LastError
    // being non-null looks like the same signal but is not: RecordFailure also writes a rejection's message
    // into LastError when asked to, and that must never be mistaken for proof of an outage - see
    // RecordFailure's remarks and its regression test for the sequence this field exists to get right.
    private bool _unreachableIsARealOutage;

    /// <summary>Initializes a new instance of the <see cref="AdminStoreBase{TClient}"/> class.</summary>
    /// <param name="client">The admin client to drive. Never null.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ownsClient">
    /// Whether this store created <paramref name="client"/> and must therefore dispose it. False for the
    /// caller-supplied-client constructor every store offers as a test seam, where the caller owns the
    /// lifetime.
    /// </param>
    protected AdminStoreBase(TClient client, ILogger? logger, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        Client = client;
        Logger = logger;
        if (ownsClient && client is IDisposable disposable) _owned.Add(disposable);
    }

    /// <summary>Gets the admin client this store drives.</summary>
    protected TClient Client { get; }

    /// <summary>Gets this store's logger, if one was supplied.</summary>
    protected ILogger? Logger { get; }

    /// <summary>
    /// Gets whether a load has completed at least once, so the UI can distinguish "loading" from "empty".
    /// Set once any guarded operation finishes, successfully or not — a failed first load is still a
    /// completed one, and leaving this false would hold the panel on a spinner forever.
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// Gets whether the last load or mutation reached the router. Only a connectivity failure moves this:
    /// a rejection reached the router and is the panel's inline error to render, so treating it as
    /// unreachable would replace the whole panel with a "router down" state that is both wrong and hides
    /// the actual message.
    /// </summary>
    public bool IsReachable { get; private set; }

    /// <summary>
    /// Gets the message from the last failed operation, if any. Usually a connectivity failure, but a
    /// caller can also route a reachable rejection here via <see cref="RecordFailure"/>'s
    /// <c>recordRejectionMessage</c> - the System Settings window does, since it reads this as its only
    /// error channel. Most panels instead render a rejection from the exception they caught, so this stays
    /// <see langword="null"/> for them even after one.
    /// </summary>
    public string? LastError { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Raised after any of this store's observable state changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Releases what this store built itself. Anything handed in by a caller is deliberately left alone —
    /// disposing a supplied transport would reach past this store into its caller's lifetime, which is the
    /// trap recorded against the GUI stores' <c>HttpClient</c> ownership.
    /// </summary>
    /// <param name="disposing">Whether this is a managed disposal.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            foreach (var resource in _owned)
                resource.Dispose();

        _owned.Clear();
        _disposed = true;
    }

    /// <summary>
    /// Registers a resource this store created and therefore owns, so <see cref="Dispose()"/> releases it.
    /// For the extra collaborators a store builds beyond its client — an <see cref="HttpClient"/>, say.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resource">The resource to take ownership of.</param>
    /// <returns><paramref name="resource"/>, so this can wrap a constructor call inline.</returns>
    protected T Own<T>(T resource)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        _owned.Add(resource);
        return resource;
    }

    /// <summary>Raises <see cref="Changed"/>.</summary>
    protected void NotifyChanged()
    {
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs a read operation, swallowing <see cref="GrpcAdminException"/> into
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than letting it escape, so a panel renders
    /// an "unreachable" state instead of crashing when the router isn't running.
    /// </summary>
    /// <param name="operation">The read to run.</param>
    /// <param name="description">
    /// A short lower-case phrase naming the operation for the failure log, e.g. <c>"read the routing mode"</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="onFailure">
    /// Optional cleanup to run on failure <em>before</em> <see cref="Changed"/> is raised, for a store that
    /// must drop stale data rather than leave it standing (see <c>JudgeCalibrationAdminStore</c>, whose
    /// report is recomputed on every read). Running it before the notification is the point: a subscriber
    /// must never be woken to render data this store has already decided to distrust.
    /// </param>
    /// <param name="beforeNotify">
    /// Optional cleanup to run, on success or failure, immediately before this call's own
    /// <see cref="Changed"/> notification - for a wrapper that holds a transient flag (<c>IsLoading</c>,
    /// <c>IsBusy</c>) around the call and would otherwise have to notify again itself just to clear it,
    /// publishing an extra, misleading "finished but still loading" state in between. Runs after
    /// <paramref name="onFailure"/> on the failure path.
    /// </param>
    /// <returns>Whether the read succeeded.</returns>
    protected async Task<bool> LoadGuardedAsync(
        Func<CancellationToken, Task> operation,
        string description,
        CancellationToken cancellationToken,
        Action? onFailure = null,
        Action? beforeNotify = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await operation(cancellationToken);
            IsReachable = true;
            LastError = null;
            _unreachableIsARealOutage = false;
            return true;
        }
        catch (GrpcAdminException ex)
        {
            // Connectivity-aware, not blunt: a rejection reached the router, so IsReachable stays true and
            // the panel renders LastError inline instead of collapsing into a "router down" state that
            // would hide the actual message. This is IsReachable's documented meaning, which the stores
            // applied consistently on their mutation paths and inconsistently on their load paths.
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _unreachableIsARealOutage = ex.IsUnavailable;
            Logger?.LogWarning(exception: ex, message: "Admin load failed: could not {Operation}.",
                description);
            onFailure?.Invoke();
            return false;
        }
        finally
        {
            IsLoaded = true;
            beforeNotify?.Invoke();
            NotifyChanged();
        }
    }

    /// <summary>
    /// Records a completed operation: reachable, no outstanding error. Does <em>not</em> raise
    /// <see cref="Changed"/> — a mutation typically publishes fresh data first and notifies once at the end.
    /// </summary>
    /// <param name="marksLoaded">
    /// Whether this also counts as a completed load. False for a mutation that fetched nothing to display
    /// (clearing transcripts, say): <see cref="IsLoaded"/> means "there is data to render", and a successful
    /// side-effect that returned none must not claim otherwise.
    /// </param>
    protected void RecordSuccess(bool marksLoaded = true)
    {
        IsReachable = true;
        if (marksLoaded) IsLoaded = true;
        LastError = null;
        _unreachableIsARealOutage = false;
    }

    /// <summary>
    /// Reflects a failed mutation in this store's state before the caller rethrows, so
    /// <see cref="IsReachable"/> keeps its documented meaning after a mutation and not only after a load. A
    /// <em>rejection</em> never moves <see cref="IsReachable"/> on its own - starting unreachable stays
    /// unreachable, starting reachable stays reachable - with one exception: if the store is currently
    /// unreachable <em>because an earlier call actually failed to reach the router</em> (as opposed to
    /// simply never having succeeded yet, or a prior rejection having merely recorded a message), this
    /// rejection disproves that outage - the router answered, however badly - so it restores
    /// <see cref="IsReachable"/> to <see langword="true"/> and replaces the stale outage message rather than
    /// leaving both standing next to proof they are wrong. Every rejection is logged, the same as an
    /// outage, for the audit trail; <see cref="Changed"/> fires at most once, immediately after
    /// <paramref name="beforeNotify"/> runs, and is skipped entirely only when nothing observable changed
    /// and no <paramref name="beforeNotify"/> was given - so a run of identical background failures does not
    /// re-render the panel on every retry, while a caller that always has a transient flag to publish
    /// (<c>IsSaving</c>, <c>IsRunning</c>) still gets its one notification.
    /// </summary>
    /// <param name="exception">The mutation's failure.</param>
    /// <param name="description">
    /// A short lower-case phrase naming the operation for the log, e.g. <c>"a price-source operation"</c>.
    /// </param>
    /// <param name="recordRejectionMessage">
    /// Whether a <em>rejection</em> (a failure the router answered) also lands in
    /// <see cref="LastError"/>. False by default, because most panels render a rejection from the exception
    /// they catch and would otherwise show it twice. True for a caller whose UI reads
    /// <see cref="LastError"/> as its only error channel — the System Settings window does, and dropping the
    /// message there would leave a rejected save looking like it succeeded.
    /// </param>
    /// <param name="beforeNotify">
    /// Optional cleanup to run immediately before this call's own <see cref="Changed"/> notification - for
    /// a wrapper that holds a transient flag (<c>IsSaving</c>, <c>IsRunning</c>) around the call and would
    /// otherwise have to notify again itself just to clear it, publishing an extra, misleading "failed but
    /// still saving" state in between. Supplying this guarantees a notification even when this call's own
    /// fields did not change, since the caller's flag change still needs publishing exactly once.
    /// </param>
    protected void RecordFailure(GrpcAdminException exception, string description,
        bool recordRejectionMessage = false, Action? beforeNotify = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!exception.IsUnavailable)
        {
            var changed = false;

            if (_unreachableIsARealOutage)
            {
                IsReachable = true;
                LastError = recordRejectionMessage ? exception.Message : null;
                _unreachableIsARealOutage = false;
                changed = true;
            }
            else if (recordRejectionMessage && LastError != exception.Message)
            {
                LastError = exception.Message;
                changed = true;
            }

            Logger?.LogWarning(exception: exception, message: "The router rejected {Operation}.", description);

            if (changed || beforeNotify is not null)
            {
                beforeNotify?.Invoke();
                NotifyChanged();
            }

            return;
        }

        var alreadyUnreachableWithSameError = !IsReachable && LastError == exception.Message;

        IsReachable = false;
        LastError = exception.Message;
        _unreachableIsARealOutage = true;
        Logger?.LogWarning(exception: exception,
            message: "The router became unreachable during {Operation}.", description);

        if (!alreadyUnreachableWithSameError || beforeNotify is not null)
        {
            beforeNotify?.Invoke();
            NotifyChanged();
        }
    }
}
