using Deluno.Infrastructure.Observability;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Deluno.Worker.Intake;
using Deluno.Worker.Seerr;
using Deluno.Intake.Contracts;
using Deluno.Integrations.Seerr;
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
        services.AddScoped<ISeerrAcquisitionService, SeerrAcquisitionService>();

        services.AddScoped<IJobHandler, MoviesCatalogRefreshJobHandler>();
        services.AddScoped<IJobHandler, SeriesCatalogRefreshJobHandler>();
        services.AddScoped<IJobHandler, IntakeSyncJobHandler>();
        services.AddScoped<ProcessorOutcomeReporter>();
        services.AddScoped<RssSyncService>();
        services.AddScoped<IJobHandler, FilesystemImportExecuteJobHandler>();
        services.AddScoped<IJobHandler, LibraryImportExistingJobHandler>();
        services.AddScoped<IJobHandler, LibrarySubtitleScanJobHandler>();
        services.AddScoped<IJobHandler, LibraryMediaProbeJobHandler>();
        services.AddScoped<IJobHandler, LibrarySubtitleSearchJobHandler>();
        services.AddScoped<IJobHandler, SubtitleTitleSearchJobHandler>();
        services.AddScoped<IJobHandler, SubtitleSyncJobHandler>();
        services.AddScoped<IJobHandler, MoviesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, SeriesQualityRecalculateJobHandler>();
        services.AddScoped<IJobHandler, MoviesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, SeriesMetadataRefreshJobHandler>();
        services.AddScoped<IJobHandler, EpisodeSearchJobHandler>();
        services.AddScoped<IJobHandler, SeasonSearchJobHandler>();
        services.AddScoped<IJobHandler, MoviesLibrarySearchJobHandler>();
        services.AddScoped<IJobHandler, MovieCollectionSyncJobHandler>();
        services.AddScoped<IJobHandler, TvLibrarySearchJobHandler>();
        services.AddScoped<IJobHandler, MediaServerRefreshJobHandler>();
        services.AddScoped<IJobHandler, MediaServerRefreshRetryJobHandler>();
        services.AddScoped<IJobHandler, DownloadClientRetryGrabJobHandler>();
        services.AddScoped<JobHandlerRegistry>();

        services.AddScoped<WorkPlanner>();
        services.AddScoped<Deluno.Platform.Contracts.IProcessorFailureReportHandler, ProcessorFailureReportHandler>();
        services.AddScoped<ExternalRedownloadService>();
        services.AddScoped<Deluno.Platform.Contracts.IProcessorOutputImportQueue, ProcessorOutputImportQueue>();
        services.AddHostedService<DelunoHeartbeatWorker>();
        services.AddHostedService<DownloadThroughputSampler>();
        services.AddHostedService<HistoryRetentionWorker>();
        services.AddHostedService<QbittorrentBlocklistSyncService>();
        services.AddHostedService<ContentBlocklistRemoteRefreshService>();

        // One probe for the whole process: rates are measured between calls, so
        // a second instance would reset this one's baseline (#272).
        services.AddSingleton<IMachineProbe, MachineProbe>();
        services.AddHostedService<MachineTelemetrySampler>();
        services.AddHostedService<DownloadProgressPublisher>();

        // #909: every check registers itself here, once, so the runner and the
        // "run right after it changes" scheduler always see the same list.
        services.AddScoped<IHealthCheck, DownloadClientHealthCheck>();
        services.AddScoped<IHealthCheck, ClientDownloadFolderFreeSpaceHealthCheck>();
        services.AddScoped<IHealthCheck, IndexerHealthCheck>();
        services.AddScoped<IHealthCheck, LibraryRootHealthCheck>();
        services.AddScoped<IHealthCheck, LibraryFolderChainHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorHandoffHoldHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorOutputSafetyNetHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorOutputImportIssueHealthCheck>();
        services.AddScoped<IHealthCheck, ExternalServiceHealthCheck>();
        services.AddScoped<IHealthCheck, MetadataProviderHealthCheck>();
        services.AddScoped<IHealthCheck, SystemDiskSpaceHealthCheck>();
        services.AddScoped<IHealthCheck, DispatchRecoveryPollerHealthCheck>();
        services.AddScoped<IProxyReachabilityTester, HttpProxyReachabilityTester>();
        services.AddScoped<IHealthCheck, ProxyHealthCheck>();
        services.AddScoped<IHealthCheck, RemotePathMappingHealthCheck>();
        services.AddScoped<IHealthCheck, SystemTimeHealthCheck>();
        services.AddScoped<IHealthCheck, MediaServerRefreshHealthCheck>();
        services.AddScoped<IHealthCheck, DownloadHardlinkHealthCheck>();
        services.AddScoped<IHealthCheck, AllowedHostsHealthCheck>();
        services.AddHttpClient(Deluno.Worker.HealthChecks.HealthCheckHttpClients.SystemTime, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(Deluno.Worker.HealthChecks.HealthCheckHttpClients.Proxy, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<HealthCheckRunner>();
        services.AddHostedService<HealthCheckHostedService>();
        return services;
    }
}
