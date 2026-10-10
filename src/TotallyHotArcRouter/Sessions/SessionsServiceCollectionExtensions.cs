using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Registers ADR-0019's session storage and its capture writer. Kept beside the feature so adding a
/// dependency here does not touch the shared service-collection file.
/// </summary>
internal static class SessionsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the master key store, the session index, the store, the startup service, the capture writer
    /// and its body pump, the capture factory, and the maintenance and retention services that delete sessions.
    /// The store is built lazily, on first use or at startup when something has already been stored, so
    /// a router that never captures creates no database or folder, and a store that cannot be opened fails the
    /// capture, not the host. The startup service is registered first, so it starts before the writer and
    /// before the proxy begins serving.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    internal static IServiceCollection AddSessionCapture(this IServiceCollection services)
    {
        services.AddOptions<SessionCaptureOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(SessionCaptureOptions.SectionName).Bind(options));

        services.AddSingleton<ISessionMasterKeyStore>(sp =>
            new SecretStoreSessionMasterKeyStore(sp.GetRequiredService<ProtectedSecretStore>()));
        services.AddSingleton<SessionIndex>();
        services.AddSingleton(sp => new Lazy<SessionStore>(() => OpenStore(sp)));
        services.AddSingleton(sp => sp.GetRequiredService<Lazy<SessionStore>>().Value);
        services.AddSingleton(sp => new Lazy<ISessionExtractReader>(() => sp.GetRequiredService<Lazy<SessionStore>>().Value));

        services.AddHostedService<SessionStoreStartupService>();
        services.AddSingleton<CaptureEpoch>();
        services.AddSingleton(CreateCaptureWriter);
        services.AddHostedService(sp => sp.GetRequiredService<SessionCaptureWriter>());

        // Registered after the writer, so a stop drains the pump first and its last turns still reach the writer.
        services.AddSingleton<CaptureBodyPump>();
        services.AddHostedService(sp => sp.GetRequiredService<CaptureBodyPump>());
        services.AddSingleton(CreateTurnCaptureFactory);

        services.AddSingleton<SessionMaintenance>();
        services.AddHostedService<SessionRetentionService>();
        return services;
    }

    /// <summary>
    /// Builds the capture writer with an optional drop callback that marks transcript rows terminal without
    /// injecting <see cref="ITranscriptStore"/> into the writer itself (#165 phase 2 follow-up).
    /// </summary>
    private static SessionCaptureWriter CreateCaptureWriter(IServiceProvider services)
    {
        return new SessionCaptureWriter(
            services.GetRequiredService<Lazy<SessionStore>>(),
            services.GetRequiredService<IOptions<SessionCaptureOptions>>(),
            services.GetRequiredService<ILogger<SessionCaptureWriter>>(),
            services.GetService<CaptureEpoch>(),
            CreateTurnDroppedHandler(services));
    }

    /// <summary>
    /// Builds the capture factory with the same drop callback the writer uses, so Submit/Dispose drops and
    /// writer drops share one mark path.
    /// </summary>
    private static TurnCaptureFactory CreateTurnCaptureFactory(IServiceProvider services)
    {
        return new TurnCaptureFactory(
            services.GetRequiredService<CaptureBodyPump>(),
            services.GetRequiredService<SessionCaptureWriter>(),
            services.GetRequiredService<Lazy<SessionStore>>(),
            services.GetRequiredService<CaptureEpoch>(),
            services.GetRequiredService<IOptionsMonitor<TranscriptOptions>>(),
            services.GetRequiredService<IOptions<SessionCaptureOptions>>(),
            services.GetRequiredService<TranscriptDatabase>(),
            services.GetRequiredService<ILogger<TurnCaptureFactory>>(),
            CreateTurnDroppedHandler(services));
    }

    /// <summary>Returns a callback that zeros <c>prompt_text_length</c> for a dropped archive turn, or null.</summary>
    private static Action<Guid>? CreateTurnDroppedHandler(IServiceProvider services)
    {
        var transcripts = services.GetService<ITranscriptStore>();
        if (transcripts is null) return null;
        return transcripts.MarkPromptUnavailableByArchiveTurn;
    }

    /// <summary>
    /// Creates the index tables, builds the store and runs its startup recovery, in that order, so nothing can
    /// append to a store that has not recovered.
    /// </summary>
    /// <param name="services">The provider that supplies the store's dependencies.</param>
    /// <returns>A recovered store.</returns>
    private static SessionStore OpenStore(IServiceProvider services)
    {
        var index = services.GetRequiredService<SessionIndex>();
        index.EnsureCreated();

        var logger = services.GetRequiredService<ILogger<SessionStore>>();
        var store = new SessionStore(
            index,
            services.GetRequiredService<ISessionMasterKeyStore>(),
            SessionStore.FolderBeside(services.GetRequiredService<TranscriptDatabase>().DatabasePath),
            logger);

        var result = store.RecoverOnStartup();
        logger.LogInformation(
            "Session storage recovered: {Truncated} files cut back, {Orphans} orphans deleted, {Dropped} rows without a file removed, {Corrupt} corrupt files left, rotation {Rotation}.",
            result.TruncatedFiles, result.DeletedOrphanFiles, result.DroppedMissingFiles, result.CorruptFiles,
            result.RotationOutcome);
        return store;
    }
}
