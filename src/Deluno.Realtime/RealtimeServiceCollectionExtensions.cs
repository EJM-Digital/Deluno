using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoRealtimeModule(this IServiceCollection services)
    {
        services.AddSignalR();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ActivityHub>("/hubs/activity");
        return endpoints;
    }
}
