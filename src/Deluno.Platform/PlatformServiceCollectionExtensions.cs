using Deluno.Platform.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Platform.Connectivity;
using Deluno.Platform.Migration;
using Deluno.Security;
using Deluno.Security.Hardening;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformSettingsRepository, SqlitePlatformSettingsRepository>();
        services.AddSingleton<IReleaseProfileRepository, SqliteReleaseProfileRepository>();
        services.AddSingleton<IUnifiedExclusionRepository, SqliteUnifiedExclusionRepository>();
        services.AddSingleton<IRecycleBinService, RecycleBinService>();
        services.AddSingleton<IDownloadHealthRepository, SqliteDownloadHealthRepository>();
        // #1240: what a person chose for keeping a struck private torrent seeding, and the moves that did not work.
        services.AddSingleton<IKeepSeedingStore, SqliteKeepSeedingStore>();
        // #901: shared by every judge that would otherwise strike, blocklist,
        // clean up, escalate a backoff or search again, so an internet outage
        // is never mistaken for a bad release.
        services.AddSingleton<IConnectivityOutageRepository, SqliteConnectivityOutageRepository>();
        services.AddSingleton<IConnectivityMonitor, ConnectivityMonitor>();
        services.AddSingleton<IDownloadSharingRepository, SqliteDownloadSharingRepository>();
        services.AddSingleton<IRssSyncStateRepository, SqliteRssSyncStateRepository>();
        services.AddSingleton<IKeptDownloadsRepository, SqliteKeptDownloadsRepository>();
        services.AddSingleton<IForceImportAttemptsRepository, SqliteForceImportAttemptsRepository>();
        services.AddSingleton<IProcessorRepository, SqliteProcessorRepository>();
        services.AddSingleton<IMigrationAuditRepository, SqliteMigrationAuditRepository>();
        services.AddSingleton<IMigrationAssistantService, MigrationAssistantService>();
        services.AddSingleton<IGuideRuleMigrationService, GuideRuleMigrationService>();
        services.AddSingleton<IAutomationIdempotencyStore, SqliteAutomationIdempotencyStore>();
        services.AddSingleton<IHealthCheckRepository, SqliteHealthCheckRepository>();
        // Same instance behind both interfaces (#820): one small class, one
        // table pair, rather than two repositories that would have to agree
        // with each other about what a queue item's identity is.
        services.AddSingleton<SqliteSharingRulesRepository>();
        services.AddSingleton<ISharingRulesRepository>(sp => sp.GetRequiredService<SqliteSharingRulesRepository>());
        services.AddSingleton<ISharingActivityTracker>(sp => sp.GetRequiredService<SqliteSharingRulesRepository>());
        services.AddSingleton<ITagReferenceStore, SqliteTagReferenceStore>();
        services.AddSingleton<IAutoTagRuleStore, SqliteAutoTagRuleStore>();
        services.AddSingleton<ITitleTagService, TitleTagService>();
        services.AddHostedService<PlatformSchemaInitializer>();
        services.AddHostedService<OutboundThrottleWatcher>();
        return services;
    }

    /// <summary>
    /// Registers the hardened ISecretProtector pipeline. Call this from
    /// the host (which knows the data root). If neither this nor a manual
    /// <c>AddSingleton&lt;ISecretProtector&gt;</c> registration is in place,
    /// other Platform services that depend on <see cref="ISecretProtector"/>
    /// will fail to resolve.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="masterKeyFilePath">
    /// Filesystem path where the AES master key lives when the FileBacked
    /// backend is selected. Typically <c>&lt;dataRoot&gt;/secrets/master.key</c>.
    /// </param>
    public static IServiceCollection AddDelunoPlatformSecrets(
        this IServiceCollection services,
        string masterKeyFilePath)
        => services.AddDelunoSecretsHardening(masterKeyFilePath);
}
