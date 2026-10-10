using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TestContext = Xunit.TestContext;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ApprovalRequestsDialog"/> (#165 phase 3, PR 3c; ADR-0020 Amendment 1): that it is built on
/// <c>DialogShell</c>, lists each pending request with its destination and filter exactly as the router holds them,
/// runs the passkey ceremony for the one the operator picks, can deny without a passkey, highlights the request a
/// printed link pointed at, and says plainly when that request is gone.
/// </summary>
public sealed class ApprovalRequestsDialogTests
{
    private static PendingApprovalInfo Pending(string id, string destination = "/exports/out.zip",
        ConversationExportFilterInfo? filter = null) => new(
        ApprovalId: id,
        Filter: filter ?? new ConversationExportFilterInfo(),
        DestinationPath: destination,
        CreatedAtUtc: new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
        ExpiresAtUtc: new DateTimeOffset(2026, 10, 10, 12, 5, 30, TimeSpan.Zero));

    private static BunitContext NewContext(
        out FakePasskeyAdminClient passkeys, out FakeWebAuthnCeremony ceremony, params PendingApprovalInfo[] pending)
    {
        var ctx = new BunitContext();
        passkeys = new FakePasskeyAdminClient { Pending = pending };
        ceremony = new FakeWebAuthnCeremony();
        ctx.Services.AddSingleton(new PasskeyAdminStore(passkeys, ceremony, new ContentGrantStore()));
        return ctx;
    }

    private static Task Click(IRenderedComponent<ApprovalRequestsDialog> cut, string selector) =>
        cut.InvokeAsync(() => cut.Find(selector).Click());

    [Fact]
    public void Is_built_on_the_dialog_shell()
    {
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        cut.Find("div.overlay-backdrop").Should().NotBeNull();
        cut.Find("div.overlay-panel span.uppercase").TextContent.Should().Be("Approvals");
        cut.Find("button[aria-label='Close approvals dialog']").Should().NotBeNull();
    }

    [Fact]
    public void Shows_an_empty_state_when_nothing_is_waiting()
    {
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        cut.Find("[data-testid='approvals-empty']").TextContent.Should().Contain("No approvals are waiting");
    }

    [Fact]
    public void Lists_each_request_with_its_destination_filter_and_expiry()
    {
        using var ctx = NewContext(out _, out _,
            Pending("a1", destination: @"C:\exports\a.zip",
                filter: new ConversationExportFilterInfo(
                    From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Harness: "claude-code", Model: "opus")),
            Pending("a2", destination: @"C:\exports\b.zip"));

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        cut.FindAll("[data-testid='approval-destination']").Select(e => e.TextContent)
            .Should().Equal(@"C:\exports\a.zip", @"C:\exports\b.zip");
        cut.FindAll("[data-testid='approval-filter']").Select(e => e.TextContent).Should().Equal(
            "from 2026-10-01 00:00:00 UTC, harness claude-code, model opus", "every captured turn");
        cut.FindAll("[data-testid='approval-expires']")[0].TextContent.Should().Contain("12:05:30 UTC");
        cut.FindAll("[data-testid='approvals-empty']").Should().BeEmpty();
    }

    [Fact]
    public async Task Approving_runs_the_passkey_ceremony_for_that_request_and_removes_it()
    {
        using var ctx = NewContext(out var passkeys, out var ceremony, Pending("a1"), Pending("a2"));
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid='approval-approve']")[1].Click());

        passkeys.BegunApprovals.Should().Equal("a2");
        passkeys.ApprovedApprovals.Should().Equal("a2");
        ceremony.GetOptions.Should().ContainSingle();
        cut.FindAll("[data-testid='approval-destination']").Should().ContainSingle();
        cut.FindAll("[data-testid^='approval-a2']").Should().BeEmpty();
    }

    [Fact]
    public async Task A_dismissed_passkey_prompt_is_shown_inline_and_approves_nothing()
    {
        using var ctx = NewContext(out var passkeys, out var ceremony, Pending("a1"));
        ceremony.Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed.");
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        await Click(cut, "[data-testid='approval-approve']");

        cut.Find("[data-testid='approvals-error']").TextContent.Should().Contain("dismissed");
        passkeys.ApprovedApprovals.Should().BeEmpty();
        cut.FindAll("[data-testid='approval-approve']").Should().ContainSingle("the request stays open to retry");
    }

    [Fact]
    public async Task A_request_that_lapsed_meanwhile_shows_the_routers_refusal_and_rereads_the_list()
    {
        using var ctx = NewContext(out var passkeys, out _, Pending("a1"));
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));
        passkeys.DecisionFailure = new GrpcAdminException("This approval request is no longer waiting.");
        passkeys.Pending = [];

        await Click(cut, "[data-testid='approval-approve']");

        cut.Find("[data-testid='approvals-error']").TextContent.Should().Contain("no longer waiting");
        cut.FindAll("[data-testid='approval-destination']").Should().BeEmpty();
    }

    [Fact]
    public async Task Denying_refuses_the_request_without_opening_a_passkey_prompt()
    {
        using var ctx = NewContext(out var passkeys, out var ceremony, Pending("a1"));
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        await Click(cut, "[data-testid='approval-deny']");

        passkeys.DeniedApprovals.Should().Equal("a1");
        ceremony.GetOptions.Should().BeEmpty();
        cut.FindAll("[data-testid='approval-destination']").Should().BeEmpty();
    }

    [Fact]
    public void Highlights_the_request_a_link_pointed_at()
    {
        using var ctx = NewContext(out _, out _, Pending("a1"), Pending("a2"));

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p
            .Add(c => c.OnClose, () => { })
            .Add(c => c.FocusApprovalId, "a2"));

        cut.Find("[data-testid='approval-a2']").ClassList.Should().Contain("border-sky-500");
        cut.Find("[data-testid='approval-a1']").ClassList.Should().NotContain("border-sky-500");
        cut.FindAll("[data-testid='approvals-focus-missing']").Should().BeEmpty();
    }

    [Fact]
    public void Says_so_when_the_request_in_the_link_is_no_longer_waiting()
    {
        using var ctx = NewContext(out _, out _, Pending("a1"));

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p
            .Add(c => c.OnClose, () => { })
            .Add(c => c.FocusApprovalId, "gone"));

        cut.Find("[data-testid='approvals-focus-missing']").TextContent.Should().Contain("no longer waiting");
    }

    [Fact]
    public void Disables_approval_and_shows_the_enrollment_hint_when_no_passkey_is_enrolled()
    {
        using var ctx = NewContext(out var passkeys, out _, Pending("a1"));
        passkeys.Status = passkeys.Status with { Enrolled = false };

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        cut.Find("[data-testid='approvals-enrollment-hint']").TextContent.Should().Contain("--mint-passkey-enrollment-code");
        cut.Find("[data-testid='approval-approve']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Says_so_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(out var passkeys, out _);
        passkeys.Failure = new GrpcAdminException("not reachable", isUnavailable: true);

        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));

        cut.Find("[data-testid='approvals-unreachable']").TextContent.Should().Contain("Could not reach the router");
    }

    [Fact]
    public async Task Refresh_picks_up_a_request_filed_after_the_dialog_opened()
    {
        using var ctx = NewContext(out var passkeys, out _);
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => { }));
        cut.FindAll("[data-testid='approval-destination']").Should().BeEmpty();
        passkeys.Pending = [Pending("a1")];

        await Click(cut, "[data-testid='approvals-refresh']");

        cut.FindAll("[data-testid='approval-destination']").Should().ContainSingle();
    }

    [Fact]
    public async Task Done_runs_OnClose()
    {
        using var ctx = NewContext(out _, out _);
        var closed = 0;
        var cut = ctx.Render<ApprovalRequestsDialog>(p => p.Add(c => c.OnClose, () => closed++));

        await Click(cut, "[data-testid='approvals-done']");

        closed.Should().Be(1);
    }
}
