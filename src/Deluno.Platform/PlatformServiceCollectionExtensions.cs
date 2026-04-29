using Deluno.Platform.Data;
using Deluno.Platform.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformSettingsRepository, SqlitePlatformSettingsRepository>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddHostedService<PlatformSchemaInitializer>();
        return services;
    }
}
