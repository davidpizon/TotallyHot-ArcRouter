using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            var host = CreateHostBuilder(args).Build();
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
}


