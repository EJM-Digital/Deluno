using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoIntegrationsModule(this IServiceCollection services)
    {
        return services;
    }
}

