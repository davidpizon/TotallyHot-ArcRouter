using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ContentGrantStore"/>: the in-memory content grant (ADR-0020) - what Set and Clear
/// do, that expiry locks without any caller involvement, and which events fire when.
/// </summary>
public sealed class ContentGrantStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_store_holds_no_grant()
    {
        var store = new ContentGrantStore();

        store.IsActive.Should().BeFalse();
        store.Token.Should().BeNull();
        store.ExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void SetGrant_makes_the_grant_active_and_raises_Granted_and_Changed()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new ContentGrantStore(clock);
        var granted = 0;
        var changed = 0;
        store.Granted += () => granted++;
        store.Changed += () => changed++;

        store.SetGrant("tok", Start.AddMinutes(15));

        store.IsActive.Should().BeTrue();
        store.Token.Should().Be("tok");
        store.ExpiresAtUtc.Should().Be(Start.AddMinutes(15));
        granted.Should().Be(1);
        changed.Should().Be(1);
    }

    [Fact]
    public void Clear_drops_the_grant_and_raises_Cleared_and_Changed()
    {
        var store = new ContentGrantStore(new ManualTimeProvider(Start));
        store.SetGrant("tok", Start.AddMinutes(15));
        var cleared = 0;
        var changed = 0;
        store.Cleared += () => cleared++;
        store.Changed += () => changed++;

        store.Clear();

        store.IsActive.Should().BeFalse();
        store.Token.Should().BeNull();
        cleared.Should().Be(1);
        changed.Should().Be(1);
    }

    [Fact]
    public void Clear_with_no_grant_held_raises_nothing()
    {
        var store = new ContentGrantStore(new ManualTimeProvider(Start));
        var raised = false;
        store.Cleared += () => raised = true;
        store.Changed += () => raised = true;

        store.Clear();

        raised.Should().BeFalse();
    }

    [Fact]
    public void The_grant_locks_by_itself_when_it_expires()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new ContentGrantStore(clock);
        store.SetGrant("tok", Start.AddMinutes(15));
        var cleared = 0;
        store.Cleared += () => cleared++;

        clock.Advance(TimeSpan.FromMinutes(14));
        store.IsActive.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(1));

        store.IsActive.Should().BeFalse();
        store.Token.Should().BeNull();
        cleared.Should().Be(1, "expiry runs the same clear path as an explicit Lock");
    }

    [Fact]
    public void An_expired_grant_is_never_handed_out_even_before_the_timer_fires()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new ContentGrantStore(clock);
        store.SetGrant("tok", Start.AddMinutes(1));

        // Move the clock without firing timers: the Token read itself must refuse an expired token, so a call
        // racing the timer never sends a header the router would reject.
        clock.SetNowWithoutFiringTimers(Start.AddMinutes(2));

        store.Token.Should().BeNull();
        store.IsActive.Should().BeFalse();
    }

    [Fact]
    public void SetGrant_replaces_the_previous_grant_and_its_timer()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new ContentGrantStore(clock);
        store.SetGrant("first", Start.AddMinutes(5));

        store.SetGrant("second", Start.AddMinutes(15));

        store.Token.Should().Be("second");
        clock.LiveTimerCount.Should().Be(1, "the first grant's timer must not outlive it");

        clock.Advance(TimeSpan.FromMinutes(6));
        store.IsActive.Should().BeTrue("the replaced grant's expiry must not lock out the newer one");
    }

    [Fact]
    public void SetGrant_with_an_already_expired_time_is_treated_as_a_lock()
    {
        var store = new ContentGrantStore(new ManualTimeProvider(Start));
        store.SetGrant("old", Start.AddMinutes(5));

        store.SetGrant("late", Start.AddSeconds(-1));

        store.IsActive.Should().BeFalse();
    }

    [Fact]
    public void SetGrant_with_a_blank_token_throws()
    {
        var store = new ContentGrantStore();

        var act = () => store.SetGrant(string.Empty, DateTimeOffset.UtcNow.AddMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose_drops_the_grant_and_stops_the_timer()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new ContentGrantStore(clock);
        store.SetGrant("tok", Start.AddMinutes(15));

        store.Dispose();

        store.IsActive.Should().BeFalse();
        clock.LiveTimerCount.Should().Be(0);
    }
}
