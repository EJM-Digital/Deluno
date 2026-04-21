using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Jobs;

public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoJobsModule(this IServiceCollection services)
    {
        services.AddSingleton<SqliteJobStore>();
        services.AddSingleton<IJobScheduler>(provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddSingleton<IJobQueueRepository>(provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddSingleton<IActivityFeedRepository>(provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddHostedService<JobsSchemaInitializer>();
        return services;
    }
}
