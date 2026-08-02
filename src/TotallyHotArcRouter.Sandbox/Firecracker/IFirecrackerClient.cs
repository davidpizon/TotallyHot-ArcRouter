namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// The Firecracker REST API surface <see cref="FirecrackerMicroVmLauncher"/> drives. Abstracted so the
/// launcher's orchestration (machine sizing applied before snapshot restore) is unit-testable without a
/// running Firecracker process.
/// </summary>
public interface IFirecrackerClient : IDisposable
{
    /// <summary>Configures the microVM machine (vCPU/memory, no network).</summary>
    /// <param name="payload">The <c>PUT /machine-config</c> JSON payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ConfigureMachineAsync(string payload, CancellationToken cancellationToken);

    /// <summary>Configures the guest's vsock device, wiring it to a host-side UDS.</summary>
    /// <param name="payload">The <c>PUT /vsock</c> JSON payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ConfigureVsockAsync(string payload, CancellationToken cancellationToken);

    /// <summary>Loads a snapshot to restore a pre-booted guest.</summary>
    /// <param name="payload">The <c>PUT /snapshot/load</c> JSON payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task LoadSnapshotAsync(string payload, CancellationToken cancellationToken);

    /// <summary>Resumes or pauses the guest.</summary>
    /// <param name="payload">The <c>PATCH /vm</c> JSON payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetVmStateAsync(string payload, CancellationToken cancellationToken);
}

/// <summary>Creates a per-run <see cref="IFirecrackerClient"/> bound to that run's API socket.</summary>
public interface IFirecrackerClientFactory
{
    /// <summary>Creates a client bound to the given Firecracker API Unix socket.</summary>
    /// <param name="apiSocketPath">The path of the Firecracker API Unix socket.</param>
    /// <returns>A client for that socket.</returns>
    IFirecrackerClient Create(string apiSocketPath);
}

