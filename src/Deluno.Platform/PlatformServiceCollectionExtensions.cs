using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformSettingsRepository, SqlitePlatformSettingsRepository>();
        services.AddHostedService<PlatformSchemaInitializer>();
        return services;
    }
}
