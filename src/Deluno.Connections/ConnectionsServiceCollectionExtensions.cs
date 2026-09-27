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
        // Per-indexer Query/Grab Limit caps (#750), in-memory and per
        // process the same way IOutboundRequestThrottle's pacing is.
        services.AddSingleton<IIndexerCallLimiter, IndexerCallLimiter>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoConnections(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoConnectionsEndpoints();
}
