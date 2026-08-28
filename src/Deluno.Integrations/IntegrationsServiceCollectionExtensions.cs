using Microsoft.Extensions.DependencyInjection;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Processors;
using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;

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
        services.AddHttpClient("processor-connections", client => client.Timeout = TimeSpan.FromSeconds(10));
        // Longer than an indexer's ten seconds on purpose: two of the subtitle
        // providers have no API and are read as HTML, and OpenSubtitles' download
        // is two round trips before a byte of subtitle arrives.
        services.AddHttpClient(
            Subtitles.SubtitleProviderHttp.ClientName,
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IDownloadClient, QbittorrentDownloadClient>();
        services.AddSingleton<IDownloadClient, SabnzbdDownloadClient>();
        services.AddSingleton<IDownloadClient, NzbGetDownloadClient>();
        services.AddSingleton<IDownloadClient, TransmissionDownloadClient>();
        services.AddSingleton<IDownloadClient, DelugeDownloadClient>();
        services.AddSingleton<IDownloadClient, UTorrentDownloadClient>();
        services.AddSingleton<IDownloadClientRegistry, DownloadClientRegistry>();
        services.AddScoped<IDownloadClientTelemetryService, DownloadClientTelemetryService>();
        services.AddScoped<IDownloadClientGrabService, DownloadClientGrabService>();
        services.AddScoped<IDownloadClientWebhookService, DownloadClientWebhookService>();
        services.AddScoped<IProcessorConnectionService, ProcessorConnectionService>();

        // Seven subtitle sources, registered as themselves so the registry can
        // list what Deluno ships without a second table saying so.
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.GestdownSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.PodnapisiSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.OpenSubtitlesSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.SubDlSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.SubSourceSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.Subf2mSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.YifySubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProviderRegistry, Subtitles.SubtitleProviderRegistry>();
        services.AddSingleton<Subtitles.ISubtitleFetchService, Subtitles.SubtitleFetchService>();

        services.AddHttpClient<TmdbMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddHostedService<CacheSchemaInitializer>();
        return services;
    }
}
