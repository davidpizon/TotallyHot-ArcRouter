using Microsoft.Extensions.Options;
using Serilog;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Telemetry;

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
            var (runBenchmarkDataSync, afterBenchmarkFlag) = ExtractFlag(args: args, flagName: "--sync-benchmark-data");

            // docs/router/live-feedback-learning-plan.md Phase 4c: headless retrain trigger, stripped for
            // the same reason --sync-benchmark-data is - it must never reach the command-line configuration
            // provider as a stray "retrain-logreg" key.
            var (runLogRegRetrain, afterLogRegFlag) =
                ExtractFlag(args: afterBenchmarkFlag, flagName: "--retrain-logreg");

            // docs/router/self-organizing-classification-plan.md Phase T2g: the cluster model's own
            // headless retrain trigger, stripped for the same reason.
            var (runClusterRetrain, afterClusterFlag) =
                ExtractFlag(args: afterLogRegFlag, flagName: "--retrain-clusters");

            // docs/router/regret-evaluation-harness-plan.md N6: headless regret-harness run trigger,
            // stripped for the same reason - it must never reach the command-line configuration provider
            // as a stray "run-regret-harness" key.
            var (runRegretHarness, afterRegretHarnessFlag) =
                ExtractFlag(args: afterClusterFlag, flagName: "--run-regret-harness");

            // docs/router/grader-reliability-plan.md Phase Q4: headless grader-reliability report trigger,
            // stripped for the same reason - it must never reach the command-line configuration provider
            // as a stray "run-grader-reliability-report" key.
            var (runGraderReliabilityReport, afterGraderReliabilityFlag) =
                ExtractFlag(args: afterRegretHarnessFlag, flagName: "--run-grader-reliability-report");

            // docs/router/geval-shadow-scoring-plan.md Phase G2: headless judge-calibration report
            // trigger, stripped for the same reason - it must never reach the command-line configuration
            // provider as a stray "run-judge-calibration-report" key.
            var (runJudgeCalibrationReport, remainingArgs) =
                ExtractFlag(args: afterGraderReliabilityFlag, flagName: "--run-judge-calibration-report");

            // `using` (not a bare local) so the sync/retrain paths below, which return without ever calling
            // RunAsync, still dispose the container and everything singleton-scoped in it - SQLite
            // connections and HttpClient handlers among them - rather than leaving that to process exit.
            using var host = CreateHostBuilder(remainingArgs).Build();

            if (runBenchmarkDataSync)
            {
                await RunBenchmarkDataSyncAsync(host.Services);
                return;
            }

            if (runLogRegRetrain)
            {
                await RunLogRegRetrainAsync(host.Services);
                return;
            }

            if (runClusterRetrain)
            {
                await RunClusterRetrainAsync(host.Services);
                return;
            }

            if (runRegretHarness)
            {
                await RunRegretHarnessAsync(host.Services);
                return;
            }

            if (runGraderReliabilityReport)
            {
                await RunGraderReliabilityReportAsync(host.Services);
                return;
            }

            if (runJudgeCalibrationReport)
            {
                await RunJudgeCalibrationReportAsync(host.Services);
                return;
            }

            Log.Information("TotallyHot.ArcRouter host created.");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(exception: ex, messageTemplate: "TotallyHot.ArcRouter host terminated unexpectedly.");

            // Without this the process exits 0 after a fatal error, reporting success to whatever launched
            // it. That matters most on the --sync-benchmark-data path, whose own partial-failure branch
            // already sets this: a CI script checking the exit code would otherwise see an exception-killed
            // sync as a clean one. Assigned rather than returned so the finally below still flushes the log.
            Environment.ExitCode = 1;
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
            // No-op outside the Windows Service Control Manager, so `dotnet run`/console debugging
            // and every other CreateHostBuilder test are unaffected - see the auto-update plan's
            // Phase 1 for the Install-RouterService.ps1 script that registers this service name.
            .UseWindowsService(options => options.ServiceName = "TotallyHotArcRouter")
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
            .ConfigureServices((_, services) =>
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

            if (arg.StartsWith(value: "--model=", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                var forcedModelName = arg["--model=".Length..];
                if (string.IsNullOrWhiteSpace(forcedModelName))
                    throw new ArgumentException(message: errorMessage, paramName: nameof(args));

                return (forcedModelName, RemoveAt(args: args, index: i));
            }

            if (string.Equals(a: arg, b: "--model", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException(message: errorMessage, paramName: nameof(args));

                return (args[i + 1], RemoveAt(args: RemoveAt(args: args, index: i), index: i));
            }
        }

        return (null, args);
    }

    /// <summary>
    /// Returns a copy of <paramref name="args"/> with the element at <paramref name="index"/> removed.
    /// </summary>
    private static string[] RemoveAt(string[] args, int index)
    {
        return [.. args.Where((_, i) => i != index)];
    }

    /// <summary>
    /// Extracts a bare boolean flag (no value) from <paramref name="args"/>, returning whether it was
    /// present and the remaining arguments with every occurrence removed. Mirrors <see cref="ExtractModelArg"/>'s
    /// stripping behavior for the same reason: an unstripped flag would otherwise bind to a stray
    /// top-level configuration key via <see cref="Host.CreateDefaultBuilder(string[])"/>'s command-line
    /// provider. All occurrences are stripped, not just the first, so a repeated flag can't leave a
    /// leftover copy for that provider to bind. Internal (not private) so <c>ProgramTests</c> can exercise
    /// the parsing directly - unlike <see cref="ExtractModelArg"/>, this can't be covered indirectly through
    /// <see cref="CreateHostBuilder"/>, since <see cref="Main"/> strips the flag before
    /// <see cref="CreateHostBuilder"/> ever sees it.
    /// </summary>
    internal static (bool Present, string[] RemainingArgs) ExtractFlag(string[] args, string flagName)
    {
        var present = args.Any(arg =>
            string.Equals(a: arg, b: flagName, comparisonType: StringComparison.OrdinalIgnoreCase));
        if (!present) return (false, args);

        var remaining = args.Where(arg =>
            !string.Equals(a: arg, b: flagName, comparisonType: StringComparison.OrdinalIgnoreCase)).ToArray();
        return (true, remaining);
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

        var result =
            await syncService.SyncAsync(datasetRef: datasetRef, null, cancellationToken: CancellationToken.None);

        var failed = result.Files.Where(file => !file.Succeeded).ToList();
        foreach (var file in result.Files)
            if (file.Succeeded)
                logger.LogInformation(message: "Synced {FileName}: {RowCount} row(s).", file.FileName, file.RowCount);
            else
                logger.LogError(message: "Failed to sync {FileName}: {Reason}", file.FileName, file.ErrorMessage);

        if (failed.Count > 0)
        {
            logger.LogError(
                message:
                "CodeRouterBench sync completed with {FailedCount} of {TotalCount} file(s) failed at commit {RepoCommit}.",
                failed.Count, result.Files.Count, result.RepoCommit);
            Environment.ExitCode = 1;
        }
        else
        {
            logger.LogInformation(
                message: "CodeRouterBench sync completed: {FileCount} file(s) synced at commit {RepoCommit}.",
                result.Files.Count, result.RepoCommit);
        }
    }

    /// <summary>
    /// Runs one <c>logreg</c> voter retrain to completion (docs/router/live-feedback-learning-plan.md
    /// Phase 4c), following <see cref="RunBenchmarkDataSyncAsync"/>'s headless-CLI shape: resolved directly
    /// from the built (but not started) host, no Kestrel, process exits once the retrain completes. Sets a
    /// non-zero <see cref="Environment.ExitCode"/> when the retrain declined for lack of data, so a CI or
    /// scheduled-task caller can detect it.
    /// </summary>
    private static async Task RunLogRegRetrainAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<IEmbeddingLogRegTrainingService>>();
        var trainingService = services.GetRequiredService<IEmbeddingLogRegTrainingService>();

        var progress = new Progress<int>(taskCount =>
            logger.LogInformation(message: "logreg retrain: embedded {TaskCount} OOD bootstrap task(s) so far.",
                taskCount));

        var outcome =
            await trainingService.RetrainAsync(bootstrapProgress: progress, cancellationToken: CancellationToken.None);

        logger.LogInformation(message: "logreg retrain finished with outcome {Kind}: {Message}", outcome.Kind,
            outcome.Message);

        if (outcome.Kind != LogRegTrainingResultKind.Trained) Environment.ExitCode = 1;
    }

    /// <summary>
    /// Runs one cluster model retrain to completion (docs/router/self-organizing-classification-plan.md
    /// Phase T2g), following <see cref="RunLogRegRetrainAsync"/>'s headless-CLI shape: resolved directly
    /// from the built (but not started) host, no Kestrel, process exits once the retrain completes. Sets a
    /// non-zero <see cref="Environment.ExitCode"/> when the retrain declined for lack of data, so a CI or
    /// scheduled-task caller can detect it.
    /// </summary>
    private static async Task RunClusterRetrainAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<IClusterTrainingService>>();
        var trainingService = services.GetRequiredService<IClusterTrainingService>();

        var progress = new Progress<int>(taskCount =>
            logger.LogInformation(message: "Cluster model retrain: embedded {TaskCount} OOD bootstrap task(s) so far.",
                taskCount));

        var outcome =
            await trainingService.RetrainAsync(bootstrapProgress: progress, cancellationToken: CancellationToken.None);

        logger.LogInformation(message: "Cluster model retrain finished with outcome {Kind}: {Message}", outcome.Kind,
            outcome.Message);

        if (outcome.Kind != ClusterTrainingResultKind.Trained) Environment.ExitCode = 1;
    }

    /// <summary>
    /// Runs one regret-evaluation harness pass to completion (docs/router/regret-evaluation-harness-plan.md
    /// N6), following <see cref="RunClusterRetrainAsync"/>'s headless-CLI shape: resolved directly from the
    /// built (but not started) host, no Kestrel, process exits once the run completes. Sets a non-zero
    /// <see cref="Environment.ExitCode"/> when the run declined or was already running, so a CI or
    /// scheduled-task caller can detect it. Unlike the retrains above, a completed run has nothing to hot-swap
    /// - both split reports are written straight to the console for a human to read or copy into the plan
    /// doc's changelog, mirroring <c>N5ComparisonReportReconciliationTests</c>'s own publish convention.
    /// </summary>
    private static async Task RunRegretHarnessAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<IRegretHarnessRunner>>();
        var runner = services.GetRequiredService<IRegretHarnessRunner>();

        var progress = new Progress<RegretHarnessStage>(stage =>
            logger.LogInformation(message: "Regret harness run: entering stage {Stage}.", stage));

        var outcome = await runner.RunAsync(stageProgress: progress, cancellationToken: CancellationToken.None);

        logger.LogInformation(message: "Regret harness run finished with outcome {Kind}: {Message}", outcome.Kind,
            outcome.Message);

        if (outcome.Kind != RegretHarnessRunResultKind.Completed)
        {
            Environment.ExitCode = 1;
            return;
        }

        foreach (var split in outcome.Splits)
        {
            Console.WriteLine(split.MarkdownTable);
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Runs one grader-reliability report pass (docs/router/grader-reliability-plan.md, Phase Q4),
    /// following <see cref="RunRegretHarnessAsync"/>'s headless-CLI shape: resolved directly from the built
    /// (but not started) host, no Kestrel, process exits once the report is printed. Purely a read over
    /// already-collected <c>grader_scores</c> rows - no live API call, no weight ever changes.
    /// </summary>
    private static async Task RunGraderReliabilityReportAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<IGraderReliabilityAnalyzer>>();
        var analyzer = services.GetRequiredService<IGraderReliabilityAnalyzer>();

        var report = await analyzer.AnalyzeAsync(CancellationToken.None);

        logger.LogInformation(
            message: "Grader reliability report generated at {GeneratedAtUtc} from {TotalRows} grader_scores row(s).",
            report.GeneratedAtUtc,
            report.TotalRowsAnalyzed);

        foreach (var dimension in report.Dimensions)
        {
            Console.WriteLine($"## {dimension.Dimension}");
            Console.WriteLine();

            Console.WriteLine("### Inter-grader agreement (Spearman)");
            Console.WriteLine("| Grader A | Grader B | Correlation | N |");
            Console.WriteLine("|---|---|---|---|");
            foreach (var pair in dimension.PairAgreements)
                Console.WriteLine(
                    $"| {pair.GraderA} | {pair.GraderB} | {FormatNullableCorrelation(pair.Correlation)} | {pair.SampleSize} |");
            Console.WriteLine();

            Console.WriteLine("### Verbosity skew (score vs. response length, Spearman)");
            Console.WriteLine("| Grader | Correlation | N |");
            Console.WriteLine("|---|---|---|");
            foreach (var skew in dimension.VerbositySkews)
                Console.WriteLine($"| {skew.GraderKey} | {FormatNullableCorrelation(skew.Correlation)} | {skew.SampleSize} |");
            Console.WriteLine();

            Console.WriteLine("### Self-preference skew (own backbone vs. other candidates)");
            Console.WriteLine("| Grader | Mean score delta | N (own) | N (other) |");
            Console.WriteLine("|---|---|---|---|");
            foreach (var skew in dimension.SelfPreferenceSkews)
                Console.WriteLine(
                    $"| {skew.GraderKey} | {(skew.MeanScoreDelta is { } delta ? delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "undefined")} | {skew.OwnModelSampleSize} | {skew.OtherModelSampleSize} |");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Runs the Phase G2 judge-calibration report and writes it to stdout as Markdown
    /// (docs/router/geval-shadow-scoring-plan.md Phase G2). Read-only: it reads accumulated
    /// judge_shadow_scores rows and computes statistics, never touching the judge's blend weight, a
    /// voter, or the live path.
    /// </summary>
    /// <param name="services">The built host's service provider.</param>
    private static async Task RunJudgeCalibrationReportAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<IJudgeCalibrationAnalyzer>>();
        var analyzer = services.GetRequiredService<IJudgeCalibrationAnalyzer>();

        var report = await analyzer.AnalyzeAsync(CancellationToken.None);

        logger.LogInformation(
            message: "Judge calibration report generated at {GeneratedAtUtc} from {TotalRows} judge_shadow_scores row(s).",
            report.GeneratedAtUtc,
            report.TotalRowsAnalyzed);

        Console.Write(JudgeCalibrationReportFormatter.FormatMarkdown(report));
    }

    /// <summary>Formats a nullable correlation for console output, naming a suppressed value rather than showing a blank.</summary>
    private static string FormatNullableCorrelation(double? correlation)
    {
        return correlation is { } value
            ? value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            : "suppressed (N too small)";
    }
}