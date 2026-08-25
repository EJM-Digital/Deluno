using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Deluno.Worker.Intake;
using Deluno.Intake.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoWorkerModule(this IServiceCollection services)
    {
        services.AddHttpClient("deluno-intake", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IntakeSyncService>();
        services.AddScoped<IIntakeSyncService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());
        services.AddScoped<IIntakeListPreviewService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());
        services.AddScoped<IIntakeListApprovalService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());

        services.AddScoped<IJobHandler, MoviesCatalogRefreshJobHandler>();
        services.AddScoped<IJobHandler, SeriesCatalogRefreshJobHandler>();
        services.AddScoped<IJobHandler, IntakeSyncJobHandler>();
        services.AddScoped<IJobHandler, FilesystemImportExecuteJobHandler>();
        services.AddScoped<IJobHandler, LibraryImportExistingJobHandler>();
        services.AddScoped<IJobHandler, MoviesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, SeriesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, MoviesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, SeriesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, EpisodeSearchJobHandler>();
        services.AddScoped<IJobHandler, LibrarySearchJobHandler>();
        services.AddScoped<JobHandlerRegistry>();

        services.AddScoped<WorkPlanner>();
        services.AddHostedService<DelunoHeartbeatWorker>();
        services.AddHostedService<DownloadThroughputSampler>();
        return services;
    }
}
