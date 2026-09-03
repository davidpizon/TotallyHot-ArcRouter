using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Registers the price catalog with the DI container. Split out of
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that adding a
/// dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class PriceCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model price catalog (docs/router/model-price-catalog.md): the shared
    /// agent_telemetry.db storage options, the CodeRouterBench corpus and its sync pipeline, the price
    /// lookup/read surfaces, the operator budget/price-override stores, the usage ledger and rollup
    /// store, provider cost reconciliation, and the tool-call capability/context-window store.
    /// </summary>
    internal static IServiceCollection AddPriceCatalog(this IServiceCollection services)
    {
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
        // so the container injects it into PriceRepository's optional param.
        services.AddSingleton<IModelIdentityResolver, ConfigModelIdentityResolver>();
        // The six repositories the former monolithic PriceCatalogRepository was split into
        // (docs/router/code-smell-refactoring-plan.md M3), one per confirmed concern: price upsert/read,
        // source-toggle CRUD, provider-budget CRUD, provider-spend accounting, rate-limit header/history,
        // and reported-usage persistence. Each is a thin, independently-testable ADO.NET wrapper sharing
        // only PriceCatalogRepositoryBase's connection/timestamp plumbing.
        services.AddSingleton<PriceRepository>();
        services.AddSingleton<PriceSourceRepository>();
        services.AddSingleton<ProviderBudgetRepository>();
        services.AddSingleton<ProviderSpendRepository>();
        services.AddSingleton<RateLimitRepository>();
        services.AddSingleton<ReportedUsageRepository>();
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
        services.AddSingleton<IEnumerable<IProviderCostReconciler>>(sp =>
            sp.GetRequiredService<IReadOnlyList<IProviderCostReconciler>>());
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
            httpClient: sp.GetRequiredService<HttpClient>(),
            repository: sp.GetRequiredService<ReportedUsageRepository>(),
            resolveAdminApiKey: () =>
            {
                var reconciliationOptions = sp.GetRequiredService<IOptions<CostReconciliationOptions>>().Value;
                var environment = sp.GetRequiredService<IEnvironmentVariableProvider>();
                var secretReader = sp.GetService<ISecretReader>();
                return TryResolveAdminApiKey(options: reconciliationOptions, environment: environment,
                    secretReader: secretReader, provider: "anthropic", adminApiKey: out var key)
                    ? key
                    : null;
            },
            logger: sp.GetRequiredService<ILogger<AnthropicUsageReportService>>()));
        // Per-(provider, model) tool-call dialect capabilities (docs/router/tool-call-normalization.md
        // Phase 1). Shares agent_telemetry.db with the price catalog, so it has the same
        // empty-until-schema-ready lifecycle as the two stores above: StartupHealthCheckHostedService
        // calls Reload after EnsureCreated. The request path takes the narrow read/record slice
        // (IToolCallCapabilityStore) mapped to the same singleton, so both see one snapshot.
        services.AddSingleton<ToolCallCapabilityRepository>();
        services.AddSingleton<ToolCallCapabilityStore>();
        services.AddSingleton<IToolCallCapabilityStore>(sp => sp.GetRequiredService<ToolCallCapabilityStore>());

        // The same singleton again under its second read interface, for the proxy's /api/show handler.
        // Separate from IToolCallCapabilityStore because a context window is not a tool-call concern -
        // see IModelContextWindowStore - but backed by the one store so both read the same snapshot and
        // one Reload refreshes both.
        services.AddSingleton<IModelContextWindowStore>(sp => sp.GetRequiredService<ToolCallCapabilityStore>());
        services.AddSingleton<PriceSourceRegistry>();
        services.AddSingleton<IPriceSourceRegistry>(sp => sp.GetRequiredService<PriceSourceRegistry>());
        services.AddSingleton<PriceCatalogIngestionService>();

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

        if (TryResolveAdminApiKey(options: options, environment: environment, secretReader: secretReader,
                provider: "openai", adminApiKey: out var openAiKey))
            reconcilers.Add(new OpenAiCostReconciler(httpClient: httpClient, adminApiKey: openAiKey,
                logger: sp.GetService<ILogger<OpenAiCostReconciler>>()));

        if (TryResolveAdminApiKey(options: options, environment: environment, secretReader: secretReader,
                provider: "anthropic", adminApiKey: out var anthropicKey))
            reconcilers.Add(new AnthropicCostReconciler(httpClient: httpClient, adminApiKey: anthropicKey,
                logger: sp.GetService<ILogger<AnthropicCostReconciler>>()));

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
            secretReader.TryRead(name: AdminApiKeySecretName(provider), value: out var stored) &&
            !string.IsNullOrWhiteSpace(stored))
        {
            adminApiKey = stored;
            return true;
        }

        if (!options.Providers.TryGetValue(key: provider, value: out var providerOptions) ||
            string.IsNullOrWhiteSpace(providerOptions.AdminApiKeyEnvVar))
            return false;

        var resolved = environment.GetVariable(providerOptions.AdminApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(resolved)) return false;

        adminApiKey = resolved;
        return true;
    }

    /// <summary>
    /// The protected-store name for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md
    /// §3's naming convention).
    /// </summary>
    internal static string AdminApiKeySecretName(string provider)
    {
        return $"reconciliation:{provider}:admin-key";
    }
}