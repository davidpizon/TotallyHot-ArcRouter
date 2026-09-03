using System.Globalization;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Components;

/// <summary>
/// Governance &gt; Benchmark Data pane: shows the CodeRouterBench corpus's freshness relative to the
/// published Hugging Face dataset and lets the operator sync it on demand.
/// Talks to the proxy's BenchmarkDataAdminService gRPC API via the injected <see cref="BenchmarkDataStore"/>.
/// </summary>
/// <remarks>
/// No licensing constraint applies here (unlike Price Sources/D5) - the corpus is public benchmark data -
/// so file names, sizes, and row counts are shown freely.
/// </remarks>
public partial class BenchmarkData
{
    /// <summary>Whether the Task Matrix card's per-file disclosure is open. Starts collapsed.</summary>
    private bool _filesExpanded;

    private string? _opError;

    /// <summary>Whether the Local Voter Model card's per-file disclosure is open. Starts collapsed.</summary>
    private bool _voterFilesExpanded;

    private string? _voterOpError;

    /// <inheritdoc/>
    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
        VoterStore.Changed -= OnVoterStoreChanged;
    }

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        VoterStore.Changed += OnVoterStoreChanged;
        await Store.LoadAsync();
        await VoterStore.LoadAsync();
    }

    /// <summary>Re-renders when the corpus store's state changes.</summary>
    private void OnStoreChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    /// <summary>Re-renders when the Local Voter Model store's state changes.</summary>
    private void OnVoterStoreChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The inline banner's text: the last mutation's rejection if there is one, otherwise the last load's.
    /// Both are cleared by the next successful call, so a stale message never outlives the failure.
    /// </summary>
    private string? ErrorMessage()
    {
        return _opError ?? Store.LastError;
    }

    /// <summary>Same convention as <see cref="ErrorMessage"/>, for the Local Voter Model section's own store.</summary>
    private string? VoterErrorMessage()
    {
        return _voterOpError ?? VoterStore.LastError;
    }

    /// <summary>Clears the inline error and reloads the corpus status. Implements the unreachable state's Retry button.</summary>
    private async Task Reload()
    {
        _opError = null;
        await Store.LoadAsync();
    }

    /// <summary>Same purpose as <see cref="Reload"/>, for the Local Voter Model section's own store.</summary>
    private async Task VoterReload()
    {
        _voterOpError = null;
        await VoterStore.LoadAsync();
    }

    /// <summary>
    /// The top-of-page Resync button's label: "Resyncing…" while either card's store is mid-sync,
    /// otherwise "Resync".
    /// </summary>
    private string ResyncLabel()
    {
        return Store.IsSyncing || VoterStore.IsSyncing ? "Resyncing…" : "Resync";
    }

    /// <summary>
    /// Runs both cards' sync operations together: the same call <see cref="RunAction"/> and
    /// <see cref="RunVoterAction"/> make individually, so a single Resync click updates the corpus and the
    /// local voter model in one step. Each already reports its own failures into its own card's error
    /// banner, so running them concurrently is safe.
    /// </summary>
    private Task RunResync()
    {
        return Task.WhenAll(RunAction(), RunVoterAction());
    }

    /// <summary>
    /// The Task Matrix header button's label. The button is only enabled while an update is actually
    /// available, so the label just needs to distinguish that from a failed freshness probe: "Check
    /// Failed" when the last probe could not reach Hugging Face (surfaced even though the button stays
    /// disabled in this state - re-checking is the top Resync button's job), "Updating…" for the duration
    /// of a sync, and "Update" otherwise.
    /// </summary>
    private string ActionLabel()
    {
        return Store.Status?.State switch
        {
            _ when Store.IsSyncing => "Updating…",
            BenchmarkDataAdminState.CheckFailed => "Check Failed",
            _ => "Update"
        };
    }

    /// <summary>
    /// Updates the corpus. Only reachable while the header button is enabled (an update is available), but
    /// still recheck-first when freshness is unknown (CheckFailed, or no status yet) rather than
    /// downloading blind - mirrors the top-of-page Resync button's per-card logic.
    /// </summary>
    private Task RunAction()
    {
        return Store.Status?.State is BenchmarkDataAdminState.Update or BenchmarkDataAdminState.Current
            ? RunAsync(() => Store.SyncAsync())
            : RunAsync(() => Store.RecheckAsync());
    }

    /// <summary>
    /// The Task Matrix card's at-rest summary line: how many files are synced, their combined size and
    /// row count, and the most recent sync time - the aggregate view that replaces the eight individual
    /// file cards this section used to show.
    /// </summary>
    private static string SummaryLine(BenchmarkDataStatusInfo status)
    {
        var syncedCount = status.Files.Count(f => f.Synced);
        var totalMb = status.Files.Sum(f => f.SizeBytes) / 1024.0 / 1024.0;
        var totalRows = status.Files.Sum(f => f.RowCount);

        if (syncedCount == 0) return $"0 of {status.Files.Count} files synced";

        var syncedAt = status.Files
            .Where(f => f.SyncedAtUtc is not null)
            .Select(f => f.SyncedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        return
            $"{syncedCount} of {status.Files.Count} files synced — {totalMb:0.#} MB, {totalRows:N0} rows — last synced {syncedAt.ToLocalTime():g} local";
    }

    /// <summary>The cumulative progress bar's byte counter label, e.g. "4.2 / 11.8 MB".</summary>
    private string CumulativeByteLabel()
    {
        return
            $"{Store.CumulativeBytesTransferred / 1024.0 / 1024.0:0.#} / {Store.CumulativeTotalBytes / 1024.0 / 1024.0:0.#} MB";
    }

    /// <summary>
    /// Formats a fill percentage for a CSS <c>width</c> value using invariant culture, so a locale with a
    /// comma decimal separator (e.g. fr-FR) cannot turn <c>width:52.5%</c> into the invalid CSS
    /// <c>width:52,5%</c>.
    /// </summary>
    private static string PercentCss(double percent)
    {
        return percent.ToString(format: "0.##", provider: CultureInfo.InvariantCulture);
    }

    /// <summary>The cumulative progress bar's fill percentage, 0-100.</summary>
    private double CumulativePercent()
    {
        return Store.CumulativeTotalBytes > 0
            ? Math.Clamp(value: 100.0 * Store.CumulativeBytesTransferred / Store.CumulativeTotalBytes, 0, 100)
            : 0;
    }

    /// <summary>The current-file progress bar's byte counter label, e.g. "1.1 / 3.4 MB".</summary>
    private string CurrentFileByteLabel()
    {
        var progress = Store.CurrentFileName is { } fileName ? Store.SyncProgress.GetValueOrDefault(fileName) : null;
        var totalBytes = progress?.TotalBytes ?? 0;
        var transferred = progress?.BytesTransferred ?? 0;
        return $"{transferred / 1024.0 / 1024.0:0.#} / {totalBytes / 1024.0 / 1024.0:0.#} MB";
    }

    /// <summary>The current-file progress bar's fill percentage, 0-100.</summary>
    private double CurrentFilePercent()
    {
        var progress = Store.CurrentFileName is { } fileName ? Store.SyncProgress.GetValueOrDefault(fileName) : null;
        if (progress is not { TotalBytes: > 0 } || progress.BytesTransferred is not { } transferred)
            return progress?.Stage is BenchmarkSyncStageInfo.Verifying or BenchmarkSyncStageInfo.Importing
                or BenchmarkSyncStageInfo.Completed
                ? 100
                : 0;

        return Math.Clamp(value: 100.0 * transferred / progress.TotalBytes.Value, 0, 100);
    }

    /// <summary>
    /// The Local Voter Model card's at-rest summary line: how many of the 5 files are synced, their
    /// combined size, how many are checksum-verified, and the most recent sync time - the aggregate view
    /// that replaces the five individual file cards this section used to show, mirroring
    /// <see cref="SummaryLine"/>.
    /// </summary>
    private static string VoterSummaryLine(LlmRouterModelStatusInfo status)
    {
        var syncedCount = status.Files.Count(f => f.Synced);
        var totalMb = status.Files.Sum(f => f.SizeBytes) / 1024.0 / 1024.0;
        var verifiedCount = status.Files.Count(f => f.ChecksumVerified);

        if (syncedCount == 0) return $"0 of {status.Files.Count} files synced";

        var syncedAt = status.Files
            .Where(f => f.SyncedAtUtc is not null)
            .Select(f => f.SyncedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        return
            $"{syncedCount} of {status.Files.Count} files synced — {totalMb:0.#} MB, {verifiedCount} verified — last synced {syncedAt.ToLocalTime():g} local";
    }

    /// <summary>The Local Voter Model cumulative progress bar's byte counter label, e.g. "4.2 / 11.8 MB".</summary>
    private string VoterCumulativeByteLabel()
    {
        return
            $"{VoterStore.CumulativeBytesTransferred / 1024.0 / 1024.0:0.#} / {VoterStore.CumulativeTotalBytes / 1024.0 / 1024.0:0.#} MB";
    }

    /// <summary>The Local Voter Model cumulative progress bar's fill percentage, 0-100.</summary>
    private double VoterCumulativePercent()
    {
        return VoterStore.CumulativeTotalBytes > 0
            ? Math.Clamp(value: 100.0 * VoterStore.CumulativeBytesTransferred / VoterStore.CumulativeTotalBytes, 0, 100)
            : 0;
    }

    /// <summary>The Local Voter Model current-file progress bar's byte counter label, e.g. "1.1 / 3.4 MB".</summary>
    private string VoterCurrentFileByteLabel()
    {
        var progress = VoterStore.CurrentFileName is { } fileName
            ? VoterStore.SyncProgress.GetValueOrDefault(fileName)
            : null;
        var totalBytes = progress?.TotalBytes ?? 0;
        var transferred = progress?.BytesTransferred ?? 0;
        return $"{transferred / 1024.0 / 1024.0:0.#} / {totalBytes / 1024.0 / 1024.0:0.#} MB";
    }

    /// <summary>
    /// The Local Voter Model current-file progress bar's fill percentage, 0-100. Unlike
    /// <see cref="CurrentFilePercent"/>, the "treat as complete" stage set has no Importing entry - this
    /// pipeline has no import step.
    /// </summary>
    private double VoterCurrentFilePercent()
    {
        var progress = VoterStore.CurrentFileName is { } fileName
            ? VoterStore.SyncProgress.GetValueOrDefault(fileName)
            : null;
        if (progress is not { TotalBytes: > 0 } || progress.BytesTransferred is not { } transferred)
            return progress?.Stage is LlmRouterModelSyncStageInfo.Verifying or LlmRouterModelSyncStageInfo.Completed
                ? 100
                : 0;

        return Math.Clamp(value: 100.0 * transferred / progress.TotalBytes.Value, 0, 100);
    }

    /// <summary>The display label for one corpus file's current sync stage.</summary>
    private static string StageLabel(BenchmarkFileStatusInfo file, BenchmarkSyncProgressInfo? progress)
    {
        return progress switch
        {
            { Stage: BenchmarkSyncStageInfo.Downloading } => "Downloading…",
            { Stage: BenchmarkSyncStageInfo.Verifying } => "Verifying…",
            { Stage: BenchmarkSyncStageInfo.Importing } => "Importing…",
            { Stage: BenchmarkSyncStageInfo.Completed } => "Synced",
            { Stage: BenchmarkSyncStageInfo.Failed } => "Failed",
            _ => file.Synced ? "Synced" : "Not synced"
        };
    }

    /// <summary>The CSS color class for one corpus file's current sync stage.</summary>
    private static string StageClass(BenchmarkSyncProgressInfo? progress)
    {
        return progress?.Stage switch
        {
            BenchmarkSyncStageInfo.Failed => "text-red-400",
            BenchmarkSyncStageInfo.Completed => "text-emerald-400",
            null => "text-slate-500",
            _ => "text-slate-400"
        };
    }

    /// <summary>
    /// The display text for one corpus file's size/progress: rows imported, bytes downloaded so far, or the at-rest
    /// size and row count.
    /// </summary>
    private static string DescribeSize(BenchmarkFileStatusInfo file, BenchmarkSyncProgressInfo? progress)
    {
        if (progress is { RowsImported: { } rows }) return $"{rows:N0} rows";

        if (progress is { BytesTransferred: { } bytes }) return $"{bytes / 1024.0 / 1024.0:0.#} MB downloaded";

        return file.Synced ? $"{file.SizeBytes / 1024.0 / 1024.0:0.#} MB, {file.RowCount:N0} rows" : "Never synced";
    }

    /// <summary>The display text for one corpus file's last-synced time, or empty if it has never synced.</summary>
    private static string DescribeSyncedAt(BenchmarkFileStatusInfo file)
    {
        return file.SyncedAtUtc is { } syncedAt ? $"Synced {syncedAt.ToLocalTime():g} local" : string.Empty;
    }

    /// <summary>
    /// Runs a mutation, surfacing a failure in the inline error banner rather than letting it escape into
    /// the renderer. Same wrapper shape as PriceSourcesAdmin's.
    /// </summary>
    private async Task<bool> RunAsync(Func<Task> operation)
    {
        _opError = null;
        try
        {
            await operation();
            return true;
        }
        catch (BenchmarkDataAdminException ex)
        {
            _opError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The Local Voter Model header button's label. The button is only enabled while an update is
    /// available (mirrors <see cref="ActionLabel"/>, minus the corpus panel's "Check Failed" state - the
    /// base URL is a fixed pin, not a rolling ref, so there is nothing to recheck against), so the label
    /// just distinguishes "Updating…", for the duration of a sync, from "Update" otherwise.
    /// </summary>
    private string VoterActionLabel()
    {
        return VoterStore.IsSyncing ? "Updating…" : "Update";
    }

    /// <summary>Updates the Local Voter Model. Only reachable while the header button is enabled (an update is available).</summary>
    private Task RunVoterAction()
    {
        return RunVoterAsync(() => VoterStore.SyncAsync());
    }

    /// <summary>The display label for one voter file's current sync stage.</summary>
    private static string VoterStageLabel(LlmRouterModelFileStatusInfo file, LlmRouterModelSyncProgressInfo? progress)
    {
        return progress switch
        {
            { Stage: LlmRouterModelSyncStageInfo.Downloading } => "Downloading…",
            { Stage: LlmRouterModelSyncStageInfo.Verifying } => "Verifying…",
            { Stage: LlmRouterModelSyncStageInfo.Failed } => "Failed",
            // A Completed event with no published size means an absent optional file was deliberately
            // skipped (the export inlines its weights), not that it synced. A cached-and-verified optional
            // file also reports Completed with BytesTransferred: null (it didn't need re-downloading), but
            // still carries its published TotalBytes - that case must fall through to "Synced" below.
            { Stage: LlmRouterModelSyncStageInfo.Completed, TotalBytes: null } when file.IsOptional => "Optional",
            // Otherwise Completed means this file finished this sync, even if the overall model status
            // (file.Synced) hasn't been refreshed yet - same as the at-rest (no progress) case below.
            { Stage: LlmRouterModelSyncStageInfo.Completed } => "Synced",
            _ => file.Synced ? "Synced" : (file.IsOptional ? "Optional" : "Not synced")
        };
    }

    /// <summary>The CSS color class for one voter file's current sync stage.</summary>
    private static string VoterStageClass(LlmRouterModelSyncProgressInfo? progress)
    {
        return progress?.Stage switch
        {
            LlmRouterModelSyncStageInfo.Failed => "text-red-400",
            LlmRouterModelSyncStageInfo.Completed => "text-emerald-400",
            null => "text-slate-500",
            _ => "text-slate-400"
        };
    }

    /// <summary>The display text for one voter file's size/progress: bytes downloaded so far, or the at-rest size.</summary>
    private static string VoterDescribeSize(LlmRouterModelFileStatusInfo file, LlmRouterModelSyncProgressInfo? progress)
    {
        if (progress is { BytesTransferred: { } bytes }) return $"{bytes / 1024.0 / 1024.0:0.#} MB downloaded";

        if (file.Synced) return $"{file.SizeBytes / 1024.0 / 1024.0:0.#} MB";

        // Same "expected, permanent absence" distinction as VoterStageLabel - an optional file that was
        // never published shouldn't read as if a sync simply hasn't happened yet.
        return file.IsOptional ? "Optional - not published for this model" : "Never synced";
    }

    /// <summary>The display text for one voter file's last-synced time, or empty if it has never synced.</summary>
    private static string VoterDescribeSyncedAt(LlmRouterModelFileStatusInfo file)
    {
        return file.SyncedAtUtc is { } syncedAt ? $"Synced {syncedAt.ToLocalTime():g} local" : string.Empty;
    }

    /// <summary>Same wrapper shape as <see cref="RunAsync"/>, for the Local Voter Model section's own store.</summary>
    private async Task<bool> RunVoterAsync(Func<Task> operation)
    {
        _voterOpError = null;
        try
        {
            await operation();
            return true;
        }
        catch (LlmRouterModelAdminException ex)
        {
            _voterOpError = ex.Message;
            return false;
        }
    }
}