using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the Import half of the Export / Import dialog (<see cref="ImportPanel"/> inside
/// <see cref="ExportDialog"/>, #165 phase 4, PR 4b): that it needs a source, stages the zip before it asks for a
/// passkey, binds the approval to the digest the router returned, sends that approval with the staged id and the
/// chosen options, shows the result, shows failures inline without asking for a passkey, and lists and deletes past
/// imports.
/// </summary>
public sealed class ImportPanelTests
{
    private const string Source = @"C:\imports\history.zip";

    private static BunitContext NewContext(
        out FakeConversationAdminClient conversations, out FakePasskeyAdminClient passkeys, out FakeWebAuthnCeremony ceremony)
    {
        var ctx = new BunitContext();
        conversations = new FakeConversationAdminClient();
        passkeys = new FakePasskeyAdminClient();
        ceremony = new FakeWebAuthnCeremony();
        ctx.Services.AddSingleton(new ConversationExportStore(conversations));
        ctx.Services.AddSingleton(new PasskeyAdminStore(passkeys, ceremony, new ContentGrantStore()));
        ctx.Services.AddSingleton(new RouterSettingsAdminStore(new FakeSettings()));
        return ctx;
    }

    private static async Task<IRenderedComponent<ExportDialog>> OpenImportAsync(BunitContext ctx)
    {
        var cut = ctx.Render<ExportDialog>();
        await cut.InvokeAsync(() => cut.Find("[data-testid='mode-import']").Click());
        return cut;
    }

    private static async Task Enter(IRenderedComponent<ExportDialog> cut, string testId, string value) =>
        await cut.Find($"[data-testid='{testId}']").InputAsync(value);

    private static Task ClickImport(IRenderedComponent<ExportDialog> cut) =>
        cut.InvokeAsync(() => cut.Find("[data-testid='import-confirm']").Click());

    [Fact]
    public async Task The_dialog_is_titled_Export_slash_Import_and_switches_between_the_two_halves()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var cut = ctx.Render<ExportDialog>();

        cut.Find("div.overlay-panel span.uppercase").TextContent.Should().Be("Export / Import");
        cut.FindAll("[data-testid='export-confirm']").Should().ContainSingle();
        cut.FindAll("[data-testid='import-panel']").Should().BeEmpty();

        await cut.InvokeAsync(() => cut.Find("[data-testid='mode-import']").Click());

        cut.FindAll("[data-testid='import-panel']").Should().ContainSingle();
        cut.FindAll("[data-testid='export-confirm']").Should().BeEmpty();

        await cut.InvokeAsync(() => cut.Find("[data-testid='mode-export']").Click());
        cut.FindAll("[data-testid='export-confirm']").Should().ContainSingle();
    }

    [Fact]
    public async Task Import_is_disabled_until_there_is_a_source()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var cut = await OpenImportAsync(ctx);

        cut.Find("[data-testid='import-confirm']").HasAttribute("disabled").Should().BeTrue();

        await Enter(cut, "import-source", "   ");
        cut.Find("[data-testid='import-confirm']").HasAttribute("disabled").Should().BeTrue("a blank path is no source");

        await Enter(cut, "import-source", Source);
        cut.Find("[data-testid='import-confirm']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Import_is_disabled_and_the_enrollment_command_is_named_when_no_passkey_is_enrolled()
    {
        await using var ctx = NewContext(out _, out var passkeys, out _);
        passkeys.Passkeys = [];
        passkeys.Status = passkeys.Status with { Enrolled = false };
        var cut = await OpenImportAsync(ctx);

        await Enter(cut, "import-source", Source);

        cut.Find("[data-testid='import-enrollment-hint']").TextContent.Should().Contain("--mint-passkey-enrollment-code");
        cut.Find("[data-testid='import-confirm']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Import_stages_first_then_runs_one_ceremony_bound_to_the_staged_digest_then_imports_that_copy()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out var ceremony);
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", "  " + Source + "  ");

        await ClickImport(cut);

        conversations.StagedSources.Should().Equal(Source);
        passkeys.BegunOperations.Should().Equal(PasskeyOperations.Import);
        passkeys.BegunParameters.Should().Equal(conversations.Staged.Sha256);
        passkeys.FinishedParameters.Should().Equal(conversations.Staged.Sha256);
        ceremony.GetOptions.Should().ContainSingle();
        conversations.ImportedStagedIds.Should().Equal(conversations.Staged.StagedId);
        conversations.ImportAuthorizations.Should().Equal($"authz-{PasskeyOperations.Import}");
        conversations.ImportOptions.Should().ContainSingle().Which.Should().Be(new ConversationImportOptionsInfo());
        System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid='import-result-counts']").TextContent, @"\s+", " ")
            .Should().Contain("5 turns added in 2 new conversations");
        cut.Find("[data-testid='import-result-grading']").TextContent.Should().Contain("5 imported turns have no score");
    }

    [Fact]
    public async Task Import_sends_the_chosen_options()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);
        await cut.InvokeAsync(() => cut.Find("[data-testid='import-timestamps']").Change("import-time"));
        await cut.InvokeAsync(() => cut.Find("[data-testid='import-keep-local']").Change(false));
        await cut.InvokeAsync(() => cut.Find("[data-testid='import-conflict']").Change(nameof(ConversationImportConflictChoice.KeepBoth)));

        await ClickImport(cut);

        conversations.ImportOptions.Should().ContainSingle().Which.Should().Be(
            new ConversationImportOptionsInfo(ImportTime: true, ConversationImportConflictChoice.KeepBoth, KeepLocalCopy: false));
    }

    [Fact]
    public async Task A_refused_source_is_shown_inline_and_no_passkey_is_asked_for()
    {
        await using var ctx = NewContext(out var conversations, out var passkeys, out _);
        conversations.StageFailure = new GrpcAdminException("The import source is not a zip file.");
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);

        await ClickImport(cut);

        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("not a zip file");
        passkeys.BegunOperations.Should().BeEmpty();
        conversations.ImportedStagedIds.Should().BeEmpty();
        cut.Find("[data-testid='import-confirm']").HasAttribute("disabled").Should().BeFalse("the operator can fix the path and retry");
    }

    [Fact]
    public async Task A_dismissed_passkey_prompt_is_shown_inline_and_nothing_is_imported()
    {
        await using var ctx = NewContext(out var conversations, out _, out var ceremony);
        ceremony.Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed.");
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);

        await ClickImport(cut);

        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("dismissed");
        conversations.ImportedStagedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_router_refusal_of_the_import_is_shown_inline()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.ImportFailure = new GrpcAdminException("The archive has schema_version 2.");
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);

        await ClickImport(cut);

        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("schema_version 2");
    }

    [Fact]
    public async Task The_result_warns_about_rejected_turns_and_turns_due_for_retention()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.ImportResult = conversations.ImportResult with { TurnsRejected = 2, TurnsDueForRetention = 1_500 };
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);

        await ClickImport(cut);

        System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid='import-result-rejected']").TextContent, @"\s+", " ")
            .Should().Contain("2 turns failed validation");
        cut.Find("[data-testid='import-result-retention']").TextContent.Should().Contain("1,500 turns");
    }

    [Fact]
    public async Task Import_another_returns_to_the_form_with_the_source_cleared()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var cut = await OpenImportAsync(ctx);
        await Enter(cut, "import-source", Source);
        await ClickImport(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid='import-another']").Click());

        cut.FindAll("[data-testid='import-result']").Should().BeEmpty();
        cut.Find("[data-testid='import-source']").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Past_imports_are_listed_and_a_delete_needs_confirming()
    {
        await using var ctx = NewContext(out var conversations, out _, out _);
        conversations.PastImports =
        [
            new ConversationImportRowInfo("i1", new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero), new string('e', 64), 2, 9, 0, 0),
        ];
        var cut = await OpenImportAsync(ctx);

        System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid='import-row-summary']").TextContent, @"\s+", " ")
            .Should().Contain("2026-10-10 09:30 UTC").And.Contain("9 turns in 2 new conversations");
        cut.FindAll("[data-testid='import-row-delete-confirm']").Should().BeEmpty();

        await cut.InvokeAsync(() => cut.Find("[data-testid='import-row-delete']").Click());
        conversations.DeletedImports.Should().BeEmpty("the first click only asks to confirm");

        await cut.InvokeAsync(() => cut.Find("[data-testid='import-row-delete-confirm']").Click());

        conversations.DeletedImports.Should().Equal("i1");
        cut.FindAll("[data-testid='import-row-i1']").Should().BeEmpty();
        cut.Find("[data-testid='import-history-empty']").Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_runs_OnClose()
    {
        await using var ctx = NewContext(out _, out _, out _);
        var closed = false;
        var cut = ctx.Render<ExportDialog>(p => p.Add(c => c.OnClose, () => closed = true));
        await cut.InvokeAsync(() => cut.Find("[data-testid='mode-import']").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid='import-cancel']").Click());

        closed.Should().BeTrue();
    }

    private sealed class FakeSettings : IRouterSettingsAdminClient
    {
        public Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RouterSettingsInfo(
                AdaptiveRoutingEnabled: false,
                EmbeddingMemoryCapacity: 20_000,
                JudgeEnabled: false,
                JudgeModelName: string.Empty,
                EligibleJudgeModels: [],
                TranscriptCaptureEnabled: true));

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
