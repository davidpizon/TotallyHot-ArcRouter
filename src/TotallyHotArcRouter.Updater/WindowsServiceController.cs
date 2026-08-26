using System.Runtime.Versioning;
using System.ServiceProcess;

namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// Production <see cref="IServiceController"/>, wrapping <see cref="System.ServiceProcess.ServiceController"/>
/// directly. Windows-only (matches this whole helper's purpose: servicing the Windows Service hosting
/// shipped in Phase 1) - annotated <see cref="SupportedOSPlatformAttribute"/> so the analyzer verifies
/// nothing outside a Windows-only call path constructs this type, rather than discovering the
/// unsupported-platform failure at runtime.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController : IServiceController
{
    /// <inheritdoc />
    public void Stop(string serviceName, TimeSpan timeout)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    /// <inheritdoc />
    public void Start(string serviceName, TimeSpan timeout)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    /// <inheritdoc />
    public bool IsRunning(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        controller.Refresh();
        return controller.Status == ServiceControllerStatus.Running;
    }
}
