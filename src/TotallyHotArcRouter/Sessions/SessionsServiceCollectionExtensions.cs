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
    /// Registers the master key store, the session index and store, the startup recovery service and the
    /// capture writer. The recovery service is registered first, so it starts before the writer and before the
    /// proxy begins serving.
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
        services.AddSingleton(sp => new SessionStore(
            sp.GetRequiredService<SessionIndex>(),
            sp.GetRequiredService<ISessionMasterKeyStore>(),
            SessionStore.FolderBeside(sp.GetRequiredService<TranscriptDatabase>().DatabasePath),
            sp.GetRequiredService<ILogger<SessionStore>>()));

        services.AddHostedService<SessionStoreStartupService>();
        services.AddSingleton<SessionCaptureWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionCaptureWriter>());
        return services;
    }
}
