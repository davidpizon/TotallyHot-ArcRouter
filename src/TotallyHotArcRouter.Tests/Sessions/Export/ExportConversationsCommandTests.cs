using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Sessions.Export;

/// <summary>
/// Covers <see cref="ExportConversationsCommand"/> (#165 phase 3, PR 3c; ADR-0020 Amendment 1): argument parsing, the
/// hand-off order (file a request, wait for the operator, only then export), a distinct exit code for each way it
/// can end, and that the command is only ever a client of the router - it reads no session file and writes no zip.
/// </summary>
public sealed class ExportConversationsCommandTests
{
    private const string ApprovalId = "approval-1";
    private const string Token = "one-op-token";

    private static ExportConversationsOptions Options(string? destination = null) =>
        new(new ConversationExportFilter(Model: "gpt-5"), destination ?? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "export.zip")));

    private static ConversationExportResult Result(int missing = 0, int corrupt = 0, int incomplete = 0) =>
        new(Path: "/exports/out.zip", Conversations: 2, Turns: 7, MissingBodies: missing, CorruptTurns: corrupt,
            IncompleteSessions: incomplete, BodyBytes: 1234);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        FakeRouter router,
        ExportConversationsOptions? options = null,
        TimeSpan? approvalTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        router.Stdout = stdout;
        var exit = await ExportConversationsCommand.RunAsync(
            options ?? Options(),
            router,
            stdout,
            stderr,
            approvalTimeout: approvalTimeout,
            progressInterval: TimeSpan.Zero,
            cancellationToken: cancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakeRouter : IConversationExportRouter
    {
        public List<string> Calls { get; } = [];

        public StringWriter? Stdout { get; set; }

        public string? StdoutWhenWaiting { get; private set; }

        public ExportApprovalResult Approval { get; set; } = new(PendingApprovalOutcome.Approved, Token);

        public RpcException? CreateFailure { get; set; }

        public RpcException? WaitFailure { get; set; }

        public RpcException? ExportFailure { get; set; }

        public Exception? UnexpectedFailure { get; set; }

        public bool HangOnWait { get; set; }

        public string? TokenSeenByExport { get; private set; }

        public ExportConversationsOptions? OptionsSeenByCreate { get; private set; }

        public ExportConversationsOptions? OptionsSeenByExport { get; private set; }

        public List<ExportCommandEvent> Events { get; set; } =
        [
            new(new ConversationExportProgress(1, 100), null),
            new(new ConversationExportProgress(2, 250), null),
            new(null, Result()),
        ];

        public Task<ExportApprovalTicket> CreateApprovalRequestAsync(
            ExportConversationsOptions options, CancellationToken cancellationToken)
        {
            Calls.Add("create");
            OptionsSeenByCreate = options;
            if (CreateFailure is not null) throw CreateFailure;
            if (UnexpectedFailure is not null) throw UnexpectedFailure;

            return Task.FromResult(new ExportApprovalTicket(
                ApprovalId, $"https://localhost:47104/?approval={ApprovalId}", new DateTimeOffset(2026, 10, 10, 12, 5, 0, TimeSpan.Zero)));
        }

        public async Task<ExportApprovalResult> WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken)
        {
            Calls.Add("wait");
            approvalId.Should().Be(ApprovalId);
            StdoutWhenWaiting = Stdout?.ToString();
            if (WaitFailure is not null) throw WaitFailure;
            if (HangOnWait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return Approval;
        }

        public async IAsyncEnumerable<ExportCommandEvent> ExportAsync(
            ExportConversationsOptions options,
            string authorizationToken,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("export");
            OptionsSeenByExport = options;
            TokenSeenByExport = authorizationToken;
            await Task.Yield();
            if (ExportFailure is not null) throw ExportFailure;

            foreach (var message in Events) yield return message;
        }
    }

    [Fact]
    public async Task RunAsync_FilesTheRequest_ThenWaits_ThenExportsWithTheApprovedAuthorization()
    {
        var router = new FakeRouter();
        var options = Options();

        var (exit, stdout, stderr) = await RunAsync(router, options);

        exit.Should().Be(ExportConversationsCommand.ExitSuccess);
        router.Calls.Should().Equal("create", "wait", "export");
        router.OptionsSeenByCreate.Should().BeSameAs(options);
        router.OptionsSeenByExport.Should().BeSameAs(options);
        router.TokenSeenByExport.Should().Be(Token);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("Exported 7 turns from 2 conversations to /exports/out.zip");
    }

    [Fact]
    public async Task RunAsync_PrintsTheDashboardLinkAndDestinationBeforeItStartsWaiting()
    {
        var router = new FakeRouter();
        var options = Options();

        await RunAsync(router, options);

        router.StdoutWhenWaiting.Should().Contain($"https://localhost:47104/?approval={ApprovalId}");
        router.StdoutWhenWaiting.Should().Contain(options.DestinationPath);
        router.StdoutWhenWaiting.Should().Contain("Approve this export in your browser");
    }

    [Fact]
    public async Task RunAsync_PrintsProgressAsTheExportRuns()
    {
        var (_, stdout, _) = await RunAsync(new FakeRouter());

        stdout.Should().Contain("1 turns, 100 bytes").And.Contain("2 turns, 250 bytes");
    }

    [Fact]
    public async Task RunAsync_WhenDenied_ExitsWithTheDenialCode_AndNeverExports()
    {
        var router = new FakeRouter { Approval = new(PendingApprovalOutcome.Denied, null) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitDenied);
        router.Calls.Should().Equal("create", "wait");
        stderr.Should().Contain("denied");
    }

    [Fact]
    public async Task RunAsync_WhenTheRequestExpires_ExitsWithTheTimeoutCode_AndNeverExports()
    {
        var router = new FakeRouter { Approval = new(PendingApprovalOutcome.Expired, null) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitTimedOut);
        router.Calls.Should().Equal("create", "wait");
        stderr.Should().Contain("expired");
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterNeverAnswers_GivesUpLocallyWithTheTimeoutCode()
    {
        var router = new FakeRouter { HangOnWait = true };

        var (exit, _, _) = await RunAsync(router, approvalTimeout: TimeSpan.FromMilliseconds(50));

        exit.Should().Be(ExportConversationsCommand.ExitTimedOut);
        router.Calls.Should().Equal("create", "wait");
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterNoLongerHoldsTheRequest_IsATimeout()
    {
        var router = new FakeRouter { WaitFailure = new RpcException(new Status(StatusCode.NotFound, "gone")) };

        var (exit, _, _) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitTimedOut);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task RunAsync_WhenTheRouterCannotBeReached_ExitsWithTheUnreachableCode(StatusCode code)
    {
        var router = new FakeRouter { CreateFailure = new RpcException(new Status(code, "no connection")) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitRouterUnreachable);
        router.Calls.Should().Equal("create");
        stderr.Should().Contain("Could not reach the router");
    }

    [Theory]
    [InlineData(StatusCode.FailedPrecondition, "no passkey enrolled")]
    [InlineData(StatusCode.Unauthenticated, "bad token")]
    [InlineData(StatusCode.InvalidArgument, "destination exists")]
    [InlineData(StatusCode.ResourceExhausted, "too many requests")]
    public async Task RunAsync_WhenTheRouterRefusesTheRequest_ExitsWithTheRefusedCodeAndTheRoutersReason(
        StatusCode code, string detail)
    {
        var router = new FakeRouter { CreateFailure = new RpcException(new Status(code, detail)) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitRefused);
        stderr.Should().Contain(detail);
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterRefusesTheExport_ExitsWithTheRefusedCode()
    {
        var router = new FakeRouter
        {
            ExportFailure = new RpcException(new Status(StatusCode.InvalidArgument, "The export destination already exists.")),
        };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitRefused);
        router.Calls.Should().Equal("create", "wait", "export");
        stderr.Should().Contain("The export destination already exists.");
    }

    [Fact]
    public async Task RunAsync_WhenTheRouterFailsInternally_ExitsWithTheGeneralFailureCode()
    {
        var router = new FakeRouter { CreateFailure = new RpcException(new Status(StatusCode.Internal, "boom")) };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitFailed);
        stderr.Should().Contain("boom");
    }

    [Fact]
    public async Task RunAsync_WhenSomethingUnexpectedFails_ExitsWithTheGeneralFailureCode()
    {
        var router = new FakeRouter { UnexpectedFailure = new FormatException("garbled") };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitFailed);
        stderr.Should().Contain("garbled");
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ExitsWithTheGeneralFailureCode_AndNeverExports()
    {
        var router = new FakeRouter { HangOnWait = true };
        using var cancellation = new CancellationTokenSource();
        var running = RunAsync(router, cancellationToken: cancellation.Token);

        await cancellation.CancelAsync();
        var (exit, _, stderr) = await running;

        exit.Should().Be(ExportConversationsCommand.ExitFailed);
        router.Calls.Should().NotContain("export");
        stderr.Should().Contain("cancelled");
    }

    [Fact]
    public async Task RunAsync_WarnsOnStderrWhenTheZipIsMissingBodiesOrTurns()
    {
        var router = new FakeRouter { Events = [new(null, Result(missing: 2, corrupt: 1, incomplete: 3))] };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitSuccess);
        stderr.Should().Contain("2 missing bodies").And.Contain("1 corrupt turns").And.Contain("3 sessions");
    }

    [Fact]
    public async Task RunAsync_WhenTheStreamEndsWithoutAResult_Fails()
    {
        var router = new FakeRouter { Events = [new(new ConversationExportProgress(1, 1), null)] };

        var (exit, _, stderr) = await RunAsync(router);

        exit.Should().Be(ExportConversationsCommand.ExitFailed);
        stderr.Should().Contain("without a result");
    }

    [Fact]
    public void EveryExitCodeIsDistinct()
    {
        int[] codes =
        [
            ExportConversationsCommand.ExitSuccess,
            ExportConversationsCommand.ExitFailed,
            ExportConversationsCommand.ExitUsage,
            ExportConversationsCommand.ExitNotElevated,
            ExportConversationsCommand.ExitDenied,
            ExportConversationsCommand.ExitTimedOut,
            ExportConversationsCommand.ExitRouterUnreachable,
            ExportConversationsCommand.ExitRefused,
        ];

        codes.Should().OnlyHaveUniqueItems();
        ExportConversationsCommand.ExitSuccess.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_NeverTouchesTheSessionFolderOrWritesTheZip()
    {
        // A stand-in for the router's data directory: a session folder whose files are held open exclusively, so any
        // read of them by this process would fail, and a destination the command must leave untouched.
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"export-cli-{Guid.NewGuid():N}");
        var sessionFolder = Path.Combine(dataDirectory, "sessions");
        Directory.CreateDirectory(sessionFolder);
        var sentinel = Path.Combine(sessionFolder, "session-1.session");
        await File.WriteAllBytesAsync(sentinel, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var destination = Path.Combine(dataDirectory, "outside", "export.zip");
        try
        {
            var before = Snapshot(dataDirectory);
            await using (new FileStream(sentinel, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var router = new FakeRouter();

                var (exit, _, _) = await RunAsync(router, Options(destination));

                exit.Should().Be(ExportConversationsCommand.ExitSuccess);
                router.Calls.Should().Equal("create", "wait", "export");
            }

            Snapshot(dataDirectory).Should().Equal(before);
            File.Exists(destination).Should().BeFalse("only the router writes the zip");
            File.Exists(destination + ".partial").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        static string[] Snapshot(string directory) =>
        [
            .. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(path => $"{path}|{(File.Exists(path) ? new FileInfo(path).Length : -1)}|{Directory.GetLastWriteTimeUtc(path):O}")
                .Order(StringComparer.Ordinal),
        ];
    }

    [Fact]
    public void TryParse_ReadsTheDestinationAndEveryFilterOption()
    {
        string[] args =
        [
            "--export-conversations", "out.zip", "--from", "2026-10-01", "--to", "2026-10-02T08:30:00Z",
            "--session", "s-1", "--harness", "claude-code", "--provider", "anthropic", "--model", "opus",
        ];

        var ok = ExportConversationsCommand.TryParse(args, out var options, out var error);

        ok.Should().BeTrue(error);
        options!.DestinationPath.Should().Be(Path.GetFullPath("out.zip"));
        options.Filter.Should().Be(new ConversationExportFilter(
            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero),
            SessionId: "s-1",
            Harness: "claude-code",
            Provider: "anthropic",
            Model: "opus"));
    }

    [Fact]
    public void TryParse_AcceptsTheEqualsFormAndAnyOrderAndCase()
    {
        string[] args = ["--MODEL=opus", "--export-conversations", "out.zip", "--Provider=anthropic"];

        var ok = ExportConversationsCommand.TryParse(args, out var options, out var error);

        ok.Should().BeTrue(error);
        options!.Filter.Model.Should().Be("opus");
        options.Filter.Provider.Should().Be("anthropic");
        options.Filter.From.Should().BeNull();
    }

    [Fact]
    public void TryParse_ResolvesARelativeDestinationAgainstTheWorkingDirectoryWithoutTouchingDisk()
    {
        var ok = ExportConversationsCommand.TryParse(["--export-conversations", "does/not/exist/out.zip"], out var options, out _);

        ok.Should().BeTrue();
        Path.IsPathFullyQualified(options!.DestinationPath).Should().BeTrue();
        File.Exists(options.DestinationPath).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(options.DestinationPath)).Should().BeFalse();
    }

    [Theory]
    [InlineData("--export-conversations")]
    [InlineData("--export-conversations", "--from", "2026-10-01")]
    [InlineData("--export-conversations", "out.zip", "--from")]
    [InlineData("--export-conversations", "out.zip", "--from", "yesterday-ish")]
    [InlineData("--export-conversations", "out.zip", "--to", "not a date")]
    [InlineData("--export-conversations", "out.zip", "--model", "--provider", "x")]
    [InlineData("--export-conversations", "out.zip", "--from", "2026-10-03", "--to", "2026-10-01")]
    [InlineData("--export-conversations", "out.zip", "--bogus", "x")]
    [InlineData("--export-conversations", "out.zip", "stray")]
    [InlineData("--export-conversations", "out.zip", "--session=")]
    public void TryParse_RejectsInvalidArgumentsWithAMessage(params string[] args)
    {
        var ok = ExportConversationsCommand.TryParse(args, out var options, out var error);

        ok.Should().BeFalse();
        options.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProgramExtractFlag_FindsTheFlagAndLeavesTheDestinationAndOptionsForTheCommand()
    {
        string[] args = ["--export-conversations", "out.zip", "--model", "opus"];

        var (present, remaining) = Program.ExtractFlag(args, ExportConversationsCommand.FlagName);

        present.Should().BeTrue();
        remaining.Should().Equal("out.zip", "--model", "opus");
    }
}
