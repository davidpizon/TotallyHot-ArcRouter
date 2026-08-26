namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// The subset of Windows Service control <see cref="UpdaterService"/> needs: stop, start, and a running
/// check, each with its own bounded wait. An abstraction over <see cref="System.ServiceProcess.ServiceController"/>
/// (the repo's established <c>IEnvironmentVariableProvider</c>-style pattern for wrapping a static-platform
/// API behind a seam) so unit tests never need an actually-installed Windows service.
/// </summary>
public interface IServiceController
{
    /// <summary>Stops <paramref name="serviceName"/> and waits up to <paramref name="timeout"/> for it to reach the Stopped state.</summary>
    /// <exception cref="TimeoutException">The service did not stop within <paramref name="timeout"/>.</exception>
    void Stop(string serviceName, TimeSpan timeout);

    /// <summary>Starts <paramref name="serviceName"/> and waits up to <paramref name="timeout"/> for it to reach the Running state.</summary>
    /// <exception cref="TimeoutException">The service did not start within <paramref name="timeout"/>.</exception>
    void Start(string serviceName, TimeSpan timeout);

    /// <summary>Returns whether <paramref name="serviceName"/> is currently in the Running state.</summary>
    bool IsRunning(string serviceName);
}
