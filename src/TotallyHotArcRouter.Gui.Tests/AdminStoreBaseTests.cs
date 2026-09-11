using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="AdminStoreBase{TClient}"/>, the shared shape behind the eleven gRPC-backed
/// Governance and System Settings stores.
/// </summary>
/// <remarks>
/// These pin the rules that were previously re-implemented once per store and therefore never asserted in
/// one place: that a failed load still raises <c>Changed</c> and still sets <c>IsLoaded</c> (otherwise a
/// panel is stuck on a spinner forever when the router is down), and that a <em>rejection</em> leaves
/// <c>IsReachable</c> true so the panel renders the message inline rather than collapsing into a "router
/// down" state that hides it.
/// </remarks>
public sealed class AdminStoreBaseTests
{
    private static GrpcAdminException Unavailable()
    {
        return new GrpcAdminException(message: "the router is not reachable.", isUnavailable: true);
    }

    private static GrpcAdminException Rejected()
    {
        return new GrpcAdminException("no price source named 'nope'.");
    }

    [Fact]
    public async Task A_successful_load_marks_the_store_loaded_and_reachable_and_notifies_once()
    {
        var store = new TestStore();
        var notifications = 0;
        store.Changed += () => notifications++;

        await store.LoadAsync();

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        store.LastError.Should().BeNull();
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_load_still_raises_Changed_and_still_marks_the_store_loaded()
    {
        var store = new TestStore { Failure = Unavailable() };
        var notifications = 0;
        store.Changed += () => notifications++;

        await store.LoadAsync();

        // The whole point: a panel that never hears about the failure renders its spinner forever.
        notifications.Should().Be(1);
        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().Be("the router is not reachable.");
    }

    [Fact]
    public async Task A_rejected_load_keeps_the_store_reachable_and_surfaces_the_message()
    {
        var store = new TestStore { Failure = Rejected() };

        await store.LoadAsync();

        store.IsReachable.Should().BeTrue();
        store.LastError.Should().Be("no price source named 'nope'.");
    }

    [Fact]
    public async Task A_failed_load_runs_its_cleanup_before_notifying()
    {
        var store = new TestStore { Failure = Rejected() };
        string? valueSeenBySubscriber = null;
        store.Changed += () => valueSeenBySubscriber = store.Value;

        await store.LoadAsync(clearValueOnFailure: true);

        // Ordering, not just the eventual value: a subscriber woken before the cleanup would render data
        // the store has already decided to distrust.
        valueSeenBySubscriber.Should().BeNull();
    }

    [Fact]
    public async Task A_successful_load_runs_beforeNotify_before_its_single_notification()
    {
        var store = new TestStore();
        var isBusy = true;
        var notifications = 0;
        bool? isBusySeenBySubscriber = null;
        store.Changed += () =>
        {
            notifications++;
            isBusySeenBySubscriber = isBusy;
        };

        // Mirrors how JudgeCalibrationAdminStore.LoadAsync/UpdateStore.RunBusyAsync clear their own
        // transient IsLoading/IsBusy flag: passing it as beforeNotify folds that into this call's one
        // notification instead of a separate wrapper-owned notify that would either fire before this one
        // (an intermediate "finished but still busy" state) or need a second Changed subscription entirely.
        await store.LoadAsync(beforeNotify: () => isBusy = false);

        notifications.Should().Be(1);
        isBusySeenBySubscriber.Should().BeFalse(
            "beforeNotify must run before Changed fires, not after");
    }

    [Fact]
    public void A_rejected_mutation_does_not_blank_the_panel_but_an_unavailable_one_does()
    {
        var store = new TestStore();

        store.Fail(Rejected());
        store.IsReachable.Should().BeFalse("IsReachable starts false and a rejection must not flip it true");
        store.LastError.Should().BeNull("a rejection is rendered from the thrown exception, not LastError");

        store.RecordReached();
        store.Fail(Rejected());
        store.IsReachable.Should().BeTrue("the router answered, so it is emphatically reachable");

        store.Fail(Unavailable());
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().Be("the router is not reachable.");
    }

    [Fact]
    public void A_rejection_after_a_real_outage_restores_reachability_and_clears_the_stale_message()
    {
        var store = new TestStore();
        store.Fail(Unavailable());
        store.IsReachable.Should().BeFalse();
        store.LastError.Should().Be("the router is not reachable.");

        // The router just answered - however badly - which disproves the outage this store is still
        // reporting. Leaving IsReachable false or the old message standing here is exactly the stale
        // "Router unreachable" panel a live router must never show.
        store.Fail(Rejected());

        store.IsReachable.Should().BeTrue("a rejection is proof the router answered, overriding the outage");
        store.LastError.Should().BeNull("the stale outage message must not survive next to proof it is wrong");
    }

    [Fact]
    public void Consecutive_rejections_with_recordRejectionMessage_never_flip_reachability()
    {
        // Regression for a real bug in an earlier version of this fix: using "LastError is non-null" as the
        // outage marker broke here, because recordRejectionMessage also writes a rejection's own message
        // into LastError. That made the *second* rejection look indistinguishable from "recovering from a
        // real outage" and incorrectly flip IsReachable true - depending on how many times the same
        // rejection had already happened. Only an actually-observed Unavailable failure may do that.
        var store = new TestStore();

        store.Fail(exception: Rejected(), recordRejectionMessage: true);
        store.IsReachable.Should().BeFalse("no outage has ever been observed - this store is still virgin");
        store.LastError.Should().Be("no price source named 'nope'.");

        store.Fail(exception: Rejected(), recordRejectionMessage: true);
        store.IsReachable.Should().BeFalse(
            "a second rejection is no more proof of an outage-recovery than the first one was");
        store.LastError.Should().Be("no price source named 'nope'.");
    }

    [Fact]
    public void A_failure_with_beforeNotify_always_notifies_exactly_once()
    {
        // Regression for a real double-notify bug: a mutation store used to call RecordFailure in its catch
        // (one notification, when anything changed) and then clear its own IsSaving/IsRunning/etc. flag in a
        // finally that notified again - so a failed mutation woke subscribers twice, the first time with the
        // busy flag still true. beforeNotify folds the flag-clear into RecordFailure's own single
        // notification instead, and - unlike a plain repeated-failure dedup - must fire even when nothing
        // else about this call changed, because the caller's flag transition alone is a real, unpublished
        // state change.
        var store = new TestStore();
        var isSaving = true;
        var notifications = 0;
        bool? isSavingSeenBySubscriber = null;
        store.Changed += () =>
        {
            notifications++;
            isSavingSeenBySubscriber = isSaving;
        };

        // A rejection with no recordRejectionMessage and no prior outage changes nothing about the store's
        // own fields - exactly the case that would otherwise be deduped into silence.
        store.Fail(Rejected(), beforeNotify: () => isSaving = false);

        notifications.Should().Be(1);
        isSavingSeenBySubscriber.Should().BeFalse("beforeNotify must run before Changed fires, not after");
    }

    [Fact]
    public void Repeated_identical_unavailable_failures_notify_only_once()
    {
        var store = new TestStore();
        var notifications = 0;
        store.Changed += () => notifications++;

        store.Fail(Unavailable());
        store.Fail(Unavailable());
        store.Fail(Unavailable());

        notifications.Should().Be(1,
            "nothing about the store's observable state changed on the second or third identical failure");
    }

    [Fact]
    public void A_rejected_mutation_records_its_message_when_the_caller_asks_for_it()
    {
        var store = new TestStore();

        store.Fail(exception: Rejected(), recordRejectionMessage: true);

        store.IsReachable.Should().BeFalse("a rejection never moves reachability either way on its own");
        store.LastError.Should().Be("no price source named 'nope'.",
            "the System Settings window reads LastError as its only error channel");
    }

    [Fact]
    public void Disposing_releases_what_the_store_built_but_not_what_it_was_handed()
    {
        var built = new TrackedDisposable();
        var handed = new TrackedDisposable();
        var store = new TestStore(handed);
        store.TakeOwnershipOf(built);

        store.Dispose();
        store.Dispose();

        built.DisposeCount.Should().Be(1, "Dispose must be idempotent");
        handed.DisposeCount.Should().Be(0, "a caller-supplied client outlives the store that borrowed it");
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    /// <summary>A minimal concrete store, standing in for the eleven real ones.</summary>
    private sealed class TestStore(object? client = null) : AdminStoreBase<object>(client ?? new object(), null)
    {
        public GrpcAdminException? Failure { get; init; }

        public string? Value { get; private set; } = "stale";

        public Task LoadAsync(bool clearValueOnFailure = false, Action? beforeNotify = null)
        {
            return LoadGuardedAsync(
                _ =>
                {
                    if (Failure is not null) throw Failure;
                    Value = "fresh";
                    return Task.CompletedTask;
                },
                "do the thing",
                CancellationToken.None,
                onFailure: clearValueOnFailure ? () => Value = null : null,
                beforeNotify: beforeNotify);
        }

        public void Fail(GrpcAdminException exception, bool recordRejectionMessage = false,
            Action? beforeNotify = null)
        {
            RecordFailure(exception: exception, description: "a test operation",
                recordRejectionMessage: recordRejectionMessage, beforeNotify: beforeNotify);
        }

        public void RecordReached()
        {
            RecordSuccess();
        }

        public void TakeOwnershipOf(IDisposable resource)
        {
            Own(resource);
        }
    }
}
