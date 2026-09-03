using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Registers transcript capture with the DI container. Split out of
/// <see cref="TotallyHot.ArcRouter.Hosting.ServiceCollectionExtensions"/> so that adding a
/// dependency here is a change to this feature's own folder rather than an edit to a single
/// 1000-line file every feature shares.
/// </summary>
internal static class TranscriptsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transcript store (docs/router/self-organizing-classification-plan.md Phase T1)
    /// and its taxonomy-comparison companion store (Phase T4).
    /// </summary>
    internal static IServiceCollection AddTranscripts(this IServiceCollection services)
    {
        // docs/router/self-organizing-classification-plan.md Phase T1: the transcript store, on by
        // default and operator-toggleable live from the System Settings window's Transcription Capture
        // row. TranscriptDatabase/SqliteTranscriptStore are registered unconditionally
        // (SqliteTranscriptStore itself no-ops every method when TranscriptOptions.Enabled is currently
        // false, so nothing queries a table that was never created), and TranscriptScoreObserver joins
        // the fan-out below unconditionally too, gating per call the same way - a construction-time
        // check could never see a later toggle.
        //
        // The SQLite-backed override layer (TranscriptSettingsConfigureOptions) is registered as an
        // IConfigureOptions<TranscriptOptions> step *after* the appsettings.json bind below - Options
        // pattern configure delegates run in registration order, so this one runs second and wins,
        // giving "stored override > appsettings.json > coded default" precedence, exactly like
        // RouterSettingsConfigureOptions does for RoutingOptions above.
        services.AddOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(TotallyHot.ArcRouter.Transcripts.TranscriptOptions.SectionName).Bind(options));
        services.AddSingleton<IConfigureOptions<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>, TotallyHot.ArcRouter.Transcripts.TranscriptSettingsConfigureOptions>();
        services.AddSingleton<IOptionsChangeTokenSource<TotallyHot.ArcRouter.Transcripts.TranscriptOptions>>(sp => sp.GetRequiredService<Router.RouterSettingsReloadToken>());
        services.AddSingleton<TotallyHot.ArcRouter.Transcripts.TranscriptDatabase>();
        services.AddSingleton<TotallyHot.ArcRouter.Transcripts.ITranscriptStore, TotallyHot.ArcRouter.Transcripts.SqliteTranscriptStore>();
        services.AddSingleton<TotallyHot.ArcRouter.Transcripts.TranscriptScoreObserver>();

        // docs/router/self-organizing-classification-plan.md Phase T4: the taxonomy comparison's own
        // store, sharing TranscriptDatabase's file and its enabled gate - with no transcripts there is
        // nothing to compare, so this needs no separate switch.
        services.AddSingleton<TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore, TotallyHot.ArcRouter.Transcripts.SqliteTaxonomyComparisonStore>();

        return services;
    }
}
