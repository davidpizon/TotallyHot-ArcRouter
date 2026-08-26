using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace TotallyHot.ArcRouter.Updater;

/// <summary>
/// Entry point for <c>TotallyHotArcRouter.Updater.exe</c>, the detached helper process the Router's
/// <c>UpdateAdminGrpcService.ApplyUpdate</c> launches to stop the Windows Service, swap the install
/// directory, and restart it (docs/router/auto-update-plan.md Phase 2). Deliberately its own tiny
/// Serilog file-sink pipeline, never the Router's own logging - the Router's pipeline may be down or
/// mid-restart while this process runs.
/// </summary>
public static class Program
{
    /// <summary>
    /// Main entry point. All argument parsing and swap logic live in <see cref="ArgumentParser"/> and
    /// <see cref="UpdaterService"/> respectively, so this method is a thin composition root - mirrors
    /// <c>TotallyHot.ArcRouter.Program</c>'s "logic in separately-testable statics" convention. Marked
    /// <see cref="SupportedOSPlatformAttribute"/> because it constructs the real
    /// <see cref="WindowsServiceController"/> - this whole helper only ever runs on Windows, servicing
    /// the Windows Service hosting shipped in Phase 1.
    /// </summary>
    /// <param name="args">Command-line arguments; see <see cref="ArgumentParser.Parse"/>.</param>
    /// <returns>0 on success, 1 on any failure - see <see cref="UpdaterService.RunAsync"/>.</returns>
    [SupportedOSPlatform("windows")]
    public static async Task<int> Main(string[] args)
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "updater-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);

        try
        {
            UpdaterArguments arguments;
            try
            {
                arguments = ArgumentParser.Parse(args);
            }
            catch (ArgumentException ex)
            {
                loggerFactory.CreateLogger("Updater").LogCritical(ex, "Invalid arguments; exiting.");
                return 1;
            }

            var service = new UpdaterService(
                new ProcessWaiter(),
                new WindowsServiceController(),
                new RealUpdateFileSystem(),
                loggerFactory.CreateLogger<UpdaterService>());

            return await service.RunAsync(arguments).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Updater").LogCritical(ex, "Updater terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }
}
