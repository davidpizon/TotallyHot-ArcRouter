using System.Globalization;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>What the <c>--export-conversations</c> command was asked to export, parsed from its arguments.</summary>
/// <param name="Filter">Which turns the export includes.</param>
/// <param name="DestinationPath">The zip's absolute path on this machine, resolved against the working directory.</param>
internal sealed record ExportConversationsOptions(ConversationExportFilter Filter, string DestinationPath);

/// <summary>An approval request the router accepted: where to approve it and when it lapses.</summary>
/// <param name="ApprovalId">The router's id for the request.</param>
/// <param name="DashboardUrl">The dashboard address that opens the approval view on this request.</param>
/// <param name="ExpiresAtUtc">When the request lapses if nobody decides it.</param>
internal sealed record ExportApprovalTicket(string ApprovalId, string DashboardUrl, DateTimeOffset ExpiresAtUtc);

/// <summary>How a request ended, and the authorization the router minted when it was approved.</summary>
/// <param name="Outcome">Approved, denied or lapsed.</param>
/// <param name="AuthorizationToken">The one-operation authorization, set only when <paramref name="Outcome"/> is <see cref="PendingApprovalOutcome.Approved"/>.</param>
internal sealed record ExportApprovalResult(PendingApprovalOutcome Outcome, string? AuthorizationToken);

/// <summary>
/// One message on the export stream: a progress report, or the finished result as the last message. Exactly one
/// of the two is set.
/// </summary>
/// <param name="Progress">A progress report, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The finished export, or <see langword="null"/> for a progress report.</param>
internal sealed record ExportCommandEvent(ConversationExportProgress? Progress, ConversationExportResult? Result);

/// <summary>
/// The three router calls <see cref="ExportConversationsCommand"/> makes, in the order it makes them. An
/// interface so a test can stand in a fake router that records that order; the production implementation is
/// <see cref="GrpcConversationExportRouter"/>. Every method reports a failed call by throwing
/// <see cref="RpcException"/>, which the command maps to an exit code.
/// </summary>
internal interface IConversationExportRouter
{
    /// <summary>Files a request for the operator to approve this export in the dashboard.</summary>
    /// <param name="options">The export to approve.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where to approve the request and when it lapses.</returns>
    Task<ExportApprovalTicket> CreateApprovalRequestAsync(
        ExportConversationsOptions options, CancellationToken cancellationToken);

    /// <summary>Waits until the operator decides the request or it lapses.</summary>
    /// <param name="approvalId">The id <see cref="CreateApprovalRequestAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How the request ended, with the authorization when it was approved.</returns>
    Task<ExportApprovalResult> WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken);

    /// <summary>Runs the export the authorization was granted for and streams its progress.</summary>
    /// <param name="options">The same export <see cref="CreateApprovalRequestAsync"/> was given; the router refuses any other.</param>
    /// <param name="authorizationToken">The authorization <see cref="WaitForApprovalAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the export, which then leaves no file behind.</param>
    /// <returns>Progress reports, then the result.</returns>
    IAsyncEnumerable<ExportCommandEvent> ExportAsync(
        ExportConversationsOptions options, string authorizationToken, CancellationToken cancellationToken);
}

/// <summary>
/// The router's <c>--export-conversations &lt;path&gt;</c> command (#165 phase 3, PR 3c; ADR-0020, Amendment 1):
/// writes the captured conversations to a zip on this machine without reading any session file itself. It is a gRPC
/// client of the <em>running</em> router, so there is one export code path and the passkey gate cannot be bypassed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flow.</b> The command files an approval request with the router and prints the dashboard address, the
/// operator approves it there with a passkey (the browser is the only place a ceremony can run on every
/// platform), the command collects the authorization, and only then calls the export RPC with it. The zip does
/// not exist until the operator has approved.
/// </para>
/// <para>
/// <b>Options.</b> <c>--from</c> and <c>--to</c> (a UTC date or timestamp, inclusive), <c>--session</c> (an archive
/// or client session id), <c>--harness</c>, <c>--provider</c> and <c>--model</c>. Each takes its value as the next
/// argument or as <c>--option=value</c>. A relative destination is resolved against the working directory, because
/// the command runs on the router machine.
/// </para>
/// <para>
/// <b>Exit codes</b> are distinct for each way the command can end, so a script can tell a refusal by the
/// operator from a router that is not running; see the <c>Exit*</c> constants. They are documented in
/// <c>README.md</c> and <c>docs/HANDBOOK.md</c>.
/// </para>
/// </remarks>
internal static class ExportConversationsCommand
{
    /// <summary>The command-line flag that selects this command; its value is the destination path.</summary>
    internal const string FlagName = "--export-conversations";

    /// <summary>Exit code: the zip was written.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Exit code: something unexpected failed, or the command was interrupted.</summary>
    internal const int ExitFailed = 1;

    /// <summary>Exit code: the arguments were invalid; nothing was sent to the router.</summary>
    internal const int ExitUsage = 2;

    /// <summary>Exit code: this account cannot open the router service's data directory, so it has no admin token to present.</summary>
    internal const int ExitNotElevated = 3;

    /// <summary>Exit code: the operator denied the export in the dashboard; nothing was written.</summary>
    internal const int ExitDenied = 4;

    /// <summary>Exit code: nobody approved the export before the request lapsed; nothing was written.</summary>
    internal const int ExitTimedOut = 5;

    /// <summary>Exit code: the router could not be reached; is it running?</summary>
    internal const int ExitRouterUnreachable = 6;

    /// <summary>Exit code: the router refused the request or the export (no passkey enrolled, a bad destination, another export running, a full disk); nothing was written.</summary>
    internal const int ExitRefused = 7;

    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Parses the command's arguments. A relative destination is resolved against the working directory (a
    /// path computation only; nothing on disk is touched).
    /// </summary>
    /// <param name="args">The process arguments, including <see cref="FlagName"/> and its value.</param>
    /// <param name="options">The parsed export when the arguments are valid.</param>
    /// <param name="error">What is wrong with the arguments, when they are not valid.</param>
    /// <returns><see langword="true"/> when the arguments are valid.</returns>
    internal static bool TryParse(
        IReadOnlyList<string> args, out ExportConversationsOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        string? destination = null;
        DateTimeOffset? from = null, to = null;
        string? session = null, harness = null, provider = null, model = null;

        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = SplitOption(args[i]);

            if (!IsKnownOption(name))
            {
                error = $"Unrecognized argument '{args[i]}'.";
                return false;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"{name} requires a value.";
                    return false;
                }

                value = args[++i];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{name} requires a non-empty value.";
                return false;
            }

            switch (name.ToLowerInvariant())
            {
                case FlagName: destination = value; break;
                case "--session": session = value; break;
                case "--harness": harness = value; break;
                case "--provider": provider = value; break;
                case "--model": model = value; break;
                case "--from":
                    if (!TryParseUtc(value, out from))
                    {
                        error = $"--from '{value}' is not a valid date or timestamp (for example 2026-10-01 or 2026-10-01T08:30:00Z).";
                        return false;
                    }

                    break;
                case "--to":
                    if (!TryParseUtc(value, out to))
                    {
                        error = $"--to '{value}' is not a valid date or timestamp (for example 2026-10-01 or 2026-10-01T08:30:00Z).";
                        return false;
                    }

                    break;
            }
        }

        if (destination is null)
        {
            error = $"{FlagName} requires the path of the zip to create.";
            return false;
        }

        if (from is { } start && to is { } end && start > end)
        {
            error = "--from is later than --to.";
            return false;
        }

        options = new ExportConversationsOptions(
            new ConversationExportFilter(from, to, session, harness, provider, model),
            Path.GetFullPath(destination));
        return true;
    }

    /// <summary>
    /// Runs the hand-off against <paramref name="router"/>: files the approval request, prints where to approve it,
    /// waits for the decision, and exports with the authorization it is given. Never throws for a failed call; each
    /// outcome is an exit code and a message on <paramref name="stderr"/>.
    /// </summary>
    /// <param name="options">The export to run.</param>
    /// <param name="router">The running router.</param>
    /// <param name="stdout">Receives the approval address and progress.</param>
    /// <param name="stderr">Receives refusals and errors.</param>
    /// <param name="logger">Receives an unexpected failure's details; optional.</param>
    /// <param name="approvalTimeout">
    /// How long to wait for the operator before giving up locally; defaults to the router's request lifetime plus a
    /// margin, so the router's own expiry normally reports first.
    /// </param>
    /// <param name="progressInterval">The least time between progress lines; defaults to two seconds.</param>
    /// <param name="timeProvider">The clock behind <paramref name="progressInterval"/>; defaults to the system clock.</param>
    /// <param name="cancellationToken">Cancels the command (for example on Ctrl+C).</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        ExportConversationsOptions options,
        IConversationExportRouter router,
        TextWriter stdout,
        TextWriter stderr,
        ILogger? logger = null,
        TimeSpan? approvalTimeout = null,
        TimeSpan? progressInterval = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var time = timeProvider ?? TimeProvider.System;
        try
        {
            var ticket = await router.CreateApprovalRequestAsync(options, cancellationToken).ConfigureAwait(false);
            await stdout.WriteLineAsync("Approve this export in your browser:").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  {ticket.DashboardUrl}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"Destination: {options.DestinationPath}").ConfigureAwait(false);
            await stdout.WriteLineAsync(
                $"Waiting for approval; the request expires at {ticket.ExpiresAtUtc:u}.").ConfigureAwait(false);

            var approval = await WaitAsync(
                router, ticket.ApprovalId, approvalTimeout ?? PendingApprovalTable.RequestTtl + TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            switch (approval.Outcome)
            {
                case PendingApprovalOutcome.Denied:
                    await stderr.WriteLineAsync("The export was denied in the dashboard. Nothing was written.")
                        .ConfigureAwait(false);
                    return ExitDenied;
                case PendingApprovalOutcome.Expired:
                    return await TimedOutAsync(stderr).ConfigureAwait(false);
            }

            return await ExportAsync(
                options, router, approval.AuthorizationToken ?? string.Empty, stdout, stderr,
                progressInterval ?? DefaultProgressInterval, time, cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalTimeoutException)
        {
            return await TimedOutAsync(stderr).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            return await ReportRpcFailureAsync(ex, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("The export was cancelled. Nothing was written.").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Top level of a one-shot command: anything else (a malformed response, an I/O error on the console)
            // becomes an exit code instead of a crash dump.
            logger?.LogError(ex, "The conversation export command failed unexpectedly.");
            await stderr.WriteLineAsync($"The export failed unexpectedly: {ex.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    /// <summary>
    /// Waits for the operator's decision, giving up locally after <paramref name="timeout"/> so a router that
    /// never answers cannot hang the command.
    /// </summary>
    /// <param name="router">The running router.</param>
    /// <param name="approvalId">The request to wait for.</param>
    /// <param name="timeout">The longest to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How the request ended.</returns>
    /// <exception cref="ApprovalTimeoutException">When <paramref name="timeout"/> passes with no decision.</exception>
    private static async Task<ExportApprovalResult> WaitAsync(
        IConversationExportRouter router, string approvalId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCancellation.CancelAfter(timeout);
        try
        {
            return await router.WaitForApprovalAsync(approvalId, waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApprovalTimeoutException();
        }
    }

    /// <summary>Runs the approved export and reports its progress and result.</summary>
    /// <param name="options">The export to run.</param>
    /// <param name="router">The running router.</param>
    /// <param name="authorizationToken">The approved authorization.</param>
    /// <param name="stdout">Receives progress and the result.</param>
    /// <param name="stderr">Receives warnings about an incomplete export.</param>
    /// <param name="progressInterval">The least time between progress lines.</param>
    /// <param name="time">The clock behind <paramref name="progressInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> ExportAsync(
        ExportConversationsOptions options,
        IConversationExportRouter router,
        string authorizationToken,
        TextWriter stdout,
        TextWriter stderr,
        TimeSpan progressInterval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await stdout.WriteLineAsync("Approved. Exporting...").ConfigureAwait(false);

        var lastProgress = time.GetTimestamp();
        ConversationExportResult? result = null;
        await foreach (var message in router.ExportAsync(options, authorizationToken, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (message.Result is { } finished)
            {
                result = finished;
                continue;
            }

            if (message.Progress is not { } progress || time.GetElapsedTime(lastProgress) < progressInterval) continue;

            lastProgress = time.GetTimestamp();
            await stdout.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"  {progress.TurnsWritten} turns, {progress.BytesWritten} bytes of bodies written so far")).ConfigureAwait(false);
        }

        if (result is null)
        {
            await stderr.WriteLineAsync("The router ended the export without a result; check that the zip is complete.")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Exported {result.Turns} turns from {result.Conversations} conversations to {result.Path}")).ConfigureAwait(false);
        if (result.MissingBodies > 0 || result.CorruptTurns > 0 || result.IncompleteSessions > 0)
        {
            await stderr.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Warning: {result.MissingBodies} missing bodies, {result.CorruptTurns} corrupt turns left out, {result.IncompleteSessions} sessions deleted mid-export.")).ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    /// <summary>Reports a request that lapsed and returns its exit code.</summary>
    /// <param name="stderr">Receives the message.</param>
    /// <returns><see cref="ExitTimedOut"/>.</returns>
    private static async Task<int> TimedOutAsync(TextWriter stderr)
    {
        await stderr.WriteLineAsync("Nobody approved the export before the request expired. Nothing was written.")
            .ConfigureAwait(false);
        return ExitTimedOut;
    }

    /// <summary>
    /// Maps a failed router call to an exit code and message: an unreachable router, a request that already
    /// lapsed, or a refusal.
    /// </summary>
    /// <param name="exception">The failed call.</param>
    /// <param name="stderr">Receives the message.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> ReportRpcFailureAsync(RpcException exception, TextWriter stderr)
    {
        switch (exception.StatusCode)
        {
            case StatusCode.Unavailable:
            case StatusCode.DeadlineExceeded:
                await stderr.WriteLineAsync(
                    "Could not reach the router. Is it running, and is this the machine it runs on?").ConfigureAwait(false);
                return ExitRouterUnreachable;

            case StatusCode.NotFound:
                // The router no longer holds the request: it lapsed before the command asked for its outcome.
                return await TimedOutAsync(stderr).ConfigureAwait(false);

            case StatusCode.Unauthenticated or StatusCode.PermissionDenied or StatusCode.FailedPrecondition
                or StatusCode.InvalidArgument or StatusCode.ResourceExhausted or StatusCode.AlreadyExists:
                await stderr.WriteLineAsync($"The router refused the export: {exception.Status.Detail}")
                    .ConfigureAwait(false);
                return ExitRefused;

            default:
                await stderr.WriteLineAsync(
                    $"The export failed ({exception.StatusCode}): {exception.Status.Detail}").ConfigureAwait(false);
                return ExitFailed;
        }
    }

    /// <summary>Splits <c>--option=value</c> into its name and value; an argument without <c>=</c> has no inline value.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The option name and its inline value, or <see langword="null"/> when there is none.</returns>
    private static (string Name, string? Value) SplitOption(string argument)
    {
        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        return argument.StartsWith("--", StringComparison.Ordinal) && separator > 0
            ? (argument[..separator], argument[(separator + 1)..])
            : (argument, null);
    }

    /// <summary>Reports whether <paramref name="name"/> is an option this command takes, ignoring case.</summary>
    /// <param name="name">The option name, including the leading dashes.</param>
    /// <returns><see langword="true"/> for a known option.</returns>
    private static bool IsKnownOption(string name) =>
        name.ToLowerInvariant() is FlagName or "--from" or "--to" or "--session" or "--harness" or "--provider" or "--model";

    /// <summary>Parses a date or timestamp as a UTC instant; a date without a time is midnight UTC.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The instant when the text parses.</param>
    /// <returns><see langword="true"/> when the text is a valid date or timestamp.</returns>
    private static bool TryParseUtc(string text, out DateTimeOffset? value)
    {
        var parsed = DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var instant);
        value = parsed ? instant : null;
        return parsed;
    }

    /// <summary>Signals that the local wait for a decision ran out, distinct from the caller cancelling.</summary>
    private sealed class ApprovalTimeoutException : Exception;
}
