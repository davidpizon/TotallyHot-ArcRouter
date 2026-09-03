using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality.DependencyInjection;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;
using TotallyHot.ArcRouter.Update;

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
            // Split into one private method per subsystem purely for readability/reviewability - DI
            // resolution order is independent of registration order (see remarks scattered through the
            // methods below), so this split is a pure move: every method is called here in exactly the
            // source order the single method used to register things in, and no registration was
            // reordered relative to any other.
            services.AddRouterCore();
            services.AddProxyRequestPipeline();
            services.AddTelemetryAndTranslation();
            services.AddQualityAndObservability();
            services.AddProxyMiddlewareCore();
            services.AddPriceCatalog();
            services.AddManagement();
            services.AddUpdate();
            services.AddBackgroundServices();
            services.AddProxyHost();

            return services;
        }

        /// <summary>
        /// Registers the quality-observation fan-out: the score-observer caches, the transcript store, the
        /// shadow judge, the composite <see cref="IQualityScoreObserver"/> that fans a scored result out to
        /// all of them, and the static analyzers added by <see cref="QualityServiceCollectionExtensions.AddQuality"/>.
        /// </summary>
        private static IServiceCollection AddQualityAndObservability(this IServiceCollection services)
        {
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

            services.AddTranscripts();
            services.AddJudge();

            services.AddSingleton<IQualityScoreObserver>(sp =>
            {
                var observers = new List<IQualityScoreObserver>
                {
                    sp.GetRequiredService<RouterMemoryScoreObserver>(),
                    sp.GetRequiredService<Router.EmbeddingMemoryScoreObserver>(),
                };

                // docs/router/self-organizing-classification-plan.md Phase T6: joins the fan-out
                // unconditionally, like the judge observer below - TranscriptScoreObserver's own store call
                // (SqliteTranscriptStore.UpdateOutcomeAsync) reads TranscriptOptions.Enabled live via
                // IOptionsMonitor and no-ops when it is currently false, so a construction-time check here
                // would only freeze the toggle in whatever state the process started in. The
                // EnableAdaptiveRouting master switch still applies, but only at the insert site
                // (ProxyMiddleware, gated live off IOptionsMonitor<RoutingOptions>) - a row that was never
                // inserted has no correlation id for this backfill to match, so it naturally no-ops too.
                observers.Add(sp.GetRequiredService<TotallyHot.ArcRouter.Transcripts.TranscriptScoreObserver>());

                // docs/router/geval-shadow-scoring-plan.md Phase G1: unlike the transcript observer above,
                // this one joins the fan-out unconditionally and checks JudgeOptions.Enabled itself on every
                // ObserveAsync. JudgeOptions.Enabled is operator-toggleable from System Settings, and this
                // factory runs exactly once - a check here would freeze the judge in whatever state the
                // process started in.
                observers.Add(sp.GetRequiredService<TotallyHot.ArcRouter.Judge.JudgeShadowScoreObserver>());

                return new Router.CompositeRouterScoreObserver(observers, sp.GetRequiredService<ILogger<Router.CompositeRouterScoreObserver>>());
            });
            services.AddQuality();

            return services;
        }

        /// <summary>
        /// Registers the Router's self-update detection pipeline (docs/router/auto-update-plan.md Phase 2):
        /// the GitHub release-check client, update-state store, and the hosted service that polls it.
        /// </summary>
        private static IServiceCollection AddUpdate(this IServiceCollection services)
        {
            // docs/router/auto-update-plan.md Phase 2 (packaging superseded by
            // docs/router/packaging-and-distribution.md): the Router's self-update *detection* pipeline
            // only - it never downloads, verifies, or applies an update itself. GitHubReleaseCheckClient
            // is registered as a typed HttpClient (rather than the IHttpClientFactory-named-client
            // pattern OnnxEmbeddingClient uses) since it has exactly one HTTP concern and no reason to
            // create more than one named client per use. Applying is entirely the GUI's responsibility
            // (TotallyHot.ArcRouter.Gui.Telemetry.MsiUpdateApplier), reached from an explicit operator
            // click; this service only records that it is about to happen, via
            // UpdateAdminGrpcService.NotifyApplyStarting.
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
            services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
            services.AddHostedService<UpdateCheckHostedService>();

            return services;
        }

        /// <summary>
        /// Registers the router's background hosted services: embedding backfill/transcript retention,
        /// the quality rescan, the judge drain/retention loops, the taxonomy comparison drain, and the
        /// startup health check plus its dependent ingestion/reconciliation/retrain pollers. Registration
        /// order matters here - the generic host awaits each <c>StartAsync</c> in registration order, so
        /// the startup checks below run to completion before
        /// <see cref="Proxy.ProxyServiceCollectionExtensions.AddProxyHost"/>'s
        /// <c>ProxyHostedService</c> binds Kestrel.
        /// </summary>
        private static IServiceCollection AddBackgroundServices(this IServiceCollection services)
        {
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

            return services;
        }

    }
}
