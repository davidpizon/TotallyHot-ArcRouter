using System.Collections.Concurrent;
using System.Security.Cryptography;
using Grpc.Core;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>How a <see cref="PendingApprovalRequest"/> ended.</summary>
public enum PendingApprovalOutcome
{
    /// <summary>The operator approved the request with a passkey.</summary>
    Approved,

    /// <summary>The operator refused the request in the dashboard.</summary>
    Denied,

    /// <summary>Nobody decided before the request's time to live ran out.</summary>
    Expired,
}

/// <summary>
/// A gated operation that a caller without a browser has asked the operator to approve (ADR-0020, Amendment 1).
/// Everything the dashboard shows the operator is here, and <paramref name="Parameters"/> is what the passkey
/// ceremony is bound to, so the dashboard approves exactly what it displays.
/// </summary>
/// <param name="Id">Opaque, unguessable id; the caller waits on it and the dashboard approves it.</param>
/// <param name="Operation">The <see cref="GatedOperation"/> constant the approval authorizes.</param>
/// <param name="Parameters">The digest the approval is bound to, for example <see cref="GatedOperation.ExportParameters"/>.</param>
/// <param name="Filter">Which turns the export includes, as the caller sent it.</param>
/// <param name="DestinationPath">The export's destination, exactly as the caller sent it.</param>
/// <param name="CreatedAtUtc">When the request was filed.</param>
/// <param name="ExpiresAtUtc">When the request lapses if nobody decides it.</param>
public sealed record PendingApprovalRequest(
    string Id,
    string Operation,
    string Parameters,
    ConversationExportFilter Filter,
    string DestinationPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>The final decision on a request, handed to the one caller that collects it.</summary>
/// <param name="Request">The request that was decided.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="CredentialName">The passkey the operator used, or empty unless <paramref name="Outcome"/> is <see cref="PendingApprovalOutcome.Approved"/>.</param>
public sealed record PendingApprovalDecision(
    PendingApprovalRequest Request,
    PendingApprovalOutcome Outcome,
    string CredentialName);

/// <summary>
/// In-memory table of approval requests waiting for the operator (ADR-0020, Amendment 1). The
/// <c>--export-conversations</c> command cannot run a passkey ceremony, so it files a request here, the dashboard
/// lists it and runs the ceremony, and the command waits for the outcome. Nothing is persisted: a router restart
/// drops every pending request, which is the safe direction, because a dropped request only means the command
/// is asked to run again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxPending"/> requests are held and each lapses after
/// <see cref="RequestTtl"/>, so a local process that floods the router with requests (it holds the management
/// token, so it can) costs a bounded amount of memory and nothing more; ADR-0020 already accepts denial of
/// service by such a process. A full table refuses new requests with
/// <see cref="StatusCode.ResourceExhausted"/> rather than evicting one the operator may be reading.
/// </para>
/// <para>
/// <b>Collected once.</b> A decision is handed to exactly one <see cref="WaitAsync"/> caller; the entry is then
/// gone, so a second waiter cannot also be given an authorization for the same approval.
/// </para>
/// </remarks>
public sealed class PendingApprovalTable
{
    /// <summary>How long a request stays open for the operator to decide before it lapses.</summary>
    public static readonly TimeSpan RequestTtl = TimeSpan.FromMinutes(5);

    /// <summary>The most requests held at once.</summary>
    public const int MaxPending = 16;

    private const int IdBytes = 16;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Uri _dashboardBaseUrl;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="PendingApprovalTable"/> class.</summary>
    /// <param name="dashboardBaseUrl">
    /// The dashboard's base address at the WebAuthn origin (<c>https://localhost:{port}</c>): a passkey ceremony only
    /// works there, so it is where a request must be approved.
    /// </param>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public PendingApprovalTable(Uri dashboardBaseUrl, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dashboardBaseUrl);
        _dashboardBaseUrl = dashboardBaseUrl;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Files a request to export <paramref name="filter"/> to <paramref name="destinationPath"/>, bound to the
    /// digest <see cref="GatedOperation.ExportParameters"/> computes for them.
    /// </summary>
    /// <param name="filter">Which turns the export includes.</param>
    /// <param name="destinationPath">The zip's destination, exactly as the export call will send it.</param>
    /// <returns>The pending request.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.ResourceExhausted"/> when <see cref="MaxPending"/> requests are already open.</exception>
    public PendingApprovalRequest CreateExport(ConversationExportFilter filter, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(destinationPath);

        Sweep();
        if (_entries.Count >= MaxPending)
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                "Too many approval requests are waiting. Decide or let the open ones lapse, then try again."));
        }

        var now = _time.GetUtcNow();
        var request = new PendingApprovalRequest(
            Id: Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdBytes)),
            Operation: GatedOperation.Export,
            Parameters: GatedOperation.ExportParameters(filter, destinationPath),
            Filter: filter,
            DestinationPath: destinationPath,
            CreatedAtUtc: now,
            ExpiresAtUtc: now + RequestTtl);

        // A 128-bit random id cannot collide in practice; TryAdd keeps that from being an assumption.
        if (!_entries.TryAdd(request.Id, new Entry(request)))
        {
            throw new RpcException(new Status(StatusCode.Internal, "Could not allocate an approval request id."));
        }

        return request;
    }

    /// <summary>Builds the dashboard address that opens the approval view on a request.</summary>
    /// <param name="approvalId">The request's id.</param>
    /// <returns>The absolute dashboard URL.</returns>
    public string DashboardUrlFor(string approvalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        return new Uri(_dashboardBaseUrl, $"/?approval={Uri.EscapeDataString(approvalId)}").AbsoluteUri;
    }

    /// <summary>Lists the requests still waiting for a decision, oldest first.</summary>
    /// <returns>A snapshot of the open requests.</returns>
    public IReadOnlyList<PendingApprovalRequest> ListPending()
    {
        Sweep();
        return
        [
            .. _entries.Values
                .Where(entry => !entry.Completion.Task.IsCompleted)
                .Select(entry => entry.Request)
                .OrderBy(request => request.CreatedAtUtc),
        ];
    }

    /// <summary>Returns the request with this id if it is still waiting for a decision.</summary>
    /// <param name="approvalId">The request's id.</param>
    /// <returns>The open request, or <see langword="null"/> when it is unknown, decided or lapsed.</returns>
    public PendingApprovalRequest? TryGetPending(string? approvalId)
    {
        if (string.IsNullOrWhiteSpace(approvalId)) return null;

        Sweep();
        return _entries.TryGetValue(approvalId, out var entry) && !entry.Completion.Task.IsCompleted
            ? entry.Request
            : null;
    }

    /// <summary>
    /// Marks the request approved. Only the first decision counts: a request that was denied, already approved or
    /// has lapsed is left as it is.
    /// </summary>
    /// <param name="approvalId">The request's id.</param>
    /// <param name="credentialName">The passkey the operator used.</param>
    /// <returns><see langword="true"/> when this call approved it.</returns>
    public bool TryApprove(string? approvalId, string credentialName) =>
        TryDecide(approvalId, PendingApprovalOutcome.Approved, credentialName);

    /// <summary>
    /// Marks the request denied. Only the first decision counts, as for <see cref="TryApprove"/>.
    /// </summary>
    /// <param name="approvalId">The request's id.</param>
    /// <returns><see langword="true"/> when this call denied it.</returns>
    public bool TryDeny(string? approvalId) => TryDecide(approvalId, PendingApprovalOutcome.Denied, string.Empty);

    /// <summary>
    /// Waits until the request is decided or lapses, then hands the decision to this caller and removes the
    /// request, so no other caller can collect it.
    /// </summary>
    /// <param name="approvalId">The request's id.</param>
    /// <param name="cancellationToken">Cancels the wait; the request stays open until it lapses.</param>
    /// <returns>The decision, or <see langword="null"/> when the id is unknown or another caller collected it.</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task<PendingApprovalDecision?> WaitAsync(string? approvalId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approvalId) || !_entries.TryGetValue(approvalId, out var entry)) return null;

        var remaining = entry.Request.ExpiresAtUtc - _time.GetUtcNow();
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        Decision decision;
        try
        {
            decision = await entry.Completion.Task.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Lapsing races with an approval that lands at the same moment; whichever completes the task first
            // wins and the other sees it already completed.
            entry.Completion.TrySetResult(new Decision(PendingApprovalOutcome.Expired, string.Empty));
            decision = await entry.Completion.Task.ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref entry.Collected, 1) != 0) return null;

        _entries.TryRemove(new KeyValuePair<string, Entry>(approvalId, entry));
        return new PendingApprovalDecision(entry.Request, decision.Outcome, decision.CredentialName);
    }

    /// <summary>Records a decision unless the request is unknown, already decided, or past its expiry.</summary>
    private bool TryDecide(string? approvalId, PendingApprovalOutcome outcome, string credentialName)
    {
        if (string.IsNullOrWhiteSpace(approvalId) || !_entries.TryGetValue(approvalId, out var entry)) return false;

        if (_time.GetUtcNow() >= entry.Request.ExpiresAtUtc)
        {
            entry.Completion.TrySetResult(new Decision(PendingApprovalOutcome.Expired, string.Empty));
            return false;
        }

        return entry.Completion.TrySetResult(new Decision(outcome, credentialName));
    }

    /// <summary>
    /// Drops requests past their expiry, first telling any caller still waiting on one that it lapsed, so the
    /// table's size reflects live requests only.
    /// </summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, entry) in _entries)
        {
            if (now < entry.Request.ExpiresAtUtc) continue;

            entry.Completion.TrySetResult(new Decision(PendingApprovalOutcome.Expired, string.Empty));
            _entries.TryRemove(new KeyValuePair<string, Entry>(id, entry));
        }
    }

    /// <summary>The decision stored in an entry's completion source.</summary>
    /// <param name="Outcome">How the request ended.</param>
    /// <param name="CredentialName">The passkey used, or empty unless approved.</param>
    private readonly record struct Decision(PendingApprovalOutcome Outcome, string CredentialName);

    /// <summary>One open request and the signal a waiter blocks on.</summary>
    private sealed class Entry(PendingApprovalRequest request)
    {
        /// <summary>Set to 1 by the single caller that collects the decision.</summary>
        public int Collected;

        /// <summary>Gets the request this entry holds.</summary>
        public PendingApprovalRequest Request { get; } = request;

        /// <summary>Gets the source completed by the first decision; continuations run off the deciding thread.</summary>
        public TaskCompletionSource<Decision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
