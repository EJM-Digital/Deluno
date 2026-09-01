using Deluno.Platform.Data;
using Deluno.Contracts;
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
        services.AddSingleton<IDownloadSharingRepository, SqliteDownloadSharingRepository>();
        services.AddSingleton<IProcessorRepository, SqliteProcessorRepository>();
        services.AddSingleton<IMigrationAuditRepository, SqliteMigrationAuditRepository>();
        services.AddSingleton<IMigrationAssistantService, MigrationAssistantService>();
        services.AddSingleton<IAutomationIdempotencyStore, SqliteAutomationIdempotencyStore>();
        services.AddHostedService<PlatformSchemaInitializer>();
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
