using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Quality.DependencyInjection;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tools;
using TotallyHot.ArcRouter.Update;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Hosting
{
    /// <summary>
    /// Extension methods for setting up agentic router services in an <see cref="IServiceCollection" />.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the core services for the agentic router to the specified <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection" /> to add the services to.</param>
        /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        public static IServiceCollection AddTotallyHotArcRouter(this IServiceCollection services)
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
                var configuredPath = configuration.GetSection(RoutingOptions.SectionName)[nameof(RoutingOptions.EmbeddingMemoryDatabasePath)];
                var databaseOptions = Options.Create(new RoutingOptions
                {
                    EmbeddingMemoryDatabasePath = configuredPath ?? new RoutingOptions().EmbeddingMemoryDatabasePath,
                });
                return new RouterSettingsStore(new RouterMemoryDatabase(databaseOptions), sp.GetRequiredService<ILogger<RouterSettingsStore>>());
            });
            services.AddSingleton<Router.RouterSettingsReloadToken>();
            services.AddSingleton<IOptionsChangeTokenSource<RoutingOptions>>(sp => sp.GetRequiredService<Router.RouterSettingsReloadToken>());
            services.AddSingleton<IConfigureOptions<RoutingOptions>, Router.RouterSettingsConfigureOptions>();

            services.AddHttpClient(nameof(Router.Embeddings.OnnxEmbeddingClient));
            services.AddSingleton<Router.Embeddings.IEmbeddingClient, Router.Embeddings.OnnxEmbeddingClient>();
            services.AddSingleton<Router.Embeddings.EmbeddingWarmupState>();
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
            services.AddHttpClient(nameof(Router.TextGeneration.OnnxTextGenerationClient));
            services.AddSingleton<Router.TextGeneration.ITextGenerationClient, Router.TextGeneration.OnnxTextGenerationClient>();

            // The Governance > Benchmark Data panel's "Local Voter Model" section: lets the operator
            // switch llm_router's active model by URL and proactively (re-)sync its files, instead of
            // only the lazy first-use download OnnxTextGenerationClient itself falls back to. Registered
            // here, right after OnnxTextGenerationClient, purely for readability - DI resolution order is
            // independent of registration order - because the seed-validation failure this store can
            // throw belongs conceptually with the LlmRouterOptions block it seeds from.
            services.AddOptions<Router.TextGeneration.LlmRouterModelOverrideStoreOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(Router.TextGeneration.LlmRouterModelOverrideStoreOptions.SectionName).Bind(options));
            services.AddSingleton<Router.TextGeneration.ILlmRouterModelOverrideStore, Router.TextGeneration.LlmRouterModelOverrideStore>();
            services.AddHttpClient(Router.TextGeneration.LlmRouterModelChecksumProbe.HttpClientName);
            services.AddSingleton<Router.TextGeneration.LlmRouterModelChecksumProbe>();
            services.AddSingleton<Router.TextGeneration.LlmRouterModelSyncService>();

            // PLAN.md Phase L: the Orchestrator ensemble. Registered by concrete type - CompositeRoutingPolicy
            // (still the registered IRoutingPolicy) takes it as a direct constructor dependency and, per
            // PLAN.md Phase M / docs/router/orchestrator-live-path-plan.md, dispatches every non-utility
            // decision to it by default (RoutingOptions.EnableOrchestratorPolicy is the kill switch back to
            // AgentRouterPolicy). BenchmarkDatabase is registered later in this method - safe, DI resolution
            // order is independent of registration order. Voters are registered by concrete type (so tests/
            // other consumers can depend on one directly) and again as IRoutingVoter (so
            // OrchestratorRoutingPolicy's IEnumerable<IRoutingVoter> constructor parameter resolves every one
            // of them).
            services.AddSingleton<Router.Orchestrator.DimBestVoter>();
            services.AddSingleton<Router.Orchestrator.MemoryKnnVoter>();
            services.AddSingleton<Router.Orchestrator.LogRegVoter>();
            services.AddSingleton<Router.Orchestrator.LlmRouterVoter>();
            services.AddSingleton<Router.Orchestrator.ClusterBestVoter>();
            services.AddSingleton<Router.Orchestrator.IRoutingVoter>(sp => sp.GetRequiredService<Router.Orchestrator.DimBestVoter>());
            services.AddSingleton<Router.Orchestrator.IRoutingVoter>(sp => sp.GetRequiredService<Router.Orchestrator.MemoryKnnVoter>());
            services.AddSingleton<Router.Orchestrator.IRoutingVoter>(sp => sp.GetRequiredService<Router.Orchestrator.LogRegVoter>());
            services.AddSingleton<Router.Orchestrator.IRoutingVoter>(sp => sp.GetRequiredService<Router.Orchestrator.LlmRouterVoter>());
            services.AddSingleton<Router.Orchestrator.IRoutingVoter>(sp => sp.GetRequiredService<Router.Orchestrator.ClusterBestVoter>());
            services.AddSingleton<Router.Orchestrator.OrchestratorRoutingPolicy>();

            // docs/router/live-feedback-learning-plan.md Phase 4: trains and hot-swaps the logreg voter's
            // artifact. LogRegRetrainHostedService (registered below, with the other hosted services) is
            // the automatic-threshold trigger; Program.cs's --retrain-logreg flag and Phase 5's Governance
            // button both resolve IEmbeddingLogRegTrainingService directly instead of going through it.
            services.AddSingleton<Router.Orchestrator.OodBootstrapSampleSource>();
            services.AddSingleton<Router.Orchestrator.IEmbeddingLogRegTrainingService, Router.Orchestrator.EmbeddingLogRegTrainingService>();

            // docs/router/self-organizing-classification-plan.md Phase T2: trains and atomically writes the
            // self-organizing cluster model's artifact. ClusterRetrainHostedService (registered below, with
            // the other hosted services) is the automatic-threshold trigger; Program.cs's --retrain-clusters
            // flag and Phase T5's Governance button both resolve IClusterTrainingService directly instead of
            // going through it.
            services.AddSingleton<Router.Orchestrator.OodClusterBootstrapSampleSource>();
            services.AddSingleton<Router.Orchestrator.IClusterTrainingService, Router.Orchestrator.ClusterTrainingService>();

            // Tools. RunVisibleTests (which shelled out to `dotnet test` in a caller-supplied directory) and
            // EstimateQuality (a placeholder length-and-comment heuristic) were removed along with the
            // executing verifier: the first was a live path to running code we do not run, and the second
            // was a competing quality API that the real static analyzers in TotallyHot.ArcRouter.Quality
            // supersede outright.
            services.AddTransient<CheckSyntax>();

            // Proxy
            services.AddOptions<ModelRoutingOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(ModelRoutingOptions.SectionName).Bind(options));
            // Writable, live-reloadable provider/model configuration (see ProviderConfigStore). Seeded
            // from the appsettings-bound ModelRoutingOptions above on first run; becomes the source of
            // truth once edited via the management API. ModelRouteResolver reads its snapshots.
            services.AddOptions<ProviderConfigStoreOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(ProviderConfigStoreOptions.SectionName).Bind(options));
            // The generic protected secret store (docs/router/secrets-at-rest-plan.md §3): one DPAPI-
            // encrypted file backing both the resolution surface (ISecretReader, injected into
            // ProviderConfigStore's migration, ModelRouteResolver's request path, and ManagementFacade's
            // model-discovery probe) and the management surface (ISecretWriter, injected into
            // ManagementFacade's write path only - it never receives a reader). Registered before
            // IProviderConfigStore so the container can inject it into ProviderConfigStore's optional
            // migration parameter.
            services.AddSingleton<ProtectedSecretStore>();
            services.AddSingleton<ISecretReader>(sp => sp.GetRequiredService<ProtectedSecretStore>());
            services.AddSingleton<ISecretWriter>(sp => sp.GetRequiredService<ProtectedSecretStore>());
            services.AddSingleton<IProviderConfigStore, ProviderConfigStore>();
            services.AddSingleton<IEnvironmentVariableProvider, EnvironmentVariableProvider>();
            services.AddSingleton<IModelRouteResolver, ModelRouteResolver>();

            // Circuit breaker (docs/router/agent-resilience-strategies.md): registered as a singleton so
            // RequestInterceptor (candidate ranking/substitution) and ProxyMiddleware (recording
            // successes/failures after real attempts) share the exact same per-target state.
            services.AddOptions<CircuitBreakerOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(CircuitBreakerOptions.SectionName).Bind(options));
            services.AddSingleton<ICircuitBreaker, CircuitBreaker>();

            // PLAN.md Phase H (the Context leg): classifies a request ahead of routing. Registered so
            // later consumers (Phase I's IRoutingPolicy) can take it as a normal DI dependency; not
            // registering IDimensionInferrer itself is deliberate - RequestInterceptor's own default
            // (a fresh KeywordDimensionInferrer) is what HeuristicRequestClassifier's default also
            // resolves to, keeping the two in lockstep without a shared registration to keep in sync.
            services.AddSingleton<IRequestClassifier, HeuristicRequestClassifier>();

            // PLAN.md Phase I (the Action leg, docs/router/utility-model-routing.md §B3-B4): selection-only
            // routing for the auto/unresolved-model paths. UtilityRoutingPolicy takes IModelPriceCatalog
            // (registered later in this method - DI resolution order is independent of registration order,
            // so this is safe). The three leaf policies (UtilityRoutingPolicy, AgentRouterPolicy, and Phase
            // L's OrchestratorRoutingPolicy above) are registered by concrete type so CompositeRoutingPolicy's
            // constructor can resolve all three directly, with only the composite exposed as IRoutingPolicy.
            services.AddSingleton<UtilityRoutingPolicy>();
            services.AddSingleton<AgentRouterPolicy>();
            services.AddSingleton<IRoutingPolicy, CompositeRoutingPolicy>();
            services.AddSingleton<RequestInterceptor>();

            // Telemetry (live routing events broadcast to GUI dashboards over gRPC - see
            // docs/router/telemetry.md, docs/router/grpc-migration.md, and
            // Telemetry/TelemetryBroadcaster.cs for the outer/inner DI-container bridging this
            // singleton is responsible for).
            services.AddSingleton<ISessionIdResolver, SessionIdResolver>();
            services.AddSingleton<IConversationContinuityMatcher, MessageHistoryContinuityMatcher>();
            // Persistent (ledger-seeded) turn tracker (docs/router/token-tracking-implementation-plan.md
            // Phase 2, §5.5) replaces the process-lifetime-only ConversationTurnTracker as the app's default:
            // a session's turn number now survives a proxy restart. ConversationTurnTracker itself remains in
            // the codebase for tests and any no-ledger direct construction of ProxyMiddleware.
            services.AddSingleton<IConversationTurnTracker, PersistentConversationTurnTracker>();
            services.AddSingleton<IUsageExtractor, UsageExtractor>();
            services.AddSingleton<IResponseTextExtractor, ResponseTextExtractor>();

            // Per-provider payload translators (see docs/router/unified-api-translation.md). Each
            // provider whose native API shape differs from OpenAI's registers one IPayloadTranslator;
            // the provider-keyed map below is what ProxyMiddleware consults. A provider with no
            // translator here is forwarded byte-for-byte, unchanged. Gemini and the three Bedrock
            // providers always translate; Anthropic (§4.4) is dual-mode - its translator is registered
            // here too, but ShouldTranslate lets real Claude Code traffic on /v1/messages keep passing
            // through untouched. The three Bedrock translators (§4.2) additionally implement
            // IBedrockPayloadTranslator, which ProxyMiddleware uses to fork into its AWS-SDK invocation
            // path instead of raw-HTTP forwarding. Registering the map from GetServices means a future
            // provider just adds its own AddSingleton<IPayloadTranslator, ...> to join it, no wiring
            // change here.
            services.AddSingleton<IPayloadTranslator, GeminiPayloadTranslator>();
            services.AddSingleton<IPayloadTranslator, AnthropicPayloadTranslator>();
            services.AddSingleton<IPayloadTranslator, AnthropicOnBedrockPayloadTranslator>();
            services.AddSingleton<IPayloadTranslator, TitanPayloadTranslator>();
            services.AddSingleton<IPayloadTranslator, LlamaPayloadTranslator>();
            services.AddSingleton<IReadOnlyDictionary<string, IPayloadTranslator>>(sp =>
                sp.GetServices<IPayloadTranslator>().ToDictionary(t => t.Provider, StringComparer.OrdinalIgnoreCase));

            // Tool-call normalization (docs/router/tool-call-normalization.md Phase 4) is registered as
            // itself, deliberately NOT also as IPayloadTranslator - it must never join the provider-keyed
            // dictionary above, since what it scans for depends on the (provider, model) pair and on
            // whether the request offered any tools, none of which a provider-keyed lookup can express.
            // ProxyMiddleware picks it up via its own optional constructor parameter and asks it, per
            // request, for a translator.
            services.AddSingleton<ToolCallNormalizerFactory>();
            services.AddSingleton<IBedrockRuntimeClientFactory, BedrockRuntimeClientFactory>();

            // Personal-scale running spend total - terminal output via the injected ILogger, plus a
            // local JSON Lines file. See SpendTracker's remarks.
            services.AddOptions<SpendTrackingOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(SpendTrackingOptions.SectionName).Bind(options));
            services.AddSingleton<ISpendTracker, SpendTracker>();

            services.AddSingleton<TelemetryBroadcaster>();
            services.AddSingleton<TelemetryPublisher>();
            services.AddSingleton<ITelemetryPublisher>(sp => sp.GetRequiredService<TelemetryPublisher>());

            // Quality verifier (off-path, best-effort). IQualityScoreObserver resolves to a single
            // implementation, so a CompositeRouterScoreObserver fans each scored result out to both
            // RouterMemoryScoreObserver (live dim_best scores) and EmbeddingMemoryScoreObserver
            // (docs/router/live-feedback-learning-plan.md Phase 2c: memory_entries writes). Registered
            // before AddQuality so it wins over the library's Null default (which uses TryAdd).
            services.AddSingleton<Router.Embeddings.PendingTaskEmbeddingCache>();
            services.AddSingleton<Router.Embeddings.PendingRequestCostCache>();
            services.AddSingleton<Router.Embeddings.PendingRequestProvenanceCache>();
            services.AddSingleton<RouterMemoryScoreObserver>();
            services.AddSingleton<Router.EmbeddingMemoryScoreObserver>();

            // docs/router/self-organizing-classification-plan.md Phase T1: the opt-in transcript store.
            // TranscriptDatabase/SqliteTranscriptStore are registered unconditionally (SqliteTranscriptStore
            // itself no-ops every method when TranscriptOptions.Enabled is false, so nothing queries a table
            // that was never created), but TranscriptScoreObserver only joins the fan-out below when capture
            // is enabled - registering a dead observer for the common (disabled) case would do nothing
            // except add a per-score no-op call.
            services.AddOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(TotallyHot.ArcRouter.Transcripts.TranscriptOptions.SectionName).Bind(options));
            services.AddSingleton<TotallyHot.ArcRouter.Transcripts.TranscriptDatabase>();
            services.AddSingleton<TotallyHot.ArcRouter.Transcripts.ITranscriptStore, TotallyHot.ArcRouter.Transcripts.SqliteTranscriptStore>();
            services.AddSingleton<TotallyHot.ArcRouter.Transcripts.TranscriptScoreObserver>();

            // docs/router/self-organizing-classification-plan.md Phase T4: the taxonomy comparison's own
            // store, sharing TranscriptDatabase's file and its enabled gate - with no transcripts there is
            // nothing to compare, so this needs no separate switch.
            services.AddSingleton<TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore, TotallyHot.ArcRouter.Transcripts.SqliteTaxonomyComparisonStore>();

            // docs/router/geval-shadow-scoring-plan.md Phase G1: the shadow judge. Every collaborator
            // (cache, queue, client, store, observer) is registered unconditionally - PendingResponseTextCache
            // and JudgeShadowScoreQueue are inert until something writes to them, and
            // SqliteJudgeShadowScoreStore shares RouterMemoryDatabase's file/schema, already created
            // unconditionally above.
            //
            // JudgeOptions is deliberately NOT bound from appsettings.json. Its two operator-facing settings
            // live in the router_settings table and are layered on by JudgeSettingsConfigureOptions, the
            // JudgeOptions counterpart of RouterSettingsConfigureOptions - so the judge is configured from
            // the System Settings window, and its backbone is whichever free model the operator set up in
            // the Providers screen (JudgeModelSelector), never a hardcoded endpoint.
            //
            // Enabled is therefore a *live* flag, which is why the observer below joins the fan-out
            // unconditionally and gates per call instead - a construction-time check could never see a
            // later toggle. Same reasoning at the drain worker, the retention loop, and ProxyMiddleware's
            // response-text retention site.
            services.AddOptions<TotallyHot.ArcRouter.Judge.JudgeOptions>()
                .ValidateDataAnnotations();
            services.AddSingleton<IConfigureOptions<TotallyHot.ArcRouter.Judge.JudgeOptions>, TotallyHot.ArcRouter.Judge.JudgeSettingsConfigureOptions>();
            services.AddHttpClient(TotallyHot.ArcRouter.Judge.GEvalJudgeClient.HttpClientName);
            services.AddSingleton<TotallyHot.ArcRouter.Judge.PendingResponseTextCache>();
            services.AddSingleton<TotallyHot.ArcRouter.Judge.IJudgeShadowScoreQueue, TotallyHot.ArcRouter.Judge.JudgeShadowScoreQueue>();
            services.AddSingleton<TotallyHot.ArcRouter.Judge.JudgeModelSelector>();
            services.AddSingleton<TotallyHot.ArcRouter.Judge.IJudgeClient, TotallyHot.ArcRouter.Judge.GEvalJudgeClient>();
            services.AddSingleton<TotallyHot.ArcRouter.Judge.IJudgeShadowScoreStore, TotallyHot.ArcRouter.Judge.SqliteJudgeShadowScoreStore>();
            services.AddSingleton<TotallyHot.ArcRouter.Judge.JudgeShadowScoreObserver>();

            // Promotes the judge from a shadow observer to a real contributor: this is what tells the
            // quality aggregator to hold a static verdict open for a judge grade instead of writing it
            // immediately. Registered before AddQuality so it wins that method's TryAddSingleton default.
            services.AddSingleton<TotallyHot.ArcRouter.Quality.Grading.IJudgeAvailability, TotallyHot.ArcRouter.Judge.JudgeAvailability>();

            services.AddSingleton<IQualityScoreObserver>(sp =>
            {
                var observers = new List<IQualityScoreObserver>
                {
                    sp.GetRequiredService<RouterMemoryScoreObserver>(),
                    sp.GetRequiredService<Router.EmbeddingMemoryScoreObserver>(),
                };

                // docs/router/self-organizing-classification-plan.md Phase T6: transcript-score
                // backfill additionally requires the adaptive-routing master switch, on top of its own
                // TranscriptOptions.Enabled flag. A plain IOptions snapshot is sufficient here (not the
                // monitor) - this factory runs once, at this singleton's construction, so a later toggle
                // has nothing to re-run regardless.
                if (sp.GetRequiredService<IOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>>().Value.Enabled
                    && sp.GetRequiredService<IOptions<RoutingOptions>>().Value.EnableAdaptiveRouting)
                {
                    observers.Add(sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.TranscriptScoreObserver>());
                }

                // docs/router/geval-shadow-scoring-plan.md Phase G1: unlike the transcript observer above,
                // this one joins the fan-out unconditionally and checks JudgeOptions.Enabled itself on every
                // ObserveAsync. JudgeOptions.Enabled is operator-toggleable from System Settings, and this
                // factory runs exactly once - a check here would freeze the judge in whatever state the
                // process started in.
                observers.Add(sp.GetRequiredService<TotallyHot.ArcRouter.Judge.JudgeShadowScoreObserver>());

                return new Router.CompositeRouterScoreObserver(observers, sp.GetRequiredService<ILogger<Router.CompositeRouterScoreObserver>>());
            });
            services.AddQuality();

            // In-flight request gauge (docs/router/routing-roi-regret-plan.md): a singleton so
            // ProxyMiddleware (incrementing per served request) and TaxonomyComparisonService
            // (hard-pausing its comparison drain while the count is non-zero) observe one number.
            services.AddSingleton<InFlightRequestGauge>();

            services.AddSingleton<ProxyMiddleware>();

            // Model price catalog (docs/router/model-price-catalog.md). Shares agent_telemetry.db with the
            // future usage ledger via the top-level Storage section. PriceCatalogOptions is validated by
            // PriceSourceRegistry's constructor (eager, at startup - mirroring ProviderConfigStore), so a
            // bad source name fails before Kestrel binds.
            services.AddOptions<StorageOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(StorageOptions.SectionName).Bind(options));
            services.AddOptions<PriceCatalogOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(PriceCatalogOptions.SectionName).Bind(options));
            services.AddSingleton<PriceCatalogDatabase>();

            // CodeRouterBench corpus (docs/router/coderouterbench-sqlite-migration-plan.md Phase 1): its
            // own coderouterbench.db, separate from agent_telemetry.db - see BenchmarkDatabase's summary.
            // Reuses the StorageOptions registration above.
            services.AddSingleton<BenchmarkDatabase>();
            services.AddSingleton<BenchmarkFileLedger>();

            // Phase 2: the checksum probe and sync service share one named HttpClient, mirroring how
            // OnnxEmbeddingClient above keeps IHttpClientFactory itself and creates a client per call -
            // not a client created once and captured for the singleton's lifetime, which would opt these
            // services out of the factory's handler lifetime rotation (DNS changes / connection refresh).
            services.AddOptions<BenchmarkSyncOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(BenchmarkSyncOptions.SectionName).Bind(options));
            services.AddHttpClient(BenchmarkChecksumProbe.HttpClientName);
            services.AddSingleton<BenchmarkChecksumProbe>();
            services.AddSingleton<BenchmarkSyncService>();

            // Phase 3: the cached Current/Update/CheckFailed state StartupHealthCheckHostedService
            // computes at startup and the Governance panel's "Recheck" action recomputes on demand.
            services.AddSingleton<BenchmarkDataStatusService>();

            // Operator price-override store (docs/router/token-tracking-implementation-plan.md Phase 3 §5.7):
            // the resolution ladder's top rung. Registered before the resolver so the container injects it
            // into ConfigModelIdentityResolver's optional overrideStore parameter.
            services.AddSingleton<ModelAliasOverrideStore>();
            // D3/§5.7 alias resolver (docs/router/d3-alias-resolution.md, docs/router/token-tracking-improvements.md
            // §5.7): maps each source's own model/provider naming onto the configured router identity at
            // ingest via the resolution ladder, so cost resolves on the client-facing ModelName. Registered
            // so the container injects it into PriceCatalogRepository's optional param.
            services.AddSingleton<IModelIdentityResolver, ConfigModelIdentityResolver>();
            services.AddSingleton<PriceCatalogRepository>();
            // Request-path price lookup (docs/router/model-price-catalog.md): ProxyMiddleware
            // estimates each paid request's cost from the catalog through this seam. Registered so the
            // container injects it into ProxyMiddleware's optional priceLookup constructor parameter.
            services.AddSingleton<IModelPriceLookup, PriceCatalogModelPriceLookup>();
            // The wider Phase 4 read surface (docs/router/model-price-catalog.md): the same rows as the
            // lookup above, but tier-selected via PriceContext and served from an in-memory cache so a
            // routing decision can price candidates inline with a live request without touching SQLite.
            // A singleton because the cache is the point - a per-request instance would never hit.
            services.AddSingleton<ModelPriceCatalog>();
            services.AddSingleton<IModelPriceCatalog>(sp => sp.GetRequiredService<ModelPriceCatalog>());
            // Owns aggregator_sources.enabled (D6). Starts empty and is populated by
            // StartupHealthCheckHostedService once the schema exists - see PriceSourceToggleStore's remarks.
            services.AddSingleton<PriceSourceToggleStore>();
            // Owns per-provider monthly budgets + spend (Governance > Providers). Same empty-until-schema-ready
            // lifecycle as the toggle store: StartupHealthCheckHostedService calls Reload after EnsureCreated.
            // Injected into ProxyMiddleware's optional budgetStore param (enforcement + spend recording) and
            // passed across to the inner host for the /admin budget endpoints.
            services.AddSingleton<ProviderBudgetStore>();
            // ProxyMiddleware depends only on the enforce+record slice (IBudgetEnforcer); map it to the same
            // singleton so the request path and the admin/cap surface share one store and one snapshot.
            services.AddSingleton<IBudgetEnforcer>(sp => sp.GetRequiredService<ProviderBudgetStore>());
            // Captures upstream anthropic-ratelimit-* response headers (docs/router/anthropic-reported-usage-plan.md
            // §5) into the same price-catalog database. Injected into ProxyMiddleware's optional
            // rateLimitCapture constructor parameter.
            services.AddSingleton<IRateLimitHeaderCapture, RateLimitHeaderCapture>();
            // The Phase 4 rollup maintainer (docs/router/token-tracking-implementation-plan.md §5.3),
            // sharing agent_telemetry.db with the ledger it rolls up. Registered before IUsageLedger so the
            // container injects it into UsageLedger's optional rollupStore constructor parameter (DI
            // resolution order is independent of registration order, but the sequence here keeps the two
            // reads next to each other). StartupHealthCheckHostedService pins the bucket timezone and runs
            // the startup backfill against this same singleton.
            services.AddSingleton<IUsageRollupStore, UsageRollupStore>();
            // The durable usage ledger (docs/router/token-tracking-implementation-plan.md Phase 2), sharing
            // agent_telemetry.db with the rest of the price catalog. Injected into ProxyMiddleware's optional
            // usageLedger constructor parameter and into PersistentConversationTurnTracker above (registered
            // earlier in this method only because DI resolution order is independent of registration order).
            services.AddSingleton<IUsageLedger, UsageLedger>();
            // Provider cost reconciliation (docs/router/token-tracking-implementation-plan.md §5.8):
            // compares each configured provider's own billing API against the local ledger estimate.
            // Entirely optional - a provider only gets an IProviderCostReconciler when its
            // AdminApiKeyEnvVar is configured *and* resolves to a non-empty value; with none configured,
            // CostReconciliationHostedService's poll loop still runs but does nothing every cycle. The
            // reconciler list is built once (a singleton factory, not per-call) since the underlying admin
            // keys don't change without a restart.
            services.AddOptions<CostReconciliationOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(CostReconciliationOptions.SectionName).Bind(options));
            services.AddSingleton<IProviderCostReconciliationStore, ProviderCostReconciliationStore>();
            services.AddSingleton<IReadOnlyList<IProviderCostReconciler>>(sp => BuildCostReconcilers(sp));
            services.AddSingleton<IEnumerable<IProviderCostReconciler>>(
                sp => sp.GetRequiredService<IReadOnlyList<IProviderCostReconciler>>());
            // Rebuilds the reconciler list from scratch on every reconciliation cycle
            // (docs/router/secrets-at-rest-plan.md §7) rather than the fixed list above, which is captured
            // once at DI construction: an operator who saves a stored Admin API key from the GUI needs the
            // very next cycle to pick it up, not the next process restart. CostReconciliationService falls
            // back to the fixed list when no factory is supplied (every existing unit test).
            services.AddSingleton<Func<IReadOnlyList<IProviderCostReconciler>>>(sp => () => BuildCostReconcilers(sp));
            services.AddSingleton<CostReconciliationService>();
            // Anthropic's own reported per-model daily token usage (docs/router/secrets-at-rest-plan.md
            // §8.1) - a Console/Enterprise-only feature layered on the same Admin API key as reconciliation
            // above. The key resolver runs stored-secret-then-env-var fresh on every cycle (mirroring
            // BuildCostReconcilers), so a key saved from the GUI takes effect without a restart, and an
            // account with none configured (Claude Pro/Max) simply gets a permanent no-op.
            services.AddSingleton(sp => new AnthropicUsageReportService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<PriceCatalogRepository>(),
                () =>
                {
                    var reconciliationOptions = sp.GetRequiredService<IOptions<CostReconciliationOptions>>().Value;
                    var environment = sp.GetRequiredService<IEnvironmentVariableProvider>();
                    var secretReader = sp.GetService<ISecretReader>();
                    return TryResolveAdminApiKey(reconciliationOptions, environment, secretReader, "anthropic", out var key) ? key : null;
                },
                sp.GetRequiredService<ILogger<AnthropicUsageReportService>>()));
            // Per-(provider, model) tool-call dialect capabilities (docs/router/tool-call-normalization.md
            // Phase 1). Shares agent_telemetry.db with the price catalog, so it has the same
            // empty-until-schema-ready lifecycle as the two stores above: StartupHealthCheckHostedService
            // calls Reload after EnsureCreated. The request path takes the narrow read/record slice
            // (IToolCallCapabilityStore) mapped to the same singleton, so both see one snapshot.
            services.AddSingleton<ToolCallCapabilityRepository>();
            services.AddSingleton<ToolCallCapabilityStore>();
            services.AddSingleton<IToolCallCapabilityStore>(sp => sp.GetRequiredService<ToolCallCapabilityStore>());
            services.AddSingleton<PriceSourceRegistry>();
            services.AddSingleton<IPriceSourceRegistry>(sp => sp.GetRequiredService<PriceSourceRegistry>());
            services.AddSingleton<PriceCatalogIngestionService>();

            // Shared management core (docs/router/mcp-endpoint-plan.md): both the REST /admin/* API and the
            // MCP endpoint's provider tools project through this one facade, so credential masking, header
            // resolution, and validation happen in exactly one place. Registered here so MCP (which lives in
            // this outer container) can resolve it; ProxyServer builds its own instance from the same
            // underlying stores for REST - the facade is stateless, so the two instances behave identically.
            services.AddSingleton<HttpClient>();
            // Probes a provider's well-known paths for which API flavors it answers
            // (docs/router/tool-call-normalization.md §3.3). Registered before the facade so the container
            // injects it into the facade's optional constructor parameters.
            services.AddSingleton<ProviderEndpointScanner>();
            // ManagementFacade's constructor resolves PriceCatalogRepository automatically (registered
            // above) into its optional priceCatalogRepository parameter, so the Anthropic Usage card's
            // rate-limit snapshot is available on this MCP-facing facade the same way it is on the REST one
            // ProxyHostedService builds below.
            services.AddSingleton<ManagementFacade>();

            // MCP (Model Context Protocol) management endpoint - agent-facing access to the same
            // provider/model/budget/price-source management as REST /admin/*, over a dedicated loopback TLS
            // port. See docs/router/mcp-endpoint-plan.md.
            services.AddOptions<McpOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(McpOptions.SectionName).Bind(options));
            services.AddHostedService<McpHostedService>();

            // docs/router/auto-update-plan.md Phase 2: the Router's self-update check/apply pipeline.
            // GitHubReleaseCheckClient and UpdateApplier are registered as typed HttpClients (rather than
            // the IHttpClientFactory-named-client pattern OnnxEmbeddingClient uses) since each has exactly
            // one HTTP concern and no reason to create more than one named client per use.
            // UpdateCheckHostedService only ever *detects* an available update (see its own remarks); an
            // apply is reached exclusively through UpdateAdminGrpcService.ApplyUpdate, behind an explicit
            // operator click in the GUI.
            services.AddOptions<UpdateOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    configuration.GetSection(UpdateOptions.SectionName).Bind(options))
                .ValidateDataAnnotations()
                .Validate(options =>
                {
                    options.EnsureValid();
                    return true;
                })
                .ValidateOnStart();
            services.AddHttpClient<IReleaseCheckClient, GitHubReleaseCheckClient>();
            services.AddHttpClient<IUpdateApplier, UpdateApplier>();
            services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
            services.AddHostedService<UpdateCheckHostedService>();

            // docs/router/self-organizing-classification-plan.md Phase T1d-T1e: background services for
            // embedding backfill and transcript retention. Both are registered unconditionally but are no-ops
            // when their respective feature flags are off (Enabled for retention, EnableEmbeddingBackfill for
            // backfill). Registered after the transcript store but before ProxyHostedService.
            services.AddHostedService<TotallyHot.ArcRouter.Transcripts.EmbeddingBackfillService>();
            services.AddHostedService<TotallyHot.ArcRouter.Transcripts.TranscriptRetentionService>();

            // The quality rescan: grades saved transcript rows rather than in-flight responses, so a
            // response the live queue dropped under load still gets graded, and a scorer change can be
            // measured against the corpus already captured instead of only against future traffic. Same
            // unconditional registration as the two above - it no-ops while EnableQualityRescan is off.
            // See docs/research/code-quality-metrics-assessment.md for why grading needs saved data.
            services.AddHostedService<TotallyHot.ArcRouter.Transcripts.QualityRescanService>();

            // docs/router/geval-shadow-scoring-plan.md Phase G1: the shadow judge's drain worker and
            // retention purge. Both are registered unconditionally and keep running regardless, no-opping
            // per job / per tick while JudgeOptions.Enabled is false - that flag is toggleable at runtime,
            // so neither may exit at startup on reading it once.
            services.AddHostedService<TotallyHot.ArcRouter.Judge.JudgeShadowScoreDrainService>();
            services.AddHostedService<TotallyHot.ArcRouter.Judge.JudgeShadowScoreRetentionService>();

            // docs/router/self-organizing-classification-plan.md Phase T4: drains the comparison queue on a
            // timer. Deliberately off the request path - a comparison needs both a verifier score and a
            // backfilled embedding, so it cannot run inline, and its results are explicitly not real-time.
            services.AddHostedService<TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonService>();

            // Hosted-service order matters: the generic host awaits each StartAsync in registration order,
            // so the startup checks (which pull the first pricing cycle) run to completion before
            // ProxyHostedService binds Kestrel below. The background poll loop is registered between them;
            // it does not run its own initial cycle.
            services.AddHostedService<StartupHealthCheckHostedService>();
            services.AddHostedService<PriceCatalogIngestionHostedService>();
            services.AddHostedService<CostReconciliationHostedService>();
            services.AddHostedService<LogRegRetrainHostedService>();
            services.AddHostedService<ClusterRetrainHostedService>();

            // ProxyServer's inner Kestrel host is handed an already-constructed ProxyMiddleware instance rather
            // than a copy of this IServiceCollection. It never gets its own IHostedService registrations, so it
            // can never end up recursively constructing another ProxyHostedService.
            services.AddHostedService(sp =>
            {
                // The /admin/* management API is served from the same host: pass the writable config store
                // (edits reload the router live), the credential accessor for model discovery, and the
                // always-present per-user management token (see ManagementAccessToken) that gates every
                // /admin request by default - the same token the MCP endpoint requires, so both management
                // surfaces are gated identically out of the box. The price catalog singletons are passed
                // across for the same reason as the broadcaster: the inner host has its own container and
                // cannot resolve them from this one. They back the Governance > Price Sources panel's gRPC API.
                return new ProxyHostedService(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProxyHostedService>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProxyServer>>(),
                    sp.GetRequiredService<ProxyMiddleware>(),
                    dependencies: new ProxyServerDependencies
                    {
                        Telemetry = sp.GetRequiredService<TelemetryBroadcaster>(),
                        // The always-present per-user token that gates every /admin request and every gRPC
                        // call by default - the same token the MCP endpoint requires, so both management
                        // surfaces are gated identically out of the box.
                        ManagementToken = ManagementAccessToken.GetOrCreate(),
                        // Backs the Governance > Routing Mode panel's gRPC API (docs/router/orchestrator-live-path-plan.md §M3.2).
                        RoutingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>(),

                        // The /admin/* management REST API. The writable config store makes edits reload the
                        // router live; the rest is what the REST facade needs, passed across for the same
                        // reason as everything else here - the inner host has its own container and cannot
                        // resolve any of it from this one.
                        ManagementApi = new ManagementApiDependencies(sp.GetRequiredService<IProviderConfigStore>())
                        {
                            Environment = sp.GetRequiredService<IEnvironmentVariableProvider>(),
                            BudgetStore = sp.GetRequiredService<ProviderBudgetStore>(),
                            EndpointScanner = sp.GetRequiredService<ProviderEndpointScanner>(),
                            CapabilityStore = sp.GetRequiredService<ToolCallCapabilityStore>(),
                            PriceCatalogRepository = sp.GetRequiredService<PriceCatalogRepository>(),
                            ModelAliasOverrideStore = sp.GetRequiredService<ModelAliasOverrideStore>(),
                            UsageRollupStore = sp.GetRequiredService<IUsageRollupStore>(),
                            SecretWriter = sp.GetRequiredService<ISecretWriter>(),
                            SecretReader = sp.GetRequiredService<ISecretReader>(),
                            // Backs Cost Analytics' "Routing ROI" feed (docs/router/self-organizing-classification-plan.md Phase T4).
                            TaxonomyComparisonStore = sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore>(),
                        },

                        // Backs the Governance > Price Sources panel's gRPC API.
                        PriceSourceAdmin = new PriceSourceAdminDependencies(
                            sp.GetRequiredService<PriceSourceToggleStore>(),
                            sp.GetRequiredService<PriceCatalogIngestionService>())
                        {
                            Options = sp.GetRequiredService<IOptions<PriceCatalogOptions>>().Value,
                        },

                        // Backs the Governance > Benchmark Data panel's gRPC API (Phase 4).
                        BenchmarkDataAdmin = new BenchmarkDataAdminDependencies(
                            sp.GetRequiredService<BenchmarkDataStatusService>(),
                            sp.GetRequiredService<BenchmarkFileLedger>(),
                            sp.GetRequiredService<BenchmarkSyncService>())
                        {
                            Options = sp.GetRequiredService<IOptions<BenchmarkSyncOptions>>().Value,
                        },

                        // Backs the Governance > Benchmark Data panel's "Local Voter Model" gRPC API.
                        LlmRouterModelAdmin = new LlmRouterModelAdminDependencies(
                            sp.GetRequiredService<Router.TextGeneration.ILlmRouterModelOverrideStore>(),
                            sp.GetRequiredService<Router.TextGeneration.LlmRouterModelSyncService>()),

                        // Backs the Governance > Cluster Model panel's gRPC API (Phase T5).
                        ClusterModelAdmin = new ClusterModelAdminDependencies(
                            sp.GetRequiredService<Router.Orchestrator.IClusterTrainingService>(),
                            sp.GetRequiredService<Router.IMemoryEntryStore>(),
                            sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.ITranscriptStore>(),
                            sp.GetRequiredService<IOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>>(),
                            sp.GetRequiredService<IOptions<StorageOptions>>()),

                        // Backs the Governance > Router Model panel's gRPC API (live-feedback-learning-plan.md Phase 5).
                        LogRegModelAdmin = new LogRegModelAdminDependencies(
                            sp.GetRequiredService<Router.Orchestrator.IEmbeddingLogRegTrainingService>(),
                            sp.GetRequiredService<Router.IMemoryEntryStore>(),
                            sp.GetRequiredService<IOptions<StorageOptions>>()),

                        // Backs the Governance UI's System Settings window gRPC API (Phase T6).
                        RouterSettingsAdmin = new RouterSettingsAdminDependencies(
                            sp.GetRequiredService<Router.RouterSettingsStore>(),
                            sp.GetRequiredService<Router.RouterSettingsReloadToken>(),
                            sp.GetRequiredService<IOptionsMonitor<RoutingOptions>>(),
                            sp.GetRequiredService<IOptionsMonitor<TotallyHot.ArcRouter.Judge.JudgeOptions>>(),
                            sp.GetRequiredService<TotallyHot.ArcRouter.Judge.JudgeModelSelector>())
                        {
                            EmbeddingMemory = sp.GetRequiredService<EmbeddingMemory>(),
                        },

                        // Backs the Governance UI's System Settings window's "Software Update" section gRPC
                        // API (docs/router/auto-update-plan.md Phase 2).
                        UpdateAdmin = new UpdateAdminDependencies(
                            sp.GetRequiredService<IUpdateStateStore>(),
                            sp.GetRequiredService<IReleaseCheckClient>(),
                            sp.GetRequiredService<IUpdateApplier>()),
                    });
            });

            return services;
        }

        /// <summary>
        /// Builds the list of <see cref="IProviderCostReconciler"/>s: one per provider with a resolvable
        /// Admin API key - a stored secret (<c>reconciliation:{provider}:admin-key</c>, see
        /// <see cref="TryResolveAdminApiKey"/>) or <see cref="ProviderReconciliationOptions.AdminApiKeyEnvVar"/>.
        /// Only <c>openai</c> and <c>anthropic</c> are recognized (docs/router/agent-cost-tracking.md §3.5) -
        /// an unrecognized provider key under <c>CostTracking:Reconciliation:Providers</c> is silently
        /// ignored, matching how an unresearched provider (Alibaba/Zhipu/Moonshot/MiniMax) simply has no
        /// reconciler. Called once at DI construction for the fixed fallback list, and again on every
        /// reconciliation cycle via the registered <c>Func&lt;IReadOnlyList&lt;IProviderCostReconciler&gt;&gt;</c>
        /// (docs/router/secrets-at-rest-plan.md §7) so a key saved from the GUI takes effect without a restart.
        /// </summary>
        internal static IReadOnlyList<IProviderCostReconciler> BuildCostReconcilers(IServiceProvider sp)
        {
            var options = sp.GetRequiredService<IOptions<CostReconciliationOptions>>().Value;
            var environment = sp.GetRequiredService<IEnvironmentVariableProvider>();
            var secretReader = sp.GetService<ISecretReader>();
            var httpClient = sp.GetRequiredService<HttpClient>();

            var reconcilers = new List<IProviderCostReconciler>();

            if (TryResolveAdminApiKey(options, environment, secretReader, "openai", out var openAiKey))
            {
                reconcilers.Add(new OpenAiCostReconciler(httpClient, openAiKey, sp.GetService<ILogger<OpenAiCostReconciler>>()));
            }

            if (TryResolveAdminApiKey(options, environment, secretReader, "anthropic", out var anthropicKey))
            {
                reconcilers.Add(new AnthropicCostReconciler(httpClient, anthropicKey, sp.GetService<ILogger<AnthropicCostReconciler>>()));
            }

            return reconcilers;
        }

        /// <summary>
        /// Resolves <paramref name="provider"/>'s Admin API key, stored secret first
        /// (<see cref="AdminApiKeySecretName"/>) then <see cref="ProviderReconciliationOptions.AdminApiKeyEnvVar"/>
        /// (docs/router/secrets-at-rest-plan.md §7) - so a key saved through <c>PUT /admin/secrets/{name}</c>
        /// takes priority over (and needs no change to) an existing environment-variable deployment.
        /// </summary>
        internal static bool TryResolveAdminApiKey(
            CostReconciliationOptions options,
            IEnvironmentVariableProvider environment,
            ISecretReader? secretReader,
            string provider,
            out string adminApiKey)
        {
            adminApiKey = string.Empty;

            if (secretReader is not null &&
                secretReader.TryRead(AdminApiKeySecretName(provider), out var stored) &&
                !string.IsNullOrWhiteSpace(stored))
            {
                adminApiKey = stored;
                return true;
            }

            if (!options.Providers.TryGetValue(provider, out var providerOptions) ||
                string.IsNullOrWhiteSpace(providerOptions.AdminApiKeyEnvVar))
            {
                return false;
            }

            var resolved = environment.GetVariable(providerOptions.AdminApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return false;
            }

            adminApiKey = resolved;
            return true;
        }

        /// <summary>The protected-store name for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §3's naming convention).</summary>
        internal static string AdminApiKeySecretName(string provider) => $"reconciliation:{provider}:admin-key";
    }
}

