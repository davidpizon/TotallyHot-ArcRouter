namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Launches the detached <c>Updater.exe</c> process. An abstraction over
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> so
/// <see cref="UpdateApplier"/>'s download-and-verify logic can be exercised in a unit test without ever
/// spawning a real process, mirroring <see cref="IUpdateApplier"/>'s own role one level up (a seam the
/// real implementation wires to <c>Process.Start</c>, and a fake substitutes in tests).
/// </summary>
public interface IUpdaterProcessLauncher
{
    /// <summary>
    /// Starts <c>Updater.exe</c> at <paramref name="updaterPath"/>, passing the install directory, the
    /// verified zip path, the service name, the current process's id, and the zip's expected SHA256 as
    /// <c>--install-dir</c>/<c>--zip-path</c>/<c>--service-name</c>/<c>--wait-pid</c>/
    /// <c>--expected-sha256</c> - see <c>ArgumentParser</c> on the updater side, where all five are
    /// required.
    /// </summary>
    /// <param name="updaterPath">Full path to <c>TotallyHotArcRouter.Updater.exe</c>.</param>
    /// <param name="installDirectory">The Router install directory the updater should swap.</param>
    /// <param name="zipPath">The downloaded, already-verified Router release zip.</param>
    /// <param name="serviceName">The Windows Service name to stop and restart around the swap.</param>
    /// <param name="expectedSha256">
    /// The release's published SHA256 for <paramref name="zipPath"/>. Passed even though
    /// <see cref="UpdateApplier"/> has already checked it, because the updater re-verifies it as the
    /// authoritative check at its own privilege boundary rather than trusting its caller.
    /// </param>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    void Launch(string updaterPath, string installDirectory, string zipPath, string serviceName, string expectedSha256);
}

/// <summary>Production <see cref="IUpdaterProcessLauncher"/>, wrapping <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> directly.</summary>
public sealed class ProcessUpdaterLauncher : IUpdaterProcessLauncher
{
    /// <inheritdoc />
    public void Launch(string updaterPath, string installDirectory, string zipPath, string serviceName, string expectedSha256)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add("--zip-path");
        startInfo.ArgumentList.Add(zipPath);
        startInfo.ArgumentList.Add("--service-name");
        startInfo.ArgumentList.Add(serviceName);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--expected-sha256");
        startInfo.ArgumentList.Add(expectedSha256);

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Process.Start returned null for '{updaterPath}'.");
        }
    }
}
