using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;
using TotallyHot.ArcRouter.Sessions.Import;

namespace TotallyHot.ArcRouter.Tests.Sessions.Import;

/// <summary>
/// Covers <see cref="ImportConversationsCommand"/> (#165 phase 4, PR 4b; ADR-0020): argument parsing, the hand-off
/// order (stage the zip, file a request for the staged copy, wait for the operator, only then import), a distinct exit
/// code for each way it can end, and that the command is only ever a client of the router.
/// </summary>
public sealed class ImportConversationsCommandTests
{
    private const string ApprovalId = "approval-1";
    private const string StagedId = "staged-1";
    private const string Token = "one-op-token";
    private static readonly string Sha = new('a', 64);

    private static ImportConversationsOptions Options(ConversationImportOptions? options = null) =>
        new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "history.zip")), options ?? new ConversationImportOptions());

    private static ConversationImportResult Result(int rejected = 0, int dueForRetention = 0) =>
        new(Guid.NewGuid(), SessionsAdded: 2, SessionsSkipped: 1, TurnsAdded: 7, TurnsAlreadyPresent: 3,
            TurnsConflictKept: 1, TurnsRejected: rejected, TurnsToGrade: 7, BodyBytes: 1234,
            TurnsDueForRetention: dueForRetention);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        FakeRouter router,
        ImportConversationsOptions? options = null,
        TimeSpan? approvalTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        router.Stdout = stdout;
        var exit = await ImportConversationsCommand.RunAsync(
            options ?? Options(),
            router,
            stdout,
            stderr,
            approvalTimeout: approvalTimeout,
            progressInterval: TimeSpan.Zero,
            cancellationToken: cancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakeRouter : IConversationImportRouter
    {
        public List<string> Calls { get; } = [];

        public StringWriter? Stdout { get; set; }

        public string? StdoutWhenWaiting { get; private set; }

        public ExportApprovalResult Approval { get; init; } = new(PendingApprovalOutcome.Approved, Token);

        public RpcException? StageFailure { get; init; }

        public RpcException? ImportFailure { get; init; }

        public Exception? UnexpectedFailure { get; init; }

        public bool HangOnWait { get; init; }

        public string? StagedPathSeen { get; private set; }

        public string? StagedIdSeenByCreate { get; private set; }

        public string? StagedIdSeenByImport { get; private set; }

        public string? TokenSeenByImport { get; private set; }

        public ConversationImportOptions? OptionsSeenByCreate { get; private set; }

        public ConversationImportOptions? OptionsSeenByImport { get; private set; }

        public List<ImportCommandEvent> Events { get; init; } =
        [
            new(new ConversationImportProgress(1, 1), null),
            new(new ConversationImportProgress(2, 2), null),
            new(null, Result()),
        ];

        public Task<ImportStagedCopy> StageAsync(string sourcePath, CancellationToken cancellationToken)
        {
            Calls.Add("stage");
            StagedPathSeen = sourcePath;
            if (StageFailure is not null) throw StageFailure;
            return Task.FromResult(new ImportStagedCopy(StagedId, Sha, 4096));
        }

        public Task<ExportApprovalTicket> CreateApprovalRequestAsync(
            string stagedId, ConversationImportOptions options, CancellationToken cancellationToken)
        {
            Calls.Add("create");
            StagedIdSeenByCreate = stagedId;
            OptionsSeenByCreate = options;
            if (UnexpectedFailure is not null) throw UnexpectedFailure;

            return Task.FromResult(new ExportApprovalTicket(
                ApprovalId, $"https://localhost:47104/?approval={ApprovalId}", new DateTimeOffset(2026, 10, 10, 12, 5, 0, TimeSpan.Zero)));
        }

        public async Task<ExportApprovalResult> WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken)
        {
            Calls.Add("wait");
            approvalId.Should().Be(ApprovalId);
            StdoutWhenWaiting = Stdout?.ToString();
            if (HangOnWait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return Approval;
        }

        public async IAsyncEnumerable<ImportCommandEvent> ImportAsync(
            string stagedId,
            ConversationImportOptions options,
            string authorizationToken,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("import");
            StagedIdSeenByImport = stagedId;
            OptionsSeenByImport = options;
            TokenSeenByImport = authorizationToken;
            await Task.Yield();
            if (ImportFailure is not null) throw ImportFailure;

            foreach (var message in Events) yield return message;
        }
    }

    [Fact]
    public async Task RunAsync_StagesFirst_ThenFilesTheRequest_ThenWaits_ThenImportsWithTheApprovedAuthorization()
    {
        var router = new FakeRouter();
        var options = Options(new ConversationImportOptions(ImportTimestampMode.ImportTime));

        var (exit, stdout, stderr) = await RunAsync(router, options);

        exit.Should().Be(ImportConversationsCommand.ExitSuccess);
        router.Calls.Should().Equal("stage", "create", "wait", "import");
        router.StagedPathSeen.Should().Be(options.SourcePath);
        router.StagedIdSeenByCreate.Should().Be(StagedId);
        router.StagedIdSeenByImport.Should().Be(StagedId);
        router.OptionsSeenByCreate.Should().Be(options.Options);
        router.OptionsSeenByImport.Should().Be(options.Options);
        router.TokenSeenByImport.Should().Be(Token);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("Imported 7 turns into 2 new conversations");
        stdout.Should().Contain("7 imported turns arrived without a score");
    }

    [Fact]
    public async Task RunAsync_PrintsTheDashboardLinkSourceAndDigestBeforeItStartsWaiting()
    {
        var router = new FakeRouter();
        var options = Options();

        await RunAsync(router, options);

        router.StdoutWhenWaiting.Should().Contain($"https://localhost:47104/?approval={ApprovalId}");
        router.StdoutWhenWaiting.Should().Contain(options.SourcePath);
        router.StdoutWhenWaiting.Should().Contain(Sha);
        router.StdoutWhenWaiting.Should().Contain("Waiting for approval");
    }

    [Fact]
    public async Task RunAsync_WhenDenied_ExitsWithTheDenialCode_AndNeverImports()
    {
        var router = new FakeRouter { Approval = new ExportApprovalResult(PendingApprovalOutcome.Denied, null) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitDenied);
        router.Calls.Should().NotContain("import");
        stderr.Should().Contain("denied");
    }

    [Fact]
    public async Task RunAsync_WhenTheRequestExpires_ExitsWithTheTimeoutCode_AndNeverImports()
    {
        var router = new FakeRouter { Approval = new ExportApprovalResult(PendingApprovalOutcome.Expired, null) };

        var (exit, _, _) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitTimedOut);
        router.Calls.Should().NotContain("import");
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterNeverAnswers_GivesUpLocallyWithTheTimeoutCode()
    {
        var router = new FakeRouter { HangOnWait = true };

        var (exit, _, _) = await RunAsync(router, approvalTimeout: TimeSpan.FromMilliseconds(50));

        exit.Should().Be(ImportConversationsCommand.ExitTimedOut);
        router.Calls.Should().NotContain("import");
    }

    [Fact]
    public async Task RunAsync_WhenTheStagedCopyIsGone_IsATimeout()
    {
        var router = new FakeRouter { ImportFailure = new RpcException(new Status(StatusCode.NotFound, "gone")) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitTimedOut);
        stderr.Should().Contain("staged copy");
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task RunAsync_WhenTheRouterCannotBeReached_ExitsWithTheUnreachableCode(StatusCode code)
    {
        var router = new FakeRouter { StageFailure = new RpcException(new Status(code, "down")) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitRouterUnreachable);
        router.Calls.Should().Equal("stage");
        stderr.Should().Contain("Could not reach the router");
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument, "The import source is not a zip file.")]
    [InlineData(StatusCode.FailedPrecondition, "No passkey is enrolled.")]
    [InlineData(StatusCode.ResourceExhausted, "Too many staged imports.")]
    public async Task RunAsync_WhenTheRouterRefusesTheSource_ExitsWithTheRefusedCodeAndTheRoutersReason(
        StatusCode code, string detail)
    {
        var router = new FakeRouter { StageFailure = new RpcException(new Status(code, detail)) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitRefused);
        stderr.Should().Contain(detail);
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterRefusesTheImport_ExitsWithTheRefusedCode()
    {
        var router = new FakeRouter
        {
            ImportFailure = new RpcException(new Status(StatusCode.InvalidArgument, "The archive has schema_version 2.")),
        };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitRefused);
        stderr.Should().Contain("schema_version 2");
    }

    [Fact]
    public async Task RunAsync_WhenSomethingUnexpectedFails_ExitsWithTheGeneralFailureCode()
    {
        var router = new FakeRouter { UnexpectedFailure = new InvalidOperationException("boom") };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitFailed);
        stderr.Should().Contain("boom");
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ExitsWithTheGeneralFailureCode_AndNeverImports()
    {
        var router = new FakeRouter { HangOnWait = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        var (exit, _, stderr) = await RunAsync(router, cancellationToken: cancellation.Token);

        exit.Should().Be(ImportConversationsCommand.ExitFailed);
        router.Calls.Should().NotContain("import");
        stderr.Should().Contain("cancelled");
    }

    [Fact]
    public async Task RunAsync_WarnsOnStderrWhenTurnsWereRejected_AndReportsRetention()
    {
        var router = new FakeRouter { Events = [new ImportCommandEvent(null, Result(rejected: 2, dueForRetention: 40))] };

        var (exit, stdout, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitSuccess);
        stderr.Should().Contain("2 turns failed validation");
        stdout.Should().Contain("delete 40 turns");
    }

    [Fact]
    public async Task RunAsync_WhenTheStreamEndsWithoutAResult_Fails()
    {
        var router = new FakeRouter { Events = [new ImportCommandEvent(new ConversationImportProgress(1, 1), null)] };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ImportConversationsCommand.ExitFailed);
        stderr.Should().Contain("without a result");
    }

    [Fact]
    public void TheExitCodesAreTheSameAsTheExportsAndEachIsDistinct()
    {
        int[] codes =
        [
            ImportConversationsCommand.ExitSuccess, ImportConversationsCommand.ExitFailed, ImportConversationsCommand.ExitUsage,
            ImportConversationsCommand.ExitNotElevated, ImportConversationsCommand.ExitDenied,
            ImportConversationsCommand.ExitTimedOut, ImportConversationsCommand.ExitRouterUnreachable,
            ImportConversationsCommand.ExitRefused,
        ];

        codes.Should().OnlyHaveUniqueItems();
        ImportConversationsCommand.ExitSuccess.Should().Be(0);
        ImportConversationsCommand.ExitDenied.Should().Be(ExportConversationsCommand.ExitDenied);
        ImportConversationsCommand.ExitRefused.Should().Be(ExportConversationsCommand.ExitRefused);
    }

    [Fact]
    public void TryParse_ReadsThePathAndDefaultsToOriginalTimestampsAndKeepingTheLocalCopy()
    {
        var ok = ImportConversationsCommand.TryParse(["--import-conversations", "in.zip"], out var options, out var error);

        ok.Should().BeTrue(error);
        options!.SourcePath.Should().Be(Path.GetFullPath("in.zip"));
        options.Options.Should().Be(new ConversationImportOptions());
        options.Options.EffectiveConflictMode.Should().Be(ImportConflictMode.Skip);
    }

    [Theory]
    [InlineData("skip", ImportConflictMode.Skip, true)]
    [InlineData("overwrite", ImportConflictMode.Overwrite, false)]
    [InlineData("keep-both", ImportConflictMode.KeepBoth, false)]
    public void TryParse_OnConflictOverwriteAndKeepBothTurnKeepLocalCopyOff(string value, ImportConflictMode mode, bool keepLocal)
    {
        var ok = ImportConversationsCommand.TryParse(
            ["--import-conversations", "in.zip", "--on-conflict", value, "--timestamps", "import-time"], out var options, out var error);

        ok.Should().BeTrue(error);
        options!.Options.Should().Be(new ConversationImportOptions(ImportTimestampMode.ImportTime, mode, keepLocal));
        options.Options.EffectiveConflictMode.Should().Be(mode);
    }

    [Fact]
    public void TryParse_AcceptsTheEqualsFormAndAnyOrderAndCase()
    {
        var ok = ImportConversationsCommand.TryParse(
            ["--TIMESTAMPS=Import-Time", "--import-conversations=in.zip", "--on-conflict=KEEP-BOTH"], out var options, out var error);

        ok.Should().BeTrue(error);
        options!.Options.Timestamps.Should().Be(ImportTimestampMode.ImportTime);
        options.Options.EffectiveConflictMode.Should().Be(ImportConflictMode.KeepBoth);
    }

    [Theory]
    [InlineData("--import-conversations")]
    [InlineData("--import-conversations", "in.zip", "--timestamps", "yesterday")]
    [InlineData("--import-conversations", "in.zip", "--on-conflict", "merge")]
    [InlineData("--import-conversations", "in.zip", "--timestamps")]
    [InlineData("--import-conversations", "in.zip", "--frobnicate", "1")]
    [InlineData("--on-conflict", "skip")]
    public void TryParse_RejectsInvalidArgumentsWithAMessage(params string[] args)
    {
        var ok = ImportConversationsCommand.TryParse(args, out var options, out var error);

        ok.Should().BeFalse();
        options.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProgramExtractFlag_FindsTheFlagAndLeavesThePathAndOptionsForTheCommand()
    {
        string[] args = ["--import-conversations", "in.zip", "--on-conflict", "skip"];

        var (present, remaining) = Program.ExtractFlag(args, ImportConversationsCommand.FlagName);

        present.Should().BeTrue();
        remaining.Should().Equal("in.zip", "--on-conflict", "skip");
    }
}
