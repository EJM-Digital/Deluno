using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        return services;
    }
}

