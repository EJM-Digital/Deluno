using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Jobs;

public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoJobsModule(this IServiceCollection services)
    {
        return services;
    }
}

