namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Runs <see cref="ContentGate.RefreshStoreProtection"/> once at startup so the gate knows whether the
/// protected secret store's ACL is acceptable before any gRPC call reaches it (ADR-0020). Until this runs,
/// <see cref="ContentGate.StoreProtected"/> is <see langword="false"/>, so the gate fails closed. Registered
/// after the first service that creates <c>secrets.dat</c>, so the probe sees the file with its final ACL.
/// </summary>
public sealed class PasskeyBootstrapHostedService : IHostedService
{
    private readonly ContentGate _contentGate;
    private readonly ILogger<PasskeyBootstrapHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="PasskeyBootstrapHostedService"/> class.</summary>
    /// <param name="contentGate">The gate whose store-protection state is refreshed.</param>
    /// <param name="logger">Records the probe outcome so an unprotected store is visible in the log.</param>
    public PasskeyBootstrapHostedService(ContentGate contentGate, ILogger<PasskeyBootstrapHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(contentGate);
        ArgumentNullException.ThrowIfNull(logger);
        _contentGate = contentGate;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _contentGate.RefreshStoreProtection();
        if (_contentGate.StoreProtected)
        {
            _logger.LogInformation(
                "Passkey content gate: secret store ACL accepted; enrolled passkeys: {Enrolled}",
                _contentGate.HasPasskeys());
        }
        else
        {
            _logger.LogError(
                "Passkey content gate: the protected secret store ACL was rejected; enrollment and gated operations are refused until it is repaired");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
