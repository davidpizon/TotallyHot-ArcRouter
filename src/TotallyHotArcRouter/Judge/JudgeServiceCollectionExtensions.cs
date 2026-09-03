using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Registers the G-Eval judge with the DI container. Split out of
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that adding a
/// dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class JudgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the geval shadow judge (docs/router/geval-shadow-scoring-plan.md Phase G1): its cache,
    /// queue, client, store, observer, and the availability flag that promotes it from a shadow
    /// observer to a real quality-aggregator contributor.
    /// </summary>
    internal static IServiceCollection AddJudge(this IServiceCollection services)
    {
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

        return services;
    }
}
