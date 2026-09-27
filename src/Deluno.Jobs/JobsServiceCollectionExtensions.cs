using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Jobs;

public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoJobsModule(this IServiceCollection services)
    {
        services.AddSingleton<SqliteDownloadDispatchesRepository>();
        services.AddSingleton<IDownloadDispatchesRepository>(provider =>
            provider.GetRequiredService<SqliteDownloadDispatchesRepository>());
        services.AddSingleton<IBlockedReleaseRepository, SqliteBlockedReleaseRepository>();
        services.AddSingleton<IImportFailureRuleRepository, SqliteImportFailureRuleRepository>();
        services.AddSingleton<SqliteImportResolutionsRepository>();
        services.AddSingleton<IImportResolutionsRepository>(provider =>
            provider.GetRequiredService<SqliteImportResolutionsRepository>());
        services.AddSingleton<SqliteDispatchAlertRepository>();
        services.AddSingleton<IDispatchAlertRepository>(provider =>
            provider.GetRequiredService<SqliteDispatchAlertRepository>());
        services.AddSingleton<SqliteDispatchMetricsRepository>();
        services.AddSingleton<IDispatchMetricsRepository>(provider =>
            provider.GetRequiredService<SqliteDispatchMetricsRepository>());
        services.AddSingleton<SqliteIndexerQueryStatsRepository>();
        services.AddSingleton<IIndexerQueryStatsRepository>(provider =>
            provider.GetRequiredService<SqliteIndexerQueryStatsRepository>());

        services.AddSingleton<SqliteDownloadThroughputRepository>();
        services.AddSingleton<IDownloadThroughputRepository>(provider =>
            provider.GetRequiredService<SqliteDownloadThroughputRepository>());

        services.AddSingleton<SqliteMachineTelemetryRepository>();
        services.AddSingleton<IMachineTelemetryRepository>(provider =>
            provider.GetRequiredService<SqliteMachineTelemetryRepository>());

        services.AddSingleton<IJobLaneSignal, JobLaneSignal>();
        services.TryAddSingleton<IActivityDisplaySettingsStore, FileActivityDisplaySettingsStore>();

        services.AddSingleton<SqliteJobStore>();
        services.AddSingleton<IJobScheduler>(provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddSingleton<IJobQueueRepository>(provider => provider.GetRequiredService<SqliteJobStore>());
        services.AddSingleton<IActivityFeedRepository>(provider => provider.GetRequiredService<SqliteJobStore>());

        services.AddSingleton<DownloadDispatchPollingService>();
        services.AddSingleton<CircuitBreakerDownloadDispatchPollingService>(provider =>
        {
            var inner = provider.GetRequiredService<DownloadDispatchPollingService>();
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CircuitBreakerDownloadDispatchPollingService>>();
            return new CircuitBreakerDownloadDispatchPollingService(inner, logger);
        });
        services.AddSingleton<IDownloadDispatchPollingService>(provider =>
            provider.GetRequiredService<CircuitBreakerDownloadDispatchPollingService>());
        // #909: the health check reads whether the circuit is open from the
        // same singleton the poller uses, rather than a second copy of the
        // state that could disagree with it.
        services.AddSingleton<IDownloadDispatchCircuitBreakerState>(provider =>
            provider.GetRequiredService<CircuitBreakerDownloadDispatchPollingService>());
        services.AddHostedService<JobsSchemaInitializer>();
        services.AddHostedService<DownloadDispatchPollingHostedService>();
        return services;
    }
}
