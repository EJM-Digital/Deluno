using Deluno.Platform.Data;
using Deluno.Platform.Quality;
using Deluno.Platform.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformSettingsRepository, SqlitePlatformSettingsRepository>();
        services.AddSingleton<IMediaDecisionService, MediaDecisionService>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddHostedService<PlatformSchemaInitializer>();
        return services;
    }
}
