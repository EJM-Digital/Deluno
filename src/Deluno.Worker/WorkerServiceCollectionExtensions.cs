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
    /// <summary>
    /// One background service failing must not stop the whole host (#1080):
    /// the failure is ignored by the host (it is logged), and the monitor
    /// keeps a record Health shows in plain words.
    /// </summary>
    public static IServiceCollection AddDelunoBackgroundServiceSafety(this IServiceCollection services)
    {
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<BackgroundServiceSafetyOptions>(services);
        services.AddSingleton<BackgroundServiceFailureMonitor>();
        services.AddSingleton<Deluno.Contracts.IBackgroundProblemReporter>(provider => provider.GetRequiredService<BackgroundServiceFailureMonitor>());
        services.AddSingleton<IBackgroundServiceFailureState>(provider => provider.GetRequiredService<BackgroundServiceFailureMonitor>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => provider.GetRequiredService<BackgroundServiceFailureMonitor>());
        return services;
    }

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
        Deluno.Worker.ApiParity.CommandEndpoints.AddDelunoCommands(services);
        services.AddScoped<Deluno.Platform.Contracts.IProcessorOutputImportQueue, ProcessorOutputImportQueue>();
        services.AddScoped<Deluno.Platform.Contracts.IGivenUpDownloadRemoval, GivenUpDownloadRemoval>();
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

        // The Dashboard summary (#1082, #1081): one memoized read of stored rows
        // and in-memory readings, and the background pieces that keep those
        // readings fresh so a request never calls a client or stats a disk.
        services.AddSingleton<Deluno.Worker.Services.JobLaneStateBoard>();
        services.AddSingleton<Deluno.Worker.Dashboard.DashboardCountsStore>();
        services.AddSingleton<Deluno.Worker.Dashboard.DashboardVolumeCache>();
        services.AddSingleton<Deluno.Worker.Dashboard.DashboardLiveStore>();
        services.AddSingleton<Deluno.Worker.Dashboard.DashboardLiveService>();
        services.AddHostedService<Deluno.Worker.Dashboard.DashboardVolumeRefresher>();
        services.AddSingleton<Deluno.Worker.Dashboard.DashboardSummaryService>();
        services.AddSingleton<Deluno.Worker.Dashboard.IDashboardSummaryService>(provider => provider.GetRequiredService<Deluno.Worker.Dashboard.DashboardSummaryService>());
        services.AddSingleton<Deluno.Contracts.IDashboardSectionSource>(provider => provider.GetRequiredService<Deluno.Worker.Dashboard.DashboardSummaryService>());
        // What needs a person, built once for the bell and the tray (#1185).
        services.AddSingleton<Deluno.Worker.Attention.IAttentionSummaryService, Deluno.Worker.Attention.AttentionSummaryService>();

        // The System overview (#1098): real readings of the machine, the network and Deluno itself,
        // taken every two seconds only while a screen shows them (see SystemOverviewSampler).
        services.AddSingleton<Deluno.Infrastructure.SystemReadings.IHostReadingSource>(_ => Deluno.Infrastructure.SystemReadings.HostReadingSources.ForThisMachine());
        services.AddSingleton<Deluno.Infrastructure.SystemReadings.HostMetricsReader>();
        services.AddSingleton<Deluno.Infrastructure.SystemReadings.ProcessUsageReader>(provider =>
            new Deluno.Infrastructure.SystemReadings.ProcessUsageReader(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Deluno.Infrastructure.SystemReadings.OverviewHistory>();
        services.AddHttpClient<Deluno.Infrastructure.SystemReadings.INetworkProbe, Deluno.Infrastructure.SystemReadings.NetworkProbe>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<Deluno.Infrastructure.SystemReadings.NetworkChecker>();
        services.AddSingleton<Deluno.Worker.SystemReadings.ISystemOverviewFacts, Deluno.Worker.SystemReadings.SystemOverviewFacts>();
        services.AddSingleton<Deluno.Worker.SystemReadings.SystemOverviewService>();
        services.AddHostedService<Deluno.Worker.SystemReadings.SystemOverviewSampler>();
        services.AddHostedService<Deluno.Worker.SystemReadings.SystemOverviewRecorder>();

        // #909: every check registers itself here, once, so the runner and the
        // "run right after it changes" scheduler always see the same list.
        services.AddScoped<IHealthCheck, DownloadClientHealthCheck>();
        services.AddScoped<IHealthCheck, ClientDownloadFolderFreeSpaceHealthCheck>();
        services.AddScoped<IHealthCheck, IndexerHealthCheck>();
        services.AddScoped<IHealthCheck, TrackerSiteHealthCheck>();
        services.AddScoped<IHealthCheck, LibraryRootHealthCheck>();
        services.AddScoped<IHealthCheck, LibraryFolderChainHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorHandoffHoldHealthCheck>();
        services.AddScoped<Deluno.Worker.Services.ProcessorOutputSweep>();
        services.AddScoped<IHealthCheck, ProcessorOutputSafetyNetHealthCheck>();
        services.AddScoped<IHealthCheck, ProcessorOutputImportIssueHealthCheck>();
        services.AddScoped<IHealthCheck, ExternalServiceHealthCheck>();
        services.AddScoped<IHealthCheck, MetadataProviderHealthCheck>();
        services.AddScoped<IHealthCheck, SystemDiskSpaceHealthCheck>();
        // #1098: the Machine area. Each reports nothing when the computer cannot say.
        services.AddSingleton<Deluno.Worker.HealthChecks.Machine.IMachinePowerReader, Deluno.Worker.HealthChecks.Machine.WindowsMachinePowerReader>();
        services.AddSingleton<Deluno.Worker.HealthChecks.Machine.IMachineDriveHealthReader, Deluno.Worker.HealthChecks.Machine.WindowsMachineDriveHealthReader>();
        services.AddSingleton<Deluno.Worker.HealthChecks.Machine.IMachineRebootReader, Deluno.Worker.HealthChecks.Machine.MachineRebootReader>();
        services.AddSingleton<Deluno.Worker.HealthChecks.Machine.IMachineTemperatureReader, Deluno.Worker.HealthChecks.Machine.MachineTemperatureReader>();
        services.AddScoped<IHealthCheck, ComputerSleepHealthCheck>();
        services.AddScoped<IHealthCheck, DriveHealthHealthCheck>();
        services.AddScoped<IHealthCheck, PendingRebootHealthCheck>();
        services.AddScoped<IHealthCheck, CpuTemperatureHealthCheck>();
        services.AddHostedService<Deluno.Worker.HealthChecks.Machine.KeepAwakeService>();
        services.AddScoped<IHealthCheck, DispatchRecoveryPollerHealthCheck>();
        services.AddScoped<IProxyReachabilityTester, HttpProxyReachabilityTester>();
        services.AddScoped<IHealthCheck, ProxyHealthCheck>();
        services.AddScoped<IHealthCheck, RemotePathMappingHealthCheck>();
        services.AddScoped<IHealthCheck, SystemTimeHealthCheck>();
        services.AddScoped<IHealthCheck, MediaServerRefreshHealthCheck>();
        services.AddScoped<IHealthCheck, DownloadHardlinkHealthCheck>();
        services.AddScoped<IHealthCheck, ClientScriptFailureHealthCheck>();
        services.AddScoped<IHealthCheck, KeepSeedingProblemHealthCheck>();
        services.AddScoped<IHealthCheck, CloudflareHelperHealthCheck>();
        services.AddScoped<IHealthCheck, AllowedHostsHealthCheck>();
        services.AddScoped<IHealthCheck, TagRoutingHealthCheck>();
        services.AddScoped<IHealthCheck, LocalNetworkAuthHealthCheck>();
        services.AddScoped<IHealthCheck, DangerousFileCheckHealthCheck>();
        services.AddScoped<IHealthCheck, SearchQueuePauseHealthCheck>();
        services.AddHttpClient(Deluno.Worker.HealthChecks.HealthCheckHttpClients.SystemTime, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(Deluno.Worker.HealthChecks.HealthCheckHttpClients.Proxy, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<HealthCheckRunner>();
        services.AddHostedService<HealthCheckHostedService>();
        services.AddScoped<Deluno.Contracts.IConnectionHealthTester, Deluno.Worker.Connections.ConnectionHealthTester>();
        services.AddSingleton<Deluno.Media.INoAcceptableReleaseReader, Deluno.Media.SqliteNoAcceptableReleaseReader>();
        services.AddSingleton<Deluno.Media.IHeldReleaseReader, Deluno.Media.SqliteHeldReleaseReader>();
        services.AddHostedService<Deluno.Worker.Connections.RegularConnectionCheckService>();
        services.AddScoped<IHealthCheck, BackgroundServiceHealthCheck>();
        services.AddDelunoBackgroundServiceSafety();
        return services;
    }
}
