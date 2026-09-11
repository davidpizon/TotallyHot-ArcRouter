using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Regret Harness panel. Wraps
/// <see cref="RegretHarnessAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives tab switches and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
public sealed class RegretHarnessAdminStore : AdminStoreBase<IRegretHarnessAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegretHarnessAdminStore"/> class, creating and owning a
    /// client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public RegretHarnessAdminStore(
        ILogger<RegretHarnessAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new RegretHarnessAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RegretHarnessAdminStore"/> class over a
    /// caller-supplied client. The seam tests use to drive the store without a live proxy; the caller
    /// owns the client's lifetime.
    /// </summary>
    /// <param name="client">The admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public RegretHarnessAdminStore(IRegretHarnessAdminClient client, ILogger<RegretHarnessAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The last completed run's status, or <see langword="null"/> before the first load.</summary>
    public RegretHarnessStatusInfo? Status { get; private set; }

    /// <summary>Whether a run is currently in progress, so the UI can disable the button and show progress.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The coarse stage a running harness is currently in, or <see langword="null"/> before any progress has been reported.</summary>
    public RegretHarnessStageInfo? CurrentStage { get; private set; }

    /// <summary>The most recent run's outcome message, or <see langword="null"/> before any run has completed this session.</summary>
    public string? LastRunMessage { get; private set; }

    /// <summary>
    /// Loads the last completed run's status. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an error state instead of crashing when the proxy isn't
    /// running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Status = await Client.GetStatusAsync(ct),
            "load the regret harness status",
            cancellationToken);
    }

    /// <summary>
    /// Runs the harness, publishing stage progress into <see cref="CurrentStage"/> as it streams in and
    /// the final outcome once it completes. <see cref="IsRunning"/> is true for the duration.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="GrpcAdminException">The run could not be started or the router is unreachable.</exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        CurrentStage = null;
        LastRunMessage = null;
        NotifyChanged();

        try
        {
            await foreach (var runEvent in Client.RunAsync(cancellationToken))
            {
                if (runEvent.StageProgress is { } stage)
                {
                    CurrentStage = stage;
                }
                else if (runEvent.Result is { } result)
                {
                    LastRunMessage = result.Message;
                    if (result.Kind == RegretHarnessRunResultKindInfo.Completed)
                        Status = new RegretHarnessStatusInfo(HasRun: true, result.RanAtUtc, result.Message,
                            result.Splits);
                }

                NotifyChanged();
            }

            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a regret harness run");
            throw;
        }
        finally
        {
            // In a finally so a failed run re-enables the button rather than leaving it stuck disabled.
            // The exception still propagates for the panel to render.
            IsRunning = false;
            NotifyChanged();
        }
    }
}
