using Deluno.Infrastructure.Observability;
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
        // The only way the sharing rule can touch a download client: through
        // the client's own action path, never the filesystem (#287, #288).
        services.AddSingleton<Deluno.Recovery.Services.IDownloadClientActionGateway, Services.DownloadClientReclaimGateway>();
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
        services.AddScoped<IJobHandler, LibrarySubtitleScanJobHandler>();
        services.AddScoped<IJobHandler, LibraryMediaProbeJobHandler>();
        services.AddScoped<IJobHandler, LibrarySubtitleSearchJobHandler>();
        services.AddScoped<IJobHandler, SubtitleSyncJobHandler>();
        services.AddScoped<IJobHandler, MoviesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, SeriesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, MoviesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, SeriesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, EpisodeSearchJobHandler>();
        services.AddScoped<IJobHandler, MoviesLibrarySearchJobHandler>();
        services.AddScoped<IJobHandler, MovieCollectionSyncJobHandler>();
        services.AddScoped<IJobHandler, TvLibrarySearchJobHandler>();
        services.AddScoped<JobHandlerRegistry>();

        services.AddScoped<WorkPlanner>();
        services.AddHostedService<DelunoHeartbeatWorker>();
        services.AddHostedService<DownloadThroughputSampler>();

        // One probe for the whole process: rates are measured between calls, so
        // a second instance would reset this one's baseline (#272).
        services.AddSingleton<IMachineProbe, MachineProbe>();
        services.AddHostedService<MachineTelemetrySampler>();
        services.AddHostedService<DownloadProgressPublisher>();
        return services;
    }
}
