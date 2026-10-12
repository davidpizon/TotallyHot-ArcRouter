using System.Globalization;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>What the <c>--import-conversations</c> command was asked to import, parsed from its arguments.</summary>
/// <param name="SourcePath">The zip's absolute path on this machine, resolved against the working directory.</param>
/// <param name="Options">How the import will run.</param>
internal sealed record ImportConversationsOptions(string SourcePath, ConversationImportOptions Options);

/// <summary>A copy of the zip the router staged, and the digest the operator's approval is bound to.</summary>
/// <param name="StagedId">The router's id for the copy.</param>
/// <param name="Sha256Hex">The copy's SHA-256 as 64 lowercase hex digits.</param>
/// <param name="SizeBytes">The copy's size.</param>
internal sealed record ImportStagedCopy(string StagedId, string Sha256Hex, long SizeBytes);

/// <summary>
/// One message on the import stream: a progress report, or the finished result as the last message. Exactly one of
/// the two is set.
/// </summary>
/// <param name="Progress">A progress report, or <see langword="null"/> for the result message.</param>
/// <param name="Result">The finished import, or <see langword="null"/> for a progress report.</param>
internal sealed record ImportCommandEvent(ConversationImportProgress? Progress, ConversationImportResult? Result);

/// <summary>
/// The four router calls <see cref="ImportConversationsCommand"/> makes, in the order it makes them. An interface so
/// a test can stand in a fake router that records that order; the production implementation is
/// <see cref="GrpcConversationImportRouter"/>. Every method reports a failed call by throwing
/// <see cref="RpcException"/>, which the command maps to an exit code.
/// </summary>
internal interface IConversationImportRouter
{
    /// <summary>Asks the router to copy the zip into private staging and hash the copy.</summary>
    /// <param name="sourcePath">The zip's absolute path on the router machine.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The staged copy and its digest.</returns>
    Task<ImportStagedCopy> StageAsync(string sourcePath, CancellationToken cancellationToken);

    /// <summary>Files a request for the operator to approve importing the staged copy in the dashboard.</summary>
    /// <param name="stagedId">The staged copy to import.</param>
    /// <param name="options">How the import will run, shown to the operator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where to approve the request and when it lapses.</returns>
    Task<ExportApprovalTicket> CreateApprovalRequestAsync(
        string stagedId, ConversationImportOptions options, CancellationToken cancellationToken);

    /// <summary>Waits until the operator decides the request or it lapses.</summary>
    /// <param name="approvalId">The id <see cref="CreateApprovalRequestAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How the request ended, with the authorization when it was approved.</returns>
    Task<ExportApprovalResult> WaitForApprovalAsync(string approvalId, CancellationToken cancellationToken);

    /// <summary>Runs the approved import and streams its progress.</summary>
    /// <param name="stagedId">The staged copy the authorization was granted for.</param>
    /// <param name="options">The options the request showed the operator.</param>
    /// <param name="authorizationToken">The authorization <see cref="WaitForApprovalAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>Progress reports, then the result.</returns>
    IAsyncEnumerable<ImportCommandEvent> ImportAsync(
        string stagedId, ConversationImportOptions options, string authorizationToken, CancellationToken cancellationToken);
}

/// <summary>
/// The router's <c>--import-conversations &lt;path&gt;</c> command (#165 phase 4, PR 4b; ADR-0020): imports a
/// conversation zip into the history without reading or writing any session file itself. It is a gRPC client of the
/// <em>running</em> router, so there is one import code path and the passkey gate cannot be bypassed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flow.</b> The command asks the router to stage a private copy of the zip and hash it, files an approval
/// request for that copy, prints the dashboard address, waits while the operator approves it there with a passkey,
/// collects the authorization, and only then calls the import RPC with it. The authorization is bound to the digest
/// of the router's own copy, so the approved bytes are the bytes imported, whatever happens to the original file
/// meanwhile. Nothing enters the history until the operator has approved.
/// </para>
/// <para>
/// <b>Options.</b> <c>--timestamps original|import-time</c> (default <c>original</c>) and
/// <c>--on-conflict skip|overwrite|keep-both</c> (default <c>skip</c>). <c>skip</c> keeps the local copy of a turn
/// that differs; the other two replace it, in the way their names say, and are the same as turning "keep the local
/// copy" off in the dashboard. Each takes its value as the next argument or as <c>--option=value</c>.
/// </para>
/// <para>
/// <b>Exit codes</b> are the same as for <c>--export-conversations</c>; see the <c>Exit*</c> constants.
/// </para>
/// </remarks>
internal static class ImportConversationsCommand
{
    /// <summary>The command-line flag that selects this command; its value is the zip's path.</summary>
    internal const string FlagName = "--import-conversations";

    /// <summary>Exit code: the import ran to the end.</summary>
    internal const int ExitSuccess = ExportConversationsCommand.ExitSuccess;

    /// <summary>Exit code: something unexpected failed, or the command was interrupted.</summary>
    internal const int ExitFailed = ExportConversationsCommand.ExitFailed;

    /// <summary>Exit code: the arguments were invalid; nothing was sent to the router.</summary>
    internal const int ExitUsage = ExportConversationsCommand.ExitUsage;

    /// <summary>Exit code: this account cannot open the router service's data directory, so it has no admin token to present.</summary>
    internal const int ExitNotElevated = ExportConversationsCommand.ExitNotElevated;

    /// <summary>Exit code: the operator denied the import in the dashboard; nothing was imported.</summary>
    internal const int ExitDenied = ExportConversationsCommand.ExitDenied;

    /// <summary>Exit code: nobody approved the import before the request lapsed; nothing was imported.</summary>
    internal const int ExitTimedOut = ExportConversationsCommand.ExitTimedOut;

    /// <summary>Exit code: the router could not be reached; is it running?</summary>
    internal const int ExitRouterUnreachable = ExportConversationsCommand.ExitRouterUnreachable;

    /// <summary>Exit code: the router refused the source or the import (no passkey enrolled, not a usable zip, another import running, a full disk).</summary>
    internal const int ExitRefused = ExportConversationsCommand.ExitRefused;

    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Parses the command's arguments. A relative path is resolved against the working directory (a path
    /// computation only; nothing on disk is touched).
    /// </summary>
    /// <param name="args">The process arguments, including <see cref="FlagName"/> and its value.</param>
    /// <param name="options">The parsed import when the arguments are valid.</param>
    /// <param name="error">What is wrong with the arguments, when they are not valid.</param>
    /// <returns><see langword="true"/> when the arguments are valid.</returns>
    internal static bool TryParse(
        IReadOnlyList<string> args, out ImportConversationsOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        string? source = null;
        var timestamps = ImportTimestampMode.Original;
        var conflict = ImportConflictMode.Skip;

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
                case FlagName: source = value; break;
                case "--timestamps":
                    switch (value.ToLowerInvariant())
                    {
                        case "original": timestamps = ImportTimestampMode.Original; break;
                        case "import-time": timestamps = ImportTimestampMode.ImportTime; break;
                        default:
                            error = $"--timestamps '{value}' is not 'original' or 'import-time'.";
                            return false;
                    }

                    break;
                case "--on-conflict":
                    switch (value.ToLowerInvariant())
                    {
                        case "skip": conflict = ImportConflictMode.Skip; break;
                        case "overwrite": conflict = ImportConflictMode.Overwrite; break;
                        case "keep-both": conflict = ImportConflictMode.KeepBoth; break;
                        default:
                            error = $"--on-conflict '{value}' is not 'skip', 'overwrite' or 'keep-both'.";
                            return false;
                    }

                    break;
            }
        }

        if (source is null)
        {
            error = $"{FlagName} requires the path of the zip to import.";
            return false;
        }

        // "skip" is keeping the local copy; the other two modes are what happens when it is not kept.
        options = new ImportConversationsOptions(
            Path.GetFullPath(source),
            new ConversationImportOptions(timestamps, conflict, KeepLocalCopy: conflict == ImportConflictMode.Skip));
        return true;
    }

    /// <summary>
    /// Runs the hand-off against <paramref name="router"/>: stages the zip, files the approval request, prints where
    /// to approve it, waits for the decision, and imports with the authorization it is given. Never throws for a
    /// failed call; each outcome is an exit code and a message on <paramref name="stderr"/>.
    /// </summary>
    /// <param name="options">The import to run.</param>
    /// <param name="router">The running router.</param>
    /// <param name="stdout">Receives the approval address and progress.</param>
    /// <param name="stderr">Receives refusals and errors.</param>
    /// <param name="logger">Receives an unexpected failure's details; optional.</param>
    /// <param name="approvalTimeout">How long to wait for the operator before giving up locally; defaults to the router's request lifetime plus a margin.</param>
    /// <param name="progressInterval">The least time between progress lines; defaults to two seconds.</param>
    /// <param name="timeProvider">The clock behind <paramref name="progressInterval"/>; defaults to the system clock.</param>
    /// <param name="cancellationToken">Cancels the command (for example on Ctrl+C).</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        ImportConversationsOptions options,
        IConversationImportRouter router,
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
            var staged = await router.StageAsync(options.SourcePath, cancellationToken).ConfigureAwait(false);
            var ticket = await router.CreateApprovalRequestAsync(staged.StagedId, options.Options, cancellationToken)
                .ConfigureAwait(false);
            await stdout.WriteLineAsync("Approve this import in your browser:").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  {ticket.DashboardUrl}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"Source: {options.SourcePath}").ConfigureAwait(false);
            await stdout.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Staged copy: {staged.SizeBytes} bytes, SHA-256 {staged.Sha256Hex}")).ConfigureAwait(false);
            await stdout.WriteLineAsync(
                $"Waiting for approval; the request expires at {ticket.ExpiresAtUtc:u}.").ConfigureAwait(false);

            var approval = await WaitAsync(
                router, ticket.ApprovalId, approvalTimeout ?? PendingApprovalTable.RequestTtl + TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            switch (approval.Outcome)
            {
                case PendingApprovalOutcome.Denied:
                    await stderr.WriteLineAsync("The import was denied in the dashboard. Nothing was imported.")
                        .ConfigureAwait(false);
                    return ExitDenied;
                case PendingApprovalOutcome.Expired:
                    return await TimedOutAsync(stderr).ConfigureAwait(false);
            }

            return await ImportAsync(
                staged.StagedId, options, router, approval.AuthorizationToken ?? string.Empty, stdout, stderr,
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
            await stderr.WriteLineAsync("The import was cancelled.").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Top level of a one-shot command: anything else (a malformed response, an I/O error on the console)
            // becomes an exit code instead of a crash dump.
            logger?.LogError(ex, "The conversation import command failed unexpectedly.");
            await stderr.WriteLineAsync($"The import failed unexpectedly: {ex.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    /// <summary>
    /// Waits for the operator's decision, giving up locally after <paramref name="timeout"/> so a router that never
    /// answers cannot hang the command.
    /// </summary>
    /// <param name="router">The running router.</param>
    /// <param name="approvalId">The request to wait for.</param>
    /// <param name="timeout">The longest to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How the request ended.</returns>
    /// <exception cref="ApprovalTimeoutException">When <paramref name="timeout"/> passes with no decision.</exception>
    private static async Task<ExportApprovalResult> WaitAsync(
        IConversationImportRouter router, string approvalId, TimeSpan timeout, CancellationToken cancellationToken)
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

    /// <summary>Runs the approved import and reports its progress and result.</summary>
    /// <param name="stagedId">The staged copy to import.</param>
    /// <param name="options">The import to run.</param>
    /// <param name="router">The running router.</param>
    /// <param name="authorizationToken">The approved authorization.</param>
    /// <param name="stdout">Receives progress and the result.</param>
    /// <param name="stderr">Receives warnings about rejected turns.</param>
    /// <param name="progressInterval">The least time between progress lines.</param>
    /// <param name="time">The clock behind <paramref name="progressInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> ImportAsync(
        string stagedId,
        ImportConversationsOptions options,
        IConversationImportRouter router,
        string authorizationToken,
        TextWriter stdout,
        TextWriter stderr,
        TimeSpan progressInterval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await stdout.WriteLineAsync("Approved. Importing...").ConfigureAwait(false);

        var lastProgress = time.GetTimestamp();
        ConversationImportResult? result = null;
        await foreach (var message in router.ImportAsync(stagedId, options.Options, authorizationToken, cancellationToken)
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
                $"  {progress.TurnsProcessed} turns examined, {progress.TurnsStored} stored")).ConfigureAwait(false);
        }

        if (result is null)
        {
            await stderr.WriteLineAsync(
                "The router ended the import without a result; run it again, which only adds what is missing.")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Imported {result.TurnsAdded} turns into {result.SessionsAdded} new conversations; {result.TurnsAlreadyPresent} turns were already present, {result.TurnsConflictKept} kept the local copy, {result.SessionsSkipped} conversations matched exactly and were skipped.")).ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{result.TurnsToGrade} imported turns arrived without a score and will be graded like any other unscored turn.")).ConfigureAwait(false);
        if (result.TurnsDueForRetention > 0)
        {
            await stdout.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"The next retention pass will delete {result.TurnsDueForRetention} turns. Imports with --timestamps import-time are kept by making them the newest history.")).ConfigureAwait(false);
        }

        if (result.TurnsRejected > 0)
        {
            await stderr.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Warning: {result.TurnsRejected} turns failed validation and were left out.")).ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    /// <summary>Reports a request that lapsed and returns its exit code.</summary>
    /// <param name="stderr">Receives the message.</param>
    /// <returns><see cref="ExitTimedOut"/>.</returns>
    private static async Task<int> TimedOutAsync(TextWriter stderr)
    {
        await stderr.WriteLineAsync("Nobody approved the import before the request expired. Nothing was imported.")
            .ConfigureAwait(false);
        return ExitTimedOut;
    }

    /// <summary>
    /// Maps a failed router call to an exit code and message: an unreachable router, a request or staged copy that
    /// already lapsed, or a refusal.
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
                // The router no longer holds the request or the staged copy: it lapsed, or was already collected.
                await stderr.WriteLineAsync(
                    "The router no longer holds this import: the approval request or the staged copy expired or was already used. Nothing was imported.")
                    .ConfigureAwait(false);
                return ExitTimedOut;

            case StatusCode.Unauthenticated or StatusCode.PermissionDenied or StatusCode.FailedPrecondition
                or StatusCode.InvalidArgument or StatusCode.ResourceExhausted or StatusCode.AlreadyExists
                or StatusCode.Unimplemented:
                await stderr.WriteLineAsync($"The router refused the import: {exception.Status.Detail}")
                    .ConfigureAwait(false);
                return ExitRefused;

            default:
                await stderr.WriteLineAsync(
                    $"The import failed ({exception.StatusCode}): {exception.Status.Detail}").ConfigureAwait(false);
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
        name.ToLowerInvariant() is FlagName or "--timestamps" or "--on-conflict";

    /// <summary>Signals that the local wait for a decision ran out, distinct from the caller cancelling.</summary>
    private sealed class ApprovalTimeoutException : Exception;
}
