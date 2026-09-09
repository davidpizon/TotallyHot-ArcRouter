using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Registers the G-Eval judge and Phase Q3's CodeJudge/ICE-Score/RACE grader portfolio with the DI
/// container. Split out of <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that
/// adding a dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class JudgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the geval shadow judge (docs/router/geval-shadow-scoring-plan.md Phase G1) and the Phase Q3
    /// portfolio graders: their caches, queues, clients, stores, dispatchers, and the availability flags
    /// that promote them from shadow dispatchers to real quality-aggregator contributors, fanned into the
    /// aggregator's single <see cref="IAsyncGraderDispatcher"/> seam via <see cref="CompositeAsyncGraderDispatcher"/>.
    /// </summary>
    internal static IServiceCollection AddJudge(this IServiceCollection services)
    {
        // docs/router/geval-shadow-scoring-plan.md Phase G1: the shadow judge. Every collaborator
        // (cache, queue, client, store, dispatcher) is registered unconditionally - PendingResponseTextCache
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
        // Enabled is therefore a *live* flag, which is why JudgeShadowScoreDispatcher is started
        // unconditionally by QualityScoreAggregator.SubmitAsync and gates per call instead - a
        // construction-time check could never see a later toggle. Same reasoning at the drain worker,
        // the retention loop, and ProxyMiddleware's response-text retention site.
        services.AddOptions<JudgeOptions>()
            .ValidateDataAnnotations();
        services.AddSingleton<IConfigureOptions<JudgeOptions>, JudgeSettingsConfigureOptions>();
        services.AddHttpClient(GEvalJudgeClient.HttpClientName);
        services.AddSingleton<PendingResponseTextCache>();
        services.AddSingleton<PendingPromptCache>();
        services.AddSingleton<IJudgeShadowScoreQueue, JudgeShadowScoreQueue>();
        services.AddSingleton<JudgeModelSelector>();
        services.AddSingleton<IJudgeClient, GEvalJudgeClient>();
        services.AddSingleton<IJudgeShadowScoreStore, SqliteJudgeShadowScoreStore>();
        services.AddSingleton<JudgeShadowScoreDispatcher>();

        // Promotes the judge from a shadow observer to a real contributor: this is what tells the
        // quality aggregator to hold a static verdict open for a judge grade instead of writing it
        // immediately. Registered before AddQuality so it wins that method's TryAddSingleton default.
        services.AddSingleton<IJudgeAvailability, JudgeAvailability>();

        AddPortfolioGraders(services);

        // The aggregator takes exactly one IAsyncGraderDispatcher; this fans it out to the judge's own
        // dispatcher and the portfolio dispatcher below rather than letting either registration silently
        // shadow the other.
        services.AddSingleton<IAsyncGraderDispatcher>(sp => new CompositeAsyncGraderDispatcher(
            dispatchers:
            [
                sp.GetRequiredService<JudgeShadowScoreDispatcher>(),
                sp.GetRequiredService<PortfolioGraderDispatcher>()
            ],
            logger: sp.GetRequiredService<ILogger<CompositeAsyncGraderDispatcher>>()));

        return services;
    }

    /// <summary>
    /// Registers Phase Q3's CodeJudge/ICE-Score/RACE portfolio: shares the judge's backbone selection
    /// (<see cref="JudgeModelSelector"/>) and pending-text caches, and is configured the same
    /// stored-override-first way as the judge via <see cref="PortfolioGraderSettingsConfigureOptions"/>.
    /// </summary>
    private static void AddPortfolioGraders(IServiceCollection services)
    {
        services.AddOptions<PortfolioGraderOptions>();
        services.AddSingleton<IConfigureOptions<PortfolioGraderOptions>, PortfolioGraderSettingsConfigureOptions>();
        services.AddHttpClient(CodeJudgeGraderClient.HttpClientNameConstant);
        services.AddHttpClient(IceScoreGraderClient.HttpClientNameConstant);
        services.AddHttpClient(RaceGraderClient.HttpClientNameConstant);
        services.AddSingleton<IPortfolioGraderQueue, PortfolioGraderQueue>();
        services.AddSingleton<IPortfolioGraderClient, CodeJudgeGraderClient>();
        services.AddSingleton<IPortfolioGraderClient, IceScoreGraderClient>();
        services.AddSingleton<IPortfolioGraderClient, RaceGraderClient>();
        services.AddSingleton<PortfolioGraderDispatcher>();
        services.AddHostedService<PortfolioGraderDrainService>();

        // Promotes the portfolio from shadow dispatchers to real contributors, mirroring JudgeAvailability's
        // registration above.
        services.AddSingleton<IPortfolioGraderAvailability, PortfolioGraderAvailability>();
    }
}