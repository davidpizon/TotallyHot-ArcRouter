using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace TotallyHot.ArcRouter;

/// <summary>
/// Application entrypoint for the TotallyHot.ArcRouter console host.
/// </summary>
public static class Program
{
    /// <summary>
    /// Main entry point for the application.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            // Headless/CI replacement for scripts/fetch-coderouterbench.sh
            // (docs/router/coderouterbench-sqlite-migration-plan.md Phase 6): stripped before
            // CreateHostBuilder for the same reason --model is, so it never reaches the command-line
            // configuration provider as a stray "sync-benchmark-data" key.
            var (runBenchmarkDataSync, remainingArgs) = ExtractFlag(args, "--sync-benchmark-data");

            // Scoped with `using` so the host is disposed on every exit path - including the
            // --sync-benchmark-data early return and the catch below. Skipping disposal there would
            // leak the DI container's HttpMessageHandler pipelines and open file handles, and skip
            // the flush/cleanup that disposable singletons do on shutdown.
            using var host = CreateHostBuilder(remainingArgs).Build();

            if (runBenchmarkDataSync)
            {
                await RunBenchmarkDataSyncAsync(host.Services);
                return;
            }

            Log.Information("TotallyHot.ArcRouter host created.");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "TotallyHot.ArcRouter host terminated unexpectedly.");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Creates the host builder.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>An <see cref="IHostBuilder"/>.</returns>
    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        var (forcedModelName, remainingArgs) = ExtractModelArg(args);

        return Host.CreateDefaultBuilder(remainingArgs)
            .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                // Streams every log event to the GUI's Console tab over the same telemetry hub as
                // routing events - additive, doesn't replace the Console sink configured above.
                // DeferredTelemetryPublisher (not a direct services.GetRequiredService<ITelemetryPublisher>()
                // here) avoids a circular dependency on the logging system itself being built - see its
                // remarks for why that circularity silently breaks every sink, not just this one.
                .WriteTo.Sink(new TelemetryLogEventSink(new DeferredTelemetryPublisher(services))))
            .ConfigureServices((hostContext, services) =>
            {
                services.AddTotallyHotArcRouter();

                // Local Proxy CLI single-model override: registered even when null (normal multi-model
                // routing) so RequestInterceptor's optional constructor parameter always has an explicit
                // value to reason about rather than depending on DI's "unregistered optional service"
                // fallback.
                services.AddSingleton(new SingleModelServingOptions { ForcedModelName = forcedModelName });
            });
    }

    /// <summary>
    /// Extracts the Local Proxy CLI's <c>--model &lt;name&gt;</c> flag from
    /// <paramref name="args"/> (mirroring LiteLLM's <c>litellm --model provider/name</c> single-model
    /// serving invocation), returning the forced model name (if present) and the remaining arguments
    /// with the flag and its value removed. Stripping it out matters: <see cref="Host.CreateDefaultBuilder(string[])"/>
    /// adds a command-line configuration provider that treats both <c>--model value</c> and
    /// <c>--model=value</c> (both handled here) as configuration input, which would otherwise bind an
    /// unstripped occurrence to a top-level <c>"model"</c> configuration key nothing else expects.
    /// </summary>
    /// <exception cref="ArgumentException"><c>--model</c> is present without a non-empty value.</exception>
    private static (string? ForcedModelName, string[] RemainingArgs) ExtractModelArg(string[] args)
    {
        const string errorMessage = "--model requires a non-empty value (e.g. --model gpt-5.4 or --model=gpt-5.4).";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--model=", StringComparison.OrdinalIgnoreCase))
            {
                var forcedModelName = arg["--model=".Length..];
                if (string.IsNullOrWhiteSpace(forcedModelName))
                {
                    throw new ArgumentException(errorMessage, nameof(args));
                }

                return (forcedModelName, RemoveAt(args, i));
            }

            if (string.Equals(arg, "--model", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException(errorMessage, nameof(args));
                }

                return (args[i + 1], RemoveAt(RemoveAt(args, i), i));
            }
        }

        return (null, args);
    }

    /// <summary>
    /// Returns a copy of <paramref name="args"/> with the element at <paramref name="index"/> removed.
    /// </summary>
    private static string[] RemoveAt(string[] args, int index) =>
        args.Where((_, i) => i != index).ToArray();

    /// <summary>
    /// Extracts a bare boolean flag (no value) from <paramref name="args"/>, returning whether it was
    /// present and the remaining arguments with it removed. Mirrors <see cref="ExtractModelArg"/>'s
    /// stripping behavior for the same reason: an unstripped flag would otherwise bind to a stray
    /// top-level configuration key via <see cref="Host.CreateDefaultBuilder(string[])"/>'s command-line
    /// provider. Internal (not private) so <c>ProgramTests</c> can exercise the parsing directly - unlike
    /// <see cref="ExtractModelArg"/>, this can't be covered indirectly through <see cref="CreateHostBuilder"/>,
    /// since <see cref="Main"/> strips the flag before <see cref="CreateHostBuilder"/> ever sees it.
    /// </summary>
    internal static (bool Present, string[] RemainingArgs) ExtractFlag(string[] args, string flagName)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, flagName, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? (false, args) : (true, RemoveAt(args, index));
    }

    /// <summary>
    /// Runs one CodeRouterBench corpus sync to completion and logs a per-file summary, replacing
    /// <c>scripts/fetch-coderouterbench.sh</c> for headless and CI machines
    /// (docs/router/coderouterbench-sqlite-migration-plan.md Phase 6). Does not start Kestrel or any
    /// hosted service - <paramref name="services"/> is resolved directly from the built (but not started)
    /// host, and the process exits once the sync completes. Sets a non-zero
    /// <see cref="Environment.ExitCode"/> when any file failed, so a CI script can detect it.
    /// </summary>
    private static async Task RunBenchmarkDataSyncAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<BenchmarkSyncService>>();
        var database = services.GetRequiredService<BenchmarkDatabase>();
        database.EnsureCreated();

        var syncService = services.GetRequiredService<BenchmarkSyncService>();
        var datasetRef = services.GetRequiredService<IOptions<BenchmarkSyncOptions>>().Value.DatasetRef;

        var result = await syncService.SyncAsync(datasetRef, progress: null, CancellationToken.None);

        var failed = result.Files.Where(file => !file.Succeeded).ToList();
        foreach (var file in result.Files)
        {
            if (file.Succeeded)
            {
                logger.LogInformation("Synced {FileName}: {RowCount} row(s).", file.FileName, file.RowCount);
            }
            else
            {
                logger.LogError("Failed to sync {FileName}: {Reason}", file.FileName, file.ErrorMessage);
            }
        }

        if (failed.Count > 0)
        {
            logger.LogError(
                "CodeRouterBench sync completed with {FailedCount} of {TotalCount} file(s) failed at commit {RepoCommit}.",
                failed.Count, result.Files.Count, result.RepoCommit);
            Environment.ExitCode = 1;
        }
        else
        {
            logger.LogInformation(
                "CodeRouterBench sync completed: {FileCount} file(s) synced at commit {RepoCommit}.",
                result.Files.Count, result.RepoCommit);
        }
    }
}


