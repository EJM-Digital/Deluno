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
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoConnections(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoConnectionsEndpoints();
}
