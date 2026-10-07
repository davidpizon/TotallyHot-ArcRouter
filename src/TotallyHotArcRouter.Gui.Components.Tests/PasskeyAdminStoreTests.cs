using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="PasskeyAdminStore"/>: the gate status and lists, the three-step ceremonies (options from
/// the router, answer from the browser, verification back at the router), and what each outcome does to the
/// content grant.
/// </summary>
public sealed class PasskeyAdminStoreTests
{
    private static PasskeyAdminStore NewStore(
        out FakePasskeyAdminClient client,
        out FakeWebAuthnCeremony ceremony,
        out ContentGrantStore grant)
    {
        client = new FakePasskeyAdminClient();
        ceremony = new FakeWebAuthnCeremony();
        grant = new ContentGrantStore();
        return new PasskeyAdminStore(client, ceremony, grant);
    }

    [Fact]
    public async Task RefreshAsync_loads_status_passkeys_and_approvals()
    {
        var store = NewStore(out var client, out _, out _);
        client.Approvals = [new PasskeyApprovalInfo("content_unlock", "Windows Hello", DateTimeOffset.UtcNow, "succeeded")];

        await store.RefreshAsync(TestContext.Current.CancellationToken);

        store.IsLoaded.Should().BeTrue();
        store.IsReachable.Should().BeTrue();
        store.IsEnrolled.Should().BeTrue();
        store.Passkeys.Should().ContainSingle().Which.Name.Should().Be("Windows Hello");
        store.Approvals.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_when_the_router_is_down_swallows_the_failure_into_the_reachability_state()
    {
        var store = NewStore(out var client, out _, out _);
        client.Failure = new GrpcAdminException(message: "down", isUnavailable: true);

        await store.RefreshAsync(TestContext.Current.CancellationToken);

        store.IsReachable.Should().BeFalse();
        store.IsEnrolled.Should().BeFalse("an unknown gate reads as not enrolled, so the UI shows the hint");
    }

    [Fact]
    public void Before_the_first_refresh_the_enrollment_hint_falls_back_to_the_flag_name()
    {
        var store = NewStore(out _, out _, out _);

        store.EnrollmentCommandHint.Should().Be("--mint-passkey-enrollment-code");
    }

    [Fact]
    public async Task RefreshAsync_drops_a_local_grant_the_router_no_longer_recognises()
    {
        // A router restart empties its grant table; the dashboard still holds a token for it.
        var store = NewStore(out var client, out _, out var grant);
        grant.SetGrant("stale", DateTimeOffset.UtcNow.AddMinutes(10));
        client.Status = client.Status with { GrantActive = false };

        await store.RefreshAsync(TestContext.Current.CancellationToken);

        grant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_keeps_a_grant_the_router_confirms()
    {
        var store = NewStore(out var client, out _, out var grant);
        grant.SetGrant("good", DateTimeOffset.UtcNow.AddMinutes(10));
        client.Status = client.Status with { GrantActive = true };

        await store.RefreshAsync(TestContext.Current.CancellationToken);

        grant.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UnlockContentAsync_runs_the_ceremony_and_holds_the_grant_in_memory()
    {
        var store = NewStore(out var client, out var ceremony, out var grant);
        client.Grant = new ContentGrantInfo("the-grant", DateTimeOffset.UtcNow.AddMinutes(15));

        await store.UnlockContentAsync(TestContext.Current.CancellationToken);

        ceremony.GetOptions.Should().ContainSingle().Which.Should().Contain("unlock");
        client.Assertions.Should().ContainSingle().Which.Should().Contain("assertion");
        grant.Token.Should().Be("the-grant");
        store.GateStatus!.GrantActive.Should().BeTrue();
    }

    [Fact]
    public async Task UnlockContentAsync_a_dismissed_browser_prompt_sets_no_grant_and_never_reaches_the_router_verification()
    {
        var store = NewStore(out var client, out var ceremony, out var grant);
        ceremony.Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed or timed out.");

        var act = () => store.UnlockContentAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WebAuthnCeremonyException>();
        grant.IsActive.Should().BeFalse();
        client.Assertions.Should().BeEmpty();
    }

    [Fact]
    public async Task UnlockContentAsync_when_not_enrolled_rethrows_the_routers_message_and_sets_no_grant()
    {
        var store = NewStore(out var client, out _, out var grant);
        client.Failure = new GrpcAdminException(message: "Conversation content is locked until a passkey is enrolled.");

        var act = () => store.UnlockContentAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<GrpcAdminException>()).Which.Message.Should().Contain("enrolled");
        grant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task LockAsync_clears_the_local_grant_and_revokes_on_the_router()
    {
        var store = NewStore(out var client, out _, out var grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(10));
        var cleared = false;
        grant.Cleared += () => cleared = true;

        await store.LockAsync(TestContext.Current.CancellationToken);

        grant.IsActive.Should().BeFalse();
        cleared.Should().BeTrue("the conversation stores clear their text on this event");
        client.LockCalls.Should().Be(1);
    }

    [Fact]
    public async Task LockAsync_with_the_router_down_still_leaves_the_dashboard_locked()
    {
        var store = NewStore(out var client, out _, out var grant);
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(10));
        client.Failure = new GrpcAdminException(message: "down", isUnavailable: true);

        var act = () => store.LockAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GrpcAdminException>();
        grant.IsActive.Should().BeFalse("locking fails closed");
    }

    [Fact]
    public async Task AuthorizeOperationAsync_returns_the_single_use_authorization_for_that_operation()
    {
        var store = NewStore(out var client, out var ceremony, out _);

        var token = await store.AuthorizeOperationAsync(PasskeyOperations.GetManagementToken,
            PasskeyOperations.NoParameters, TestContext.Current.CancellationToken);

        token.Should().Be("authz-get_management_token");
        client.BegunOperations.Should().Equal("get_management_token");
        client.FinishedOperations.Should().Equal("get_management_token");
        ceremony.GetOptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task AuthorizeOperationAsync_a_dismissed_prompt_throws_and_finishes_nothing()
    {
        var store = NewStore(out var client, out var ceremony, out _);
        ceremony.Failure = new WebAuthnCeremonyException("dismissed");

        var act = () => store.AuthorizeOperationAsync(PasskeyOperations.RegenerateManagementToken,
            PasskeyOperations.NoParameters, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WebAuthnCeremonyException>();
        client.FinishedOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrollAsync_runs_the_registration_ceremony_and_refreshes_the_list()
    {
        var store = NewStore(out var client, out var ceremony, out _);
        client.Passkeys = [];
        client.Status = client.Status with { Enrolled = false };
        await store.RefreshAsync(TestContext.Current.CancellationToken);
        store.IsEnrolled.Should().BeFalse();

        var enrolled = await store.EnrollAsync(" ABCD-EFGH ", "Laptop", TestContext.Current.CancellationToken);

        enrolled.Name.Should().Be("Laptop");
        client.EnrollmentCodes.Should().Equal("ABCD-EFGH");
        ceremony.CreateOptions.Should().ContainSingle();
        store.Passkeys.Should().ContainSingle().Which.Name.Should().Be("Laptop");
        store.IsEnrolled.Should().BeTrue();
    }

    [Fact]
    public async Task EnrollAsync_a_rejected_code_rethrows_without_opening_the_browser_prompt()
    {
        var store = NewStore(out var client, out var ceremony, out _);
        client.Failure = new GrpcAdminException(message: "The enrollment code is invalid or has expired.");

        var act = () => store.EnrollAsync("WRONG", "Laptop", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GrpcAdminException>();
        ceremony.CreateOptions.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrollAsync_a_blank_code_throws()
    {
        var store = NewStore(out _, out _, out _);

        var act = () => store.EnrollAsync("  ", "Laptop", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
