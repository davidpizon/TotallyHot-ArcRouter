using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;
using TotallyHot.ArcRouter.Tools;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Registers the routing core with the DI container. Split out of
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that adding a
/// dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class RouterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the routing core: learned memory storage, <see cref="RoutingOptions"/>/
    /// <see cref="EmbeddingOptions"/>/<see cref="LlmRouterOptions"/> and their live-override layers, the
    /// embedding and local text-generation clients, the Orchestrator voter ensemble, and the retrain
    /// hosted-service triggers' training services - plus <see cref="CheckSyntax"/>, the one remaining
    /// tool.
    /// </summary>
    internal static IServiceCollection AddRouterCore(this IServiceCollection services)
    {
        // Core Router. IRouterMemoryStore is backed by RouterMemoryDatabase (registered below with the
        // Phase J embedding memory that shares the same file), so both learned-memory tables live in one
        // WAL-journaled SQLite database rather than the crash-unsafe JSON file this replaced.
        services.AddSingleton<IRouterMemoryStore, SqliteRouterMemoryStore>();
        services.AddSingleton<RouterMemory>();
        services.AddSingleton<AgentAsARouter>();

        // PLAN.md Phase J: task-embedding-keyed memory. RouterMemoryDatabase owns a SQLite file
        // separate from the price catalog's agent_telemetry.db (its own lifecycle/locking);
        // StartupHealthCheckHostedService creates the schema and loads EmbeddingMemory's working set
        // before Kestrel binds, mirroring the price-catalog startup checks.
        services.AddOptions<EmbeddingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(EmbeddingOptions.SectionName).Bind(options));
        // EnsureValid() is enforced here (rather than by a consuming component's constructor, this
        // options type's usual pattern - see e.g. CircuitBreaker's) because RoutingOptions is read
        // piecemeal by several singletons (AgentAsARouter, RouterMemoryDatabase, RequestInterceptor),
        // none of which is guaranteed to be constructed eagerly; ValidateOnStart guarantees the check
        // runs during host startup regardless of which of those paths is actually exercised.
        // ValidateDataAnnotations() enforces the [Range]/[Required] attributes on individual properties
        // (e.g. EmbeddingBudgetMs) that EnsureValid's hand-written checks don't cover - the two are
        // complementary, not redundant: EnsureValid checks cross-property invariants annotations can't
        // express, ValidateDataAnnotations checks the per-property bounds EnsureValid doesn't repeat.
        services.AddOptions<RoutingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(RoutingOptions.SectionName).Bind(options))
            .ValidateDataAnnotations()
            .Validate(options =>
            {
                options.EnsureValid();
                return true;
            })
            .ValidateOnStart();

        // docs/router/self-organizing-classification-plan.md Phase T6: the SQLite-backed override
        // layer, registered as an IConfigureOptions<RoutingOptions> step *after* the appsettings.json
        // bind above - Options-pattern configure delegates run in registration order, so this one runs
        // second and wins, giving "stored override > appsettings.json > coded default" precedence.
        // RouterSettingsStore is deliberately built from a private RouterMemoryDatabase resolved
        // straight from configuration rather than the DI singleton below (which itself needs
        // IOptions<RoutingOptions>) - see RouterSettingsStore's remarks for why the DI singleton would
        // be circular here.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var configuredPath =
                configuration.GetSection(RoutingOptions.SectionName)[
                    nameof(RoutingOptions.EmbeddingMemoryDatabasePath)];
            var databaseOptions = Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = configuredPath ?? new RoutingOptions().EmbeddingMemoryDatabasePath
            });
            return new RouterSettingsStore(database: new RouterMemoryDatabase(databaseOptions),
                logger: sp.GetRequiredService<ILogger<RouterSettingsStore>>());
        });
        services.AddSingleton<RouterSettingsReloadToken>();
        services.AddSingleton<IOptionsChangeTokenSource<RoutingOptions>>(sp =>
            sp.GetRequiredService<RouterSettingsReloadToken>());
        services.AddSingleton<IConfigureOptions<RoutingOptions>, RouterSettingsConfigureOptions>();

        services.AddHttpClient(nameof(OnnxEmbeddingClient));
        services.AddSingleton<IEmbeddingClient, OnnxEmbeddingClient>();
        services.AddSingleton<EmbeddingWarmupState>();
        services.AddSingleton<RouterMemoryDatabase>();
        services.AddSingleton<IMemoryEntryStore, SqliteMemoryEntryStore>();
        services.AddSingleton<EmbeddingMemory>();

        // llm_router voter's local ONNX GenAI text-generation model (PLAN.md Phase L) - same
        // download-once-cache-forever shape as EmbeddingOptions/OnnxEmbeddingClient above.
        services.AddOptions<LlmRouterOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(LlmRouterOptions.SectionName).Bind(options))
            .ValidateDataAnnotations()
            .Validate(options =>
            {
                options.EnsureValid();
                return true;
            })
            .ValidateOnStart();
        services.AddHttpClient(nameof(OnnxTextGenerationClient));
        services.AddSingleton<ITextGenerationClient, OnnxTextGenerationClient>();

        // The Governance > Benchmark Data panel's "Local Voter Model" section: lets the operator
        // switch llm_router's active model by URL and proactively (re-)sync its files, instead of
        // only the lazy first-use download OnnxTextGenerationClient itself falls back to. Registered
        // here, right after OnnxTextGenerationClient, purely for readability - DI resolution order is
        // independent of registration order - because the seed-validation failure this store can
        // throw belongs conceptually with the LlmRouterOptions block it seeds from.
        services.AddOptions<LlmRouterModelOverrideStoreOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(LlmRouterModelOverrideStoreOptions.SectionName).Bind(options));
        services.AddSingleton<ILlmRouterModelOverrideStore, LlmRouterModelOverrideStore>();
        services.AddHttpClient(LlmRouterModelChecksumProbe.HttpClientName);
        services.AddSingleton<LlmRouterModelChecksumProbe>();
        services.AddSingleton<LlmRouterModelSyncService>();

        // PLAN.md Phase L: the Orchestrator ensemble. Registered by concrete type - CompositeRoutingPolicy
        // (still the registered IRoutingPolicy) takes it as a direct constructor dependency and, per
        // PLAN.md Phase M / docs/router/orchestrator-live-path-plan.md, dispatches every non-utility
        // decision to it by default (RoutingOptions.EnableOrchestratorPolicy is the kill switch back to
        // AgentRouterPolicy). BenchmarkDatabase is registered later in this method - safe, DI resolution
        // order is independent of registration order. Voters are registered by concrete type (so tests/
        // other consumers can depend on one directly) and again as IRoutingVoter (so
        // OrchestratorRoutingPolicy's IEnumerable<IRoutingVoter> constructor parameter resolves every one
        // of them).
        services.AddSingleton<DimBestVoter>();
        services.AddSingleton<MemoryKnnVoter>();
        services.AddSingleton<LogRegVoter>();
        services.AddSingleton<LlmRouterVoter>();
        services.AddSingleton<ClusterBestVoter>();
        services.AddSingleton<IRoutingVoter>(sp => sp.GetRequiredService<DimBestVoter>());
        services.AddSingleton<IRoutingVoter>(sp => sp.GetRequiredService<MemoryKnnVoter>());
        services.AddSingleton<IRoutingVoter>(sp => sp.GetRequiredService<LogRegVoter>());
        services.AddSingleton<IRoutingVoter>(sp => sp.GetRequiredService<LlmRouterVoter>());
        services.AddSingleton<IRoutingVoter>(sp => sp.GetRequiredService<ClusterBestVoter>());
        services.AddSingleton<OrchestratorRoutingPolicy>();

        // docs/router/live-feedback-learning-plan.md Phase 4: trains and hot-swaps the logreg voter's
        // artifact. LogRegRetrainHostedService (registered below, with the other hosted services) is
        // the automatic-threshold trigger; Program.cs's --retrain-logreg flag and Phase 5's Governance
        // button both resolve IEmbeddingLogRegTrainingService directly instead of going through it.
        services.AddSingleton<OodBootstrapSampleSource>();
        services.AddSingleton<IEmbeddingLogRegTrainingService, EmbeddingLogRegTrainingService>();

        // docs/router/self-organizing-classification-plan.md Phase T2: trains and atomically writes the
        // self-organizing cluster model's artifact. ClusterRetrainHostedService (registered below, with
        // the other hosted services) is the automatic-threshold trigger; Program.cs's --retrain-clusters
        // flag and Phase T5's Governance button both resolve IClusterTrainingService directly instead of
        // going through it.
        services.AddSingleton<OodClusterBootstrapSampleSource>();
        services.AddSingleton<IClusterTrainingService, ClusterTrainingService>();

        // docs/router/regret-evaluation-harness-plan.md N6: re-runs the N5 comparison report on demand.
        // Program.cs's --run-regret-harness flag and the Governance UI's Regret Harness panel both
        // resolve IRegretHarnessRunner directly - read-only and informational, so there is no hosted
        // automatic trigger the way the retrains above have one.
        services.AddSingleton<IRegretHarnessRunner, RegretHarnessRunner>();

        // Tools. RunVisibleTests (which shelled out to `dotnet test` in a caller-supplied directory) and
        // EstimateQuality (a placeholder length-and-comment heuristic) were removed along with the
        // executing verifier: the first was a live path to running code we do not run, and the second
        // was a competing quality API that the real static analyzers in TotallyHot.ArcRouter.Quality
        // supersede outright.
        services.AddTransient<CheckSyntax>();

        return services;
    }
}