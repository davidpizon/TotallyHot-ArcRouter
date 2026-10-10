using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TestContext = Xunit.TestContext;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ExportDialog"/> (#165 phase 3, PR 3b): that it is built on <c>DialogShell</c> with an
/// Export-only title, shows capture state and store counts, keeps Export disabled until there is a destination,
/// earns a passkey approval bound to exactly the filter and destination it then sends, shows progress and the
/// result path, shows a router failure inline, and runs <c>OnClose</c> when closed.
/// </summary>
public sealed class ExportDialogTests
{
    private const string Destination = @"C:\exports\conversations.zip";

    private static BunitContext NewContext(
        out FakeConversationAdminClient conversations,
        out FakePasskeyAdminClient passkeys,
        out FakeWebAuthnCeremony ceremony,
        bool captureEnabled = true)
    {
        var ctx = new BunitContext();
        conversations = new FakeConversationAdminClient();
        passkeys = new FakePasskeyAdminClient();
        ceremony = new FakeWebAuthnCeremony();
        ctx.Services.AddSingleton(new ConversationExportStore(conversations));
        ctx.Services.AddSingleton(new PasskeyAdminStore(passkeys, ceremony, new ContentGrantStore()));
        ctx.Services.AddSingleton(new RouterSettingsAdminStore(new FakeRouterSettingsClient(captureEnabled)));
        return ctx;
    }

    private static async Task Enter(IRenderedComponent<ExportDialog> cut, string testId, string value) =>
        await cut.Find($"[data-testid='{testId}']").InputAsync(value);

    private static Task ClickExport(IRenderedComponent<ExportDialog> cut) =>
        cut.InvokeAsync(() => cut.Find("[data-testid='export-confirm']").Click());

    [Fact]
    public void Is_built_on_the_dialog_shell_and_is_titled_Export_only()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<ExportDialog>();

        cut.Find("div.overlay-backdrop").Should().NotBeNull();
        cut.Find("div.overlay-panel").Should().NotBeNull();
        cut.Find("div.overlay-panel span.uppercase").TextContent.Should().Be("Export");
        cut.Find("button[aria-label='Close export dialog']").Should().NotBeNull();
        cut.Markup.Should().NotContain("Import");
    }

    [Fact]
    public void Shows_the_capture_state_the_counts_the_bytes_on_disk_and_the_sample_size()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<ExportDialog>();

        cut.Find("[data-testid='export-capture-state']").TextContent.Trim().Should().Be("on");
        cut.Find("[data-testid='export-session-count']").TextContent.Should().Be("3");
        cut.Find("[data-testid='export-turn-count']").TextContent.Should().Be("1,250");
        cut.Find("[data-testid='export-bytes-on-disk']").TextContent.Should().Be("5.0 MB");
        cut.Find("[data-testid='export-sample-size']").TextContent.Should().Contain("20,000");
    }

    [Fact]
    public void Shows_capture_off_when_the_router_has_it_off()
    {
        using var ctx = NewContext(out _, out _, out _, captureEnabled: false);

        var cut = ctx.Render<ExportDialog>();

        cut.Find("[data-testid='export-capture-state']").TextContent.Trim().Should().Be("off");
    }

    [Fact]
    public void Says_so_when_the_router_cannot_be_reached()
    {
        using var ctx = NewContext(out var conversations, out _, out _);
        conversations.SummaryFailure = new GrpcAdminException("not reachable", isUnavailable: true);

        var cut = ctx.Render<ExportDialog>();

        cut.Find("[data-testid='export-unreachable']").TextContent.Should().Contain("Could not reach the router");
        cut.FindAll("[data-testid='export-summary']").Should().BeEmpty();
    }

    [Fact]
    public async Task Export_is_disabled_until_there_is_a_destination()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var cut = ctx.Render<ExportDialog>();

        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeTrue();

        await Enter(cut, "export-destination", "   ");
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeTrue("a blank path is no destination");

        await Enter(cut, "export-destination", Destination);
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Export_is_disabled_and_the_enrollment_command_is_named_when_no_passkey_is_enrolled()
    {
        await using var ctx = NewContext(out _, out var passkeys, out _);
        passkeys.Passkeys = [];
        passkeys.Status = passkeys.Status with { Enrolled = false };
        var cut = ctx.Render<ExportDialog>();

        await Enter(cut, "export-destination", Destination);

        cut.Find("[data-testid='export-enrollment-hint']").TextContent.Should().Contain("--mint-passkey-enrollment-code");
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Export_runs_one_ceremony_bound_to_exactly_the_filter_and_destination_it_then_sends()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out var ceremony);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-from", "2026-10-01T08:30");
        await Enter(cut, "export-to", "2026-10-09T17:45");
        await Enter(cut, "export-session", " session-1 ");
        await Enter(cut, "export-harness", "claude-code");
        await Enter(cut, "export-provider", "anthropic");
        await Enter(cut, "export-model", "claude-x");
        await Enter(cut, "export-destination", $"  {Destination}  ");

        await ClickExport(cut);

        var expectedFilter = new ConversationExportFilterInfo(
            From: new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 10, 9, 17, 45, 0, TimeSpan.Zero),
            SessionId: "session-1",
            Harness: "claude-code",
            Provider: "anthropic",
            Model: "claude-x");
        conversations.Filters.Should().Equal(expectedFilter);
        conversations.Destinations.Should().Equal(Destination);
        passkeys.BegunOperations.Should().Equal(PasskeyOperations.Export);
        ceremony.GetOptions.Should().ContainSingle();

        // The approval is bound to the digest of what the RPC then receives, so the router accepts it.
        var bound = PasskeyOperations.ExportParameters(conversations.Filters[0], conversations.Destinations[0]);
        passkeys.BegunParameters.Should().Equal(bound);
        passkeys.FinishedParameters.Should().Equal(bound);
        conversations.Authorizations.Should().Equal("authz-export");
    }

    [Fact]
    public async Task Export_with_no_filter_binds_the_approval_to_the_empty_filter()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out _);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        conversations.Filters.Should().Equal(new ConversationExportFilterInfo());
        passkeys.BegunParameters.Should().Equal(
            PasskeyOperations.ExportParameters(new ConversationExportFilterInfo(), Destination));
    }

    [Fact]
    public async Task A_finished_export_shows_the_result_path_and_counts()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.Result = conversations.Result with { MissingBodies = 2, CorruptTurns = 1 };
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        cut.Find("[data-testid='export-result-path']").TextContent.Should().Be(Destination);
        cut.Find("[data-testid='export-result-counts']").TextContent.Should().Contain("7 turns from 3 conversations");
        cut.Find("[data-testid='export-result-warnings']").TextContent.Should().Contain("2 missing bodies").And.Contain("1 corrupt turns");
        cut.FindAll("[data-testid='export-confirm']").Should().BeEmpty("the form is replaced by the result");
    }

    [Fact]
    public async Task Shows_progress_while_the_export_runs()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        var hold = new TaskCompletionSource();
        conversations.HoldBeforeResult = hold.Task;
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        var running = cut.Find("[data-testid='export-confirm']").ClickAsync();

        cut.WaitForAssertion(() => cut.Find("[data-testid='export-progress']").TextContent
            .Should().Contain("2 turns").And.Contain("2.0 KB"), timeout: TimeSpan.FromSeconds(5));
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeTrue("one export at a time");

        hold.SetResult();
        await running;
        cut.WaitForAssertion(() => cut.Find("[data-testid='export-result']"), timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_router_failure_is_shown_inline_and_the_form_stays()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.ExportFailure = new GrpcAdminException(
            "The conversation export failed: The export destination already exists; an export never overwrites a file.");
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        cut.Find("[data-testid='export-error']").TextContent.Should().Contain("never overwrites a file");
        cut.Find("[data-testid='export-destination']").GetAttribute("value").Should().Be(Destination);
        cut.FindAll("[data-testid='export-result']").Should().BeEmpty();
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeFalse("the operator can fix the path and retry");
    }

    [Fact]
    public async Task An_unreachable_router_gets_the_plain_message()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.ExportFailure = new GrpcAdminException("The conversation export failed: the router is not reachable.", isUnavailable: true);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        cut.Find("[data-testid='export-error']").TextContent.Should().Be("Could not reach the router. Is the proxy running?");
    }

    [Fact]
    public async Task A_dismissed_passkey_prompt_is_shown_and_nothing_is_sent_to_the_router()
    {
        await using var ctx = NewContext(out var conversations, out _, out var ceremony);
        ceremony.Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed or timed out.");
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        cut.Find("[data-testid='export-error']").TextContent.Should().Contain("dismissed or timed out");
        conversations.Destinations.Should().BeEmpty("no export is attempted without an approval");
    }

    [Fact]
    public async Task A_router_that_refuses_the_ceremony_is_shown_inline()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out _);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-destination", Destination);
        passkeys.Failure = new GrpcAdminException("Conversation content is locked until a passkey is enrolled.");

        await ClickExport(cut);

        cut.Find("[data-testid='export-error']").TextContent.Should().Contain("until a passkey is enrolled");
        conversations.Destinations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_date_that_does_not_parse_is_reported_before_any_ceremony()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out _);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-from", "not-a-date");
        await Enter(cut, "export-destination", Destination);

        await ClickExport(cut);

        cut.Find("[data-testid='export-error']").TextContent.Should().Contain("From is not a valid date");
        passkeys.BegunOperations.Should().BeEmpty();
        conversations.Destinations.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_another_returns_to_the_form_with_the_filter_kept_and_the_destination_cleared()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var cut = ctx.Render<ExportDialog>();
        await Enter(cut, "export-harness", "codex");
        await Enter(cut, "export-destination", Destination);
        await ClickExport(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid='export-another']").Click());

        cut.Find("[data-testid='export-harness']").GetAttribute("value").Should().Be("codex");
        cut.Find("[data-testid='export-destination']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid='export-confirm']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Closing_runs_OnClose_from_the_header_button_and_from_Cancel()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var closed = 0;
        var cut = ctx.Render<ExportDialog>(p => p.Add(c => c.OnClose, () => closed++));

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Close export dialog']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='export-cancel']").Click());

        closed.Should().Be(2);
    }

    [Fact]
    public async Task Closing_during_an_export_cancels_it_and_runs_OnClose()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.HoldBeforeResult = new TaskCompletionSource().Task;
        var closed = false;
        var cut = ctx.Render<ExportDialog>(p => p.Add(c => c.OnClose, () => closed = true));
        await Enter(cut, "export-destination", Destination);
        var running = cut.Find("[data-testid='export-confirm']").ClickAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid='export-progress']"), timeout: TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Close export dialog']").Click());

        closed.Should().BeTrue();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        ctx.Services.GetRequiredService<ConversationExportStore>().IsExporting.Should().BeFalse();
    }

    private sealed class FakeRouterSettingsClient(bool captureEnabled) : IRouterSettingsAdminClient
    {
        public Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RouterSettingsInfo(
                AdaptiveRoutingEnabled: false,
                EmbeddingMemoryCapacity: 20_000,
                JudgeEnabled: false,
                JudgeModelName: string.Empty,
                EligibleJudgeModels: [],
                TranscriptCaptureEnabled: captureEnabled));

        public Task<RouterSettingsInfo> UpdateAsync(
            bool adaptiveRoutingEnabled,
            int embeddingMemoryCapacity,
            bool judgeEnabled,
            string judgeModelName,
            bool transcriptCaptureEnabled,
            bool codeJudgeEnabled = false,
            bool iceScoreEnabled = false,
            bool raceEnabled = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClearTranscriptsResult> ClearTranscriptsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
