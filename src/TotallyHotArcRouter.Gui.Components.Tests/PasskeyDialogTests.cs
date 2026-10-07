using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TestContext = Xunit.TestContext;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="UnlockContentDialog"/>, <see cref="AddPasskeyDialog"/> and <see cref="ContentGateBar"/>
/// (ADR-0020): that each builds on <c>DialogShell</c>, drives the right store operation, reports a failed
/// ceremony inline, and - because the bUnit JS runtime is left in strict mode - that none of them touches
/// browser JavaScript directly, so the grant cannot reach <c>localStorage</c>, <c>sessionStorage</c> or a
/// cookie by way of these components.
/// </summary>
public sealed class PasskeyDialogTests
{
    private static BunitContext NewContext(
        out FakePasskeyAdminClient client,
        out FakeWebAuthnCeremony ceremony,
        out ContentGrantStore grant)
    {
        var ctx = new BunitContext();
        client = new FakePasskeyAdminClient();
        ceremony = new FakeWebAuthnCeremony();
        grant = new ContentGrantStore();
        var store = new PasskeyAdminStore(client, ceremony, grant);
        ctx.Services.AddSingleton(store);
        return ctx;
    }

    [Fact]
    public void UnlockContentDialog_is_built_on_the_dialog_shell()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<UnlockContentDialog>();

        cut.Find("div.overlay-backdrop").Should().NotBeNull();
        cut.Find("div.overlay-panel").Should().NotBeNull();
        cut.Markup.Should().Contain("Unlock Conversations");
        cut.Find("button[aria-label='Close unlock conversations dialog']").Should().NotBeNull();
    }

    [Fact]
    public async Task UnlockContentDialog_unlocking_holds_the_grant_and_reports_success_without_any_js_interop()
    {
        using var ctx = NewContext(out var client, out _, out var grant);
        client.Grant = new ContentGrantInfo("g-1", DateTimeOffset.UtcNow.AddMinutes(15));
        var unlocked = false;
        await ctx.Services.GetRequiredService<PasskeyAdminStore>().RefreshAsync(TestContext.Current.CancellationToken);
        var cut = ctx.Render<UnlockContentDialog>(p => p.Add(c => c.OnUnlocked, () => unlocked = true));

        await cut.InvokeAsync(() => cut.Find("[data-testid='unlock-confirm']").Click());

        unlocked.Should().BeTrue();
        grant.Token.Should().Be("g-1");
        ctx.JSInterop.Invocations.Should().BeEmpty("the grant is memory-only; no storage API is ever called");
    }

    [Fact]
    public async Task UnlockContentDialog_a_failed_ceremony_shows_the_reason_and_does_not_report_success()
    {
        using var ctx = NewContext(out _, out var ceremony, out var grant);
        ceremony.Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed or timed out.");
        var unlocked = false;
        await ctx.Services.GetRequiredService<PasskeyAdminStore>().RefreshAsync(TestContext.Current.CancellationToken);
        var cut = ctx.Render<UnlockContentDialog>(p => p.Add(c => c.OnUnlocked, () => unlocked = true));

        await cut.InvokeAsync(() => cut.Find("[data-testid='unlock-confirm']").Click());

        cut.Find("[data-testid='unlock-error']").TextContent.Should().Contain("dismissed or timed out");
        unlocked.Should().BeFalse();
        grant.IsActive.Should().BeFalse();
    }

    [Fact]
    public void UnlockContentDialog_before_enrollment_names_the_enrollment_command_and_disables_unlock()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<UnlockContentDialog>();

        // The store has not refreshed yet, so the gate is unknown and reads as not enrolled.
        cut.Find("[data-testid='unlock-enrollment-hint']").TextContent.Should().Contain("--mint-passkey-enrollment-code");
        cut.Find("[data-testid='unlock-confirm']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void AddPasskeyDialog_is_built_on_the_dialog_shell_and_names_the_enrollment_command()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<AddPasskeyDialog>();

        cut.Find("div.overlay-backdrop").Should().NotBeNull();
        cut.Find("div.overlay-panel").Should().NotBeNull();
        cut.Markup.Should().Contain("Add Passkey").And.Contain("--mint-passkey-enrollment-code");
    }

    [Fact]
    public void AddPasskeyDialog_cannot_submit_without_a_code()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<AddPasskeyDialog>();

        cut.Find("[data-testid='add-passkey-confirm']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task AddPasskeyDialog_enrolls_with_the_pasted_code_and_chosen_name()
    {
        using var ctx = NewContext(out var client, out var ceremony, out _);
        var enrolled = false;
        var cut = ctx.Render<AddPasskeyDialog>(p => p.Add(c => c.OnEnrolled, () => enrolled = true));
        cut.Find("[data-testid='add-passkey-code']").Input("ABCD-EFGH");
        cut.Find("[data-testid='add-passkey-name']").Input("Laptop");

        await cut.InvokeAsync(() => cut.Find("[data-testid='add-passkey-confirm']").Click());

        client.EnrollmentCodes.Should().Equal("ABCD-EFGH");
        client.EnrolledNames.Should().Equal("Laptop");
        ceremony.CreateOptions.Should().ContainSingle();
        enrolled.Should().BeTrue();
        ctx.JSInterop.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task AddPasskeyDialog_a_rejected_code_is_shown_inline_and_the_dialog_stays_open()
    {
        using var ctx = NewContext(out var client, out var ceremony, out _);
        client.Failure = new GrpcAdminException(message: "Could not start the passkey enrollment: The enrollment code is invalid or has expired.");
        var enrolled = false;
        var cut = ctx.Render<AddPasskeyDialog>(p => p.Add(c => c.OnEnrolled, () => enrolled = true));
        cut.Find("[data-testid='add-passkey-code']").Input("WRONG");

        await cut.InvokeAsync(() => cut.Find("[data-testid='add-passkey-confirm']").Click());

        cut.Find("[data-testid='add-passkey-error']").TextContent.Should().Contain("invalid or has expired");
        enrolled.Should().BeFalse();
        ceremony.CreateOptions.Should().BeEmpty("the browser prompt never opens for a rejected code");
    }

    [Fact]
    public void ContentGateBar_locked_and_enrolled_offers_unlock()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ContentGateBar>(p => p.Add(c => c.Locked, true));

        cut.Find("[data-testid='content-unlock']").Should().NotBeNull();
        cut.FindAll("[data-testid='content-add-passkey']").Should().BeEmpty();
    }

    [Fact]
    public void ContentGateBar_locked_and_not_enrolled_names_the_command_and_offers_add_passkey_instead_of_unlock()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ContentGateBar>(p => p
            .Add(c => c.Locked, true)
            .Add(c => c.ShowEnrollmentHint, true));

        cut.Find("[data-testid='content-enrollment-hint']").TextContent.Should().Contain("--mint-passkey-enrollment-code");
        cut.Find("[data-testid='content-add-passkey']").Should().NotBeNull();
        cut.FindAll("[data-testid='content-unlock']").Should().BeEmpty();
    }

    [Fact]
    public void ContentGateBar_unlocked_shows_the_expiry_and_a_lock_control()
    {
        using var ctx = new BunitContext();
        var locked = false;

        var cut = ctx.Render<ContentGateBar>(p => p
            .Add(c => c.Locked, false)
            .Add(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(10))
            .Add(c => c.OnLock, () => locked = true));
        cut.Find("[data-testid='content-lock']").Click();

        cut.Find("[data-testid='content-unlocked']").TextContent.Should().Contain("until");
        locked.Should().BeTrue();
    }
}
