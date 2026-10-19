using Deluno.Contracts;
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
        services.AddScoped<IIndexerFeedReader, IndexerFeedReader>();

        // Native trackers (#606): the definitions, one engine holding every
        // tracker's cookies and pace, and the indexer-facing side of both.
        services.AddSingleton<Search.Trackers.ITrackerDefinitionCatalog, Search.Trackers.TrackerDefinitionCatalog>();
        // A helper the person runs, for trackers behind a Cloudflare check (#628).
        services.AddHttpClient(Search.Trackers.FlareSolverrCloudflareHelper.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(75));
        services.AddSingleton<Search.Trackers.ICloudflareHelper, Search.Trackers.FlareSolverrCloudflareHelper>();
        services.AddHostedService<Search.Trackers.CloudflareHelperSessionCleanupService>();
        // Scheduled/manual tracker-definition update (#756): off by default
        // (#936). Writes into the same data-folder trackers directory the
        // catalog above already treats as an override.
        services.AddHttpClient(Search.Trackers.GitHubTrackerDefinitionSource.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno tracker-definitions-update/1.0");
        }).AddHttpMessageHandler(() => new Deluno.Infrastructure.Http.GitHubApiConditionalHandler());
        services.AddSingleton<Search.Trackers.IRemoteTrackerDefinitionSource, Search.Trackers.GitHubTrackerDefinitionSource>();
        services.AddSingleton<Search.Trackers.ITrackerDefinitionUpdateStore, Search.Trackers.SqliteTrackerDefinitionUpdateStore>();
        services.AddSingleton<Search.Trackers.ITrackerDefinitionUpdateService, Search.Trackers.TrackerDefinitionUpdateService>();
        services.AddHostedService<Search.Trackers.TrackerDefinitionAutoUpdateHostedService>();
        // Says once, after an upgrade, which indexers moved from Deluno's own
        // site files to the built-in site list (#986).
        services.AddHostedService<Search.Trackers.TrackerSiteSwitchHostedService>();
        services.AddSingleton<Search.Trackers.ITrackerEngine, Search.Trackers.TrackerEngine>();
        services.AddSingleton<Search.Trackers.TrackerIndexers>();
        services.AddSingleton<Deluno.Connections.Contracts.ITrackerIndexerTester>(provider => provider.GetRequiredService<Search.Trackers.TrackerIndexers>());
        services.AddSingleton<Deluno.Connections.Contracts.ITrackerDefinitionNames, Search.Trackers.TrackerDefinitionNames>();
        services.AddScoped<IAcquisitionDecisionPipeline, AcquisitionDecisionPipeline>();
        services.AddHttpClient("indexers", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
        });
        services.AddHttpClient("download-clients", client => client.Timeout = TimeSpan.FromSeconds(8));
        // #824: short and cheap on purpose - this runs before every health
        // pass, and a slow probe URL should never itself stall the pass it
        // is meant to guard.
        services.AddHttpClient(HttpDownloadHealthConnectivityCheck.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<IDownloadHealthConnectivityCheck, HttpDownloadHealthConnectivityCheck>();
        services.AddHttpClient("processor-connections", client => client.Timeout = TimeSpan.FromSeconds(10));
        // The connection check follows redirects itself so it can keep the processor's secret off them (#1064).
        services.AddHttpClient("processor-health", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, AllowAutoRedirect = false });
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
        services.AddSingleton<IDownloadClient, RtorrentDownloadClient>();
        services.AddSingleton<IDownloadClientRegistry, DownloadClientRegistry>();
        // Singleton: it is read from DownloadDispatchPollingService, itself a
        // singleton, and every dependency it takes (the connections repository,
        // the client registry) already is one.
        services.AddSingleton<Deluno.Contracts.IGrabAppearanceLookupService, GrabAppearanceLookupService>();
        services.AddSingleton<LegacyItemIdUpgradeMemory>();
        services.AddScoped<IDownloadClientTelemetryService, DownloadClientTelemetryService>();
        // #1142: the Usenet provider is entered once and written to SABnzbd and NZBGet.
        services.AddScoped<IUsenetClientRetester, StoredUsenetClientRetester>();
        services.AddScoped<UsenetProviderService>();
        services.AddScoped<Deluno.Contracts.IClientHeldFileProbe, ClientHeldFileProbe>();
        // #1081: the last thing the clients said, refreshed in the background, so no screen waits on a live call.
        services.AddSingleton<DownloadTelemetrySnapshotStore>();
        services.AddSingleton<IDownloadTelemetrySnapshotStore>(provider => provider.GetRequiredService<DownloadTelemetrySnapshotStore>());
        services.AddHostedService<DownloadTelemetryRefreshService>();
        services.AddSingleton<OrphanedDownloadCache>();
        services.AddHostedService<OrphanedDownloadRefreshService>();
        services.AddSingleton<SearchQueuePauseMemory>();
        services.AddScoped<ISearchQueuePauseGate, SearchQueuePauseGate>();
        services.AddSingleton<LeftoverScanMemory>();
        services.AddScoped<IOrphanedDownloadService, OrphanedDownloadService>();
        services.AddScoped<ILeftoverDownloadCleanupService, LeftoverDownloadCleanupService>();
        services.AddScoped<IIndexerTorrentFetcher, IndexerTorrentFetcher>();
        services.AddScoped<IDownloadClientGrabService, DownloadClientGrabService>();
        services.AddScoped<IFolderChainService, FolderChainService>();
        services.AddHostedService<DownloadFolderChangeNoticeService>();
        // Scoped, because the telemetry service it drives is. The schedule and
        // the "clean up now" button resolve the same registration.
        services.AddScoped<IRefusedDownloadCleanupService, RefusedDownloadCleanupService>();
        services.AddScoped<IDownloadClientWebhookService, DownloadClientWebhookService>();
        // One memory of "Weir asked Deluno to wait" for every call to it (#1266).
        services.AddSingleton<Deluno.Platform.Contracts.ProcessorRetryHolds>();
        services.AddScoped<IProcessorConnectionService, ProcessorConnectionService>();

        // Plex, Jellyfin and Emby (#493). Thirty seconds because a library
        // listing of a large server is one long page; no retry handler, so a
        // server that is down costs one quiet failure rather than a storm.
        services.AddHttpClient(MediaServers.MediaServerHttp.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<MediaServers.IMediaServerClient, MediaServers.PlexMediaServerClient>();
        services.AddSingleton<MediaServers.IMediaServerClient, MediaServers.JellyfinMediaServerClient>();
        services.AddSingleton<MediaServers.IMediaServerClient, MediaServers.EmbyMediaServerClient>();
        services.AddSingleton<MediaServers.IMediaServerClientRegistry, MediaServers.MediaServerClientRegistry>();
        services.AddSingleton<MediaServers.IMediaServerRefreshNotifier, MediaServers.MediaServerRefreshNotifier>();

        // Jellyseerr and Overseerr (#522), over the media servers' HTTP client.
        services.AddSingleton<Seerr.ISeerrClient, Seerr.SeerrClient>();
        // Trakt OAuth (#846): a device-code sign-in, not RSS scraping, so
        // private lists and Trakt's own built-in Popular/Trending/
        // Anticipated/Top-Watched/Recommended lists work.
        services.AddHttpClient(Trakt.TraktClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<Trakt.ITraktClient, Trakt.TraktClient>();
        services.AddSingleton<Trakt.ITraktTokenProvider, Trakt.TraktTokenProvider>();

        // Six subtitle sources, registered as themselves so the registry can
        // list what Deluno ships without a second table saying so.
        //
        // YifySubtitles is deliberately not here. Weir shipped it against an
        // undocumented `/api?q=` endpoint that now answers with HTML on every
        // host it ever used — checked, not assumed. A provider that can only
        // ever find nothing is worse than one that is absent (DESIGN-002),
        // because its health looks fine and it quietly makes every film search
        // one request slower.
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.GestdownSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.PodnapisiSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.OpenSubtitlesSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.SubDlSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.SubSourceSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.Subf2mSubtitleProvider>();
        // #837: three more from Bazarr's 50+, chosen for real regional and
        // language coverage the first six do not have — Balkan, Chinese and
        // Hungarian, not an arbitrary count.
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.TitloviSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.AssrtSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.SupersubtitlesSubtitleProvider>();
        // #837: the rest of the issue's own named list. Napiprojekt is
        // deliberately not registered — see SubtitleProviderRegistry's own
        // note on why.
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.Addic7edSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProvider, Subtitles.Providers.KtuvitSubtitleProvider>();
        services.AddSingleton<Subtitles.ISubtitleProviderRegistry, Subtitles.SubtitleProviderRegistry>();
        services.AddSingleton<Subtitles.ISubtitleFetchService, Subtitles.SubtitleFetchService>();

        // One health record for the whole process: the typed client below is
        // rebuilt per request, so a failure kept on it never reached /api/metadata/status (#1090).
        services.AddSingleton<MetadataServiceHealth>();
        services.AddSingleton<Deluno.Infrastructure.Resilience.IMetadataServiceReset, MetadataServiceReset>();
        // The per-call limit (about ten seconds) is applied in the provider; this is the
        // backstop above it, which artwork downloads also run under.
        services.AddHttpClient<TmdbMetadataProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddHostedService<CacheSchemaInitializer>();

        // TheXEM (#863): no account, no key — a title's TVDB id is enough.
        services.AddHttpClient(Metadata.SceneMapping.XemSceneMappingProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Deluno.Contracts.ProjectRepositories.DelunoUserAgent);
        });
        services.AddSingleton<Metadata.SceneMapping.ISceneMappingProvider, Metadata.SceneMapping.XemSceneMappingProvider>();
        return services;
    }
}
