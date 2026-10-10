using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model behind the Sessions tab's Export dialog (#165 phase 3): the router's session-store
/// summary, and one conversation export with its progress and result. Wraps
/// <see cref="IConversationAdminClient"/> in the shared <see cref="AdminStoreBase{TClient}"/> shape, so a failed
/// load degrades to the unreachable state instead of crashing the dialog.
/// </summary>
/// <remarks>
/// Load/mutation split, as in <see cref="AdminStoreBase{TClient}"/>: <see cref="LoadSummaryAsync"/> swallows a
/// failure into the reachability state, while <see cref="ExportAsync"/> records it and rethrows, because the
/// operator has to be told that the export they just approved did not happen. The passkey ceremony that earns the
/// authorization is the caller's: this store takes the finished token, like <see cref="ManagementTokenAdminStore"/>.
/// </remarks>
public sealed class ConversationExportStore : AdminStoreBase<IConversationAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationExportStore"/> class over the shared
    /// <see cref="IRouterChannelProvider"/> - see its remarks.
    /// </summary>
    /// <param name="channelProvider">Supplies the shared call invoker this store's client is constructed over.</param>
    /// <param name="logger">Optional logger.</param>
    public ConversationExportStore(IRouterChannelProvider channelProvider, ILogger<ConversationExportStore>? logger = null)
        : base(client: new ConversationAdminClient(channelProvider.CallInvoker), logger: logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationExportStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">The conversation admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public ConversationExportStore(IConversationAdminClient client, ILogger<ConversationExportStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>What the router's session store holds as of the last successful load, or <see langword="null"/> before one.</summary>
    public ConversationExportSummaryInfo? Summary { get; private set; }

    /// <summary>Gets whether an export is running.</summary>
    public bool IsExporting { get; private set; }

    /// <summary>How far the running (or last) export has got, or <see langword="null"/> before the first report.</summary>
    public ConversationExportProgressInfo? Progress { get; private set; }

    /// <summary>What the last export wrote, or <see langword="null"/> when none has finished since <see cref="Reset"/>.</summary>
    public ConversationExportResultInfo? Result { get; private set; }

    /// <summary>
    /// Reads the session-store summary. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>Whether the summary was read.</returns>
    public Task<bool> LoadSummaryAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Summary = await Client.GetSummaryAsync(ct).ConfigureAwait(false),
            "read the conversation store",
            cancellationToken);
    }

    /// <summary>Clears the last export's progress and result, so the dialog shows its form again.</summary>
    public void Reset()
    {
        if (IsExporting || (Progress is null && Result is null)) return;

        Progress = null;
        Result = null;
        NotifyChanged();
    }

    /// <summary>
    /// Runs one export, updating <see cref="Progress"/> as the router reports and setting <see cref="Result"/>
    /// when it finishes. The router refuses the call unless <paramref name="authorizationToken"/> was earned for
    /// exactly this <paramref name="filter"/> and <paramref name="destinationPath"/> - see
    /// <see cref="PasskeyOperations.ExportParameters"/>.
    /// </summary>
    /// <param name="filter">Which turns to include; must be the filter the authorization was bound to.</param>
    /// <param name="destinationPath">The zip's absolute path on the router machine; must be the path the authorization was bound to.</param>
    /// <param name="authorizationToken">The one-operation authorization from the passkey ceremony.</param>
    /// <param name="cancellationToken">Cancels the export, which then leaves no file behind.</param>
    /// <returns>What the export wrote.</returns>
    /// <exception cref="GrpcAdminException">The call failed, was not authorized, was refused, or the router is unreachable.</exception>
    /// <exception cref="InvalidOperationException">The stream ended without a result.</exception>
    public async Task<ConversationExportResultInfo> ExportAsync(
        ConversationExportFilterInfo filter,
        string destinationPath,
        string authorizationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        IsExporting = true;
        Progress = null;
        Result = null;
        NotifyChanged();

        try
        {
            await foreach (var update in Client
                               .ExportAsync(filter, destinationPath, authorizationToken, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (update.Progress is { } progress)
                {
                    Progress = progress;
                    NotifyChanged();
                }
                else if (update.Result is { } result)
                {
                    Result = result;
                }
            }

            var finished = Result ?? throw new InvalidOperationException("The router ended the export without a result.");
            RecordSuccess(marksLoaded: false);
            return finished;
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a conversation export");
            throw;
        }
        finally
        {
            IsExporting = false;
            NotifyChanged();
        }
    }
}
