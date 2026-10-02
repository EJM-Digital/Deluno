using Deluno.Connections.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Connections;

public static class ConnectionsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoConnectionsModule(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionsRepository, SqliteConnectionsRepository>();
        // Its own repository rather than fourteen more methods on the one
        // ADR-001 Step 1 has just finished splitting for being too large.
        services.AddSingleton<ISubtitleProviderRepository, SqliteSubtitleProviderRepository>();
        services.AddSingleton<IMediaServerRepository, SqliteMediaServerRepository>();
        services.AddSingleton<IMediaServerRefreshBacklogRepository, SqliteMediaServerRefreshBacklogRepository>();
        services.AddSingleton<ISeerrRepository, SqliteSeerrRepository>();
        services.AddSingleton<ISeerrAcquisitionsRepository, SqliteSeerrAcquisitionsRepository>();
        services.AddSingleton<ITraktConnectionRepository, SqliteTraktConnectionRepository>();
        // Per-indexer Query/Grab Limit caps (#750), in-memory and per
        // process the same way IOutboundRequestThrottle's pacing is.
        services.AddSingleton<IIndexerCallLimiter, IndexerCallLimiter>();
        // One pooled handler per assigned indexer proxy (#976), so an
        // indexer's own search/RSS/caps/grab requests actually go out
        // through the proxy assigned to it.
        services.AddSingleton<IIndexerProxyHttpClientFactory, IndexerProxyHttpClientFactory>();
        // #1117: the newest stable release of each project a connection can be, read from its public feed in the
        // background at most once a day. Weir's feed is added by the companions module, which owns its repository name.
        services.AddHttpClient(Versions.LatestReleaseService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubTagsFeed(Versions.ConnectionProjects.Qbittorrent, "qbittorrent/qBittorrent", "release-"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Transmission, "transmission/transmission"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubTagsFeed(Versions.ConnectionProjects.Deluge, "deluge-torrent/deluge", "deluge-"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Sabnzbd, "sabnzbd/sabnzbd"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Nzbget, "nzbgetcom/nzbget"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.PlexDownloadsFeed(Versions.ConnectionProjects.Plex));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Jellyfin, "jellyfin/jellyfin"));
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Emby, "MediaBrowser/Emby.Releases"));
        // Overseerr and Jellyseerr became one project, Seerr; its releases are the upgrade for both.
        services.AddSingleton<Versions.ReleaseFeed>(new Versions.GitHubReleasesFeed(Versions.ConnectionProjects.Seerr, "seerr-team/seerr"));
        services.AddSingleton<Versions.LatestReleaseService>();
        services.AddSingleton<global::Deluno.Contracts.ILatestReleaseService>(provider => provider.GetRequiredService<Versions.LatestReleaseService>());
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoConnections(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoConnectionsEndpoints();
}
