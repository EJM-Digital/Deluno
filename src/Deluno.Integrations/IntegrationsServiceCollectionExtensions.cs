using Microsoft.Extensions.DependencyInjection;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Builtin;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;

namespace Deluno.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoIntegrationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IReleaseRankingTrainingDataSource, SqliteReleaseRankingTrainingDataSource>();
        services.AddSingleton<MlNetReleaseRankingModelService>();
        services.AddSingleton<IReleaseRankingModelService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
        services.AddSingleton<IReleaseRankingModelAdminService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
        services.AddHostedService<RankingModelTrainingHostedService>();
        services.AddSingleton<IIntelligentRoutingService, IntelligentRoutingService>();
        services.AddScoped<IMediaSearchPlanner, FeedMediaSearchPlanner>();
        services.AddScoped<IAcquisitionDecisionPipeline, AcquisitionDecisionPipeline>();
        services.AddHttpClient("indexers", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient("download-clients", client => client.Timeout = TimeSpan.FromSeconds(8));
        services.AddScoped<IDownloadClientTelemetryService, DownloadClientTelemetryService>();
        services.AddScoped<IDownloadClientGrabService, DownloadClientGrabService>();
        services.AddScoped<IDownloadClientWebhookService, DownloadClientWebhookService>();

        // Built-in downloader adapters for protocol values "deluno-nzb"
        // and "deluno-torrent". Registered as scoped so they share
        // DbContext lifetime with the services that dispatch into them.
        // BuiltinAdapterDispatcher resolves the right adapter at request
        // time from the registered IEnumerable<IBuiltinDownloaderAdapter>.
        services.AddScoped<IBuiltinDownloaderAdapter, BuiltinNzbAdapter>();
        services.AddScoped<IBuiltinDownloaderAdapter, BuiltinTorrentAdapter>();
        services.AddScoped<BuiltinAdapterDispatcher>();

        // Bridge: Downloader-local IDownloaderSecretProtector → Platform's
        // ISecretProtector. Downloader can't reference Platform directly
        // (boundary rule); this adapter lives in Integrations which
        // legitimately references both.
        services.AddSingleton<Deluno.Downloader.Persistence.IDownloaderSecretProtector,
            DownloaderSecretProtectorAdapter>();

        services.AddHttpClient<TmdbMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddHostedService<CacheSchemaInitializer>();
        return services;
    }
}
