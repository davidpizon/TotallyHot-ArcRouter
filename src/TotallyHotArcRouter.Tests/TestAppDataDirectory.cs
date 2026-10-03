using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// Redirects <see cref="AppDataPaths"/> into a per-run scratch directory before any code in this test
/// assembly runs, so no test reads or writes the real machine-shared data directory
/// (<c>%ProgramData%\TotallyHotArcRouter</c> on Windows) or the real per-user root legacy storage is
/// adopted from. This is the isolation <c>TempDatabase</c> gives one SQLite file, applied to every
/// default path at once.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, any test that reached a default path used the real directory: starting
/// <c>ProxyHostedService</c> rewrote <c>web-interface.json</c> and loaded the real CA and
/// <c>secrets.dat</c>, a TLS <c>ProxyServer</c> loaded the real leaf certificate, building the host
/// opened the real log directory and read the operator's <c>appsettings.local.json</c>, and the model
/// sync tests created and deleted cache folders under the real <c>models</c> directory.
/// </para>
/// <para>
/// A module initializer rather than an xUnit assembly fixture, because the redirect has to come before
/// every possible first resolution, including assembly fixtures and static initializers in test
/// classes. <see cref="AppDataPaths"/> memoizes its answer and refuses to be redirected afterward, so a
/// redirect that came too late would throw here rather than quietly let a test touch the real directory.
/// The scratch root sits inside this run's <see cref="TestScratchDirectory.RunRoot"/>, so
/// <see cref="TestTempDirectorySweeper"/> deletes it with the rest of the run's scratch at the end.
/// </para>
/// <para>
/// The one deliberate exception is <c>InstalledBenchmarkCorpus</c>, which the CodeRouterBench
/// reconciliation tests use to read the real synced corpus on purpose. Even that only copies the file
/// into this scratch root with a shared read, and never opens SQLite in the real directory.
/// </para>
/// </remarks>
internal static class TestAppDataDirectory
{
    /// <summary>Gets this run's app-data scratch root, inside <see cref="TestScratchDirectory.RunRoot"/>.</summary>
    public static string Root { get; } = Path.Combine(path1: TestScratchDirectory.RunRoot, path2: "appdata");

    /// <summary>
    /// Gets the directory standing in for the machine-shared data directory. It ends in
    /// <see cref="AppDataPaths.ApplicationDirectoryName"/>, as the real Windows and macOS directories do.
    /// </summary>
    public static string MachineSharedDirectory { get; } = Path.Combine(path1: Root, path2: "ProgramData",
        path3: AppDataPaths.ApplicationDirectoryName);

    /// <summary>Gets the directory standing in for the per-user application-data root (<c>%LOCALAPPDATA%</c>).</summary>
    public static string PerUserRoot { get; } = Path.Combine(path1: Root, path2: "LocalAppData");

    /// <summary>Installs the redirect. The runtime calls this when the test assembly loads.</summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        AppDataPaths.RedirectForTesting(machineSharedDirectory: MachineSharedDirectory, perUserRoot: PerUserRoot);
    }
}
