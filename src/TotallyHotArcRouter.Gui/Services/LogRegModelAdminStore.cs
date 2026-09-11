using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Router Model panel. Wraps
/// <see cref="LogRegModelAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives tab switches and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
public sealed class LogRegModelAdminStore : AdminStoreBase<ILogRegModelAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegModelAdminStore"/> class, creating and owning a
    /// client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public LogRegModelAdminStore(
        ILogger<LogRegModelAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new LogRegModelAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegModelAdminStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    /// <param name="client">The admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public LogRegModelAdminStore(ILogRegModelAdminClient client, ILogger<LogRegModelAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The logreg model's last-known status, or <see langword="null"/> before the first load.</summary>
    public LogRegModelStatusInfo? Status { get; private set; }

    /// <summary>Whether a retrain is currently running, so the UI can disable the button and show progress.</summary>
    public bool IsRetraining { get; private set; }

    /// <summary>The number of OOD bootstrap tasks embedded so far by a running retrain. Reset at the start of each retrain.</summary>
    public int BootstrapTasksEmbedded { get; private set; }

    /// <summary>The most recent retrain's outcome message, or <see langword="null"/> before any retrain has run this session.</summary>
    public string? LastRetrainMessage { get; private set; }

    /// <summary>
    /// Loads the logreg model's current status. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an error state instead of crashing when the proxy isn't
    /// running.
    /// </summary>
    /// <remarks>
    /// Only a connectivity failure clears <see cref="AdminStoreBase{TClient}.IsReachable"/>, the
    /// same split the base applies to mutations: a status call the router *answered* with a rejection has to
    /// keep the panel's normal layout and show the rejection, because collapsing it into the "Router
    /// unreachable" state both misstates the cause and hides the message that explains it.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Status = await Client.GetStatusAsync(ct),
            "load the logreg model status",
            cancellationToken);
    }

    /// <summary>
    /// Runs a retrain, publishing bootstrap-embedding progress into <see cref="BootstrapTasksEmbedded"/> as
    /// it streams in and the final outcome plus status once it completes. <see cref="IsRetraining"/> is true
    /// for the duration.
    /// </summary>
    /// <param name="cancellationToken">Cancels the retrain.</param>
    /// <exception cref="GrpcAdminException">The retrain could not be started or the router is unreachable.</exception>
    public async Task RetrainAsync(CancellationToken cancellationToken = default)
    {
        IsRetraining = true;
        BootstrapTasksEmbedded = 0;
        LastRetrainMessage = null;
        NotifyChanged();

        var retrainingCleared = false;

        try
        {
            await foreach (var retrainEvent in Client.RetrainAsync(cancellationToken))
            {
                if (retrainEvent.BootstrapProgress is { } progress)
                {
                    BootstrapTasksEmbedded = progress.TasksEmbedded;
                }
                else if (retrainEvent.Result is { } result)
                {
                    LastRetrainMessage = result.Message;
                    Status = result.Status;
                }

                NotifyChanged();
            }

            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            // recordRejectionMessage: true because this panel's own catch (see RouterModelAdmin.razor.cs)
            // swallows the exception without capturing its message anywhere else - LastRetrainMessage is
            // only ever set from a streamed Result event, never from a caught exception - so LastError is
            // the only place a rejection's text survives for the markup to render.
            RecordFailure(exception: ex, description: "a logreg-model operation", recordRejectionMessage: true,
                beforeNotify: () =>
                {
                    IsRetraining = false;
                    retrainingCleared = true;
                });
            throw;
        }
        finally
        {
            // The exception still propagates for the panel to render. RecordFailure's beforeNotify already
            // cleared IsRetraining and published the failure's one notification, so this only runs (and
            // notifies) on the success path.
            if (!retrainingCleared)
            {
                IsRetraining = false;
                NotifyChanged();
            }
        }
    }
}
